/* ============================================================
 * YukinoChan.RdpNative — FreeRDP 3.32 桥接层实现
 *
 * M0 踩出的 API 事实全部落实（计划书附录 C）：
 *   1) 进程级一次性初始化：WSAStartup（winpr 3.x 不代劳）、
 *      freerdp_register_addin_provider（内建通道提供器）、
 *      OPENSSL_MODULES = DLL 所在目录（_wputenv_s，legacy → md4 → NTLM）
 *   2) gdi_init 必须在 PostConnect（PreConnect 段错误）
 *   3) ServerPort 是 UINT32
 *   4) Authenticate 弃用 → AuthenticateEx
 *   5) load_addins 在 freerdp/client/cmdline.h
 *   6) rdpsnd 硬依赖 rdpdr（load_addins 自动拉回 DeviceRedirection）
 *   7) NetworkAutoDetect / Multitransport / HeartbeatPdu 必须关
 *      （标准 RDP 安全 + AutoDetect = RC4 解密流错位）
 * ============================================================ */
#include "ycn_rdp.h"

/* WIN32_LEAN_AND_MEAN 必须定义：否则 windows.h 引入 shellapi.h，其 NIIF_* 宏
 * 会与 freerdp rail.h 的 NIIF 枚举冲突（C2059）。spike 已踩过 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <freerdp/freerdp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/error.h>
#include <freerdp/scancode.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/channels.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/channels/drdynvc.h>
#include <freerdp/channels/rdpsnd.h>
#include <freerdp/client/rdpsnd.h>   /* rdpsndDevicePlugin::{SetVolume,...}（本机静音靠链式挂钩它） */

/* FREERDP_ADDIN_CHANNEL_* 的值照抄 freerdp/addin.h。
 * ⚠️ 不能 #include <freerdp/addin.h>：它会拉进 rail.h/shellapi.h，
 * 与 windows.h 的 NIIF_* 宏撞车（C2059，M0 已踩过同类坑）。 */
#define YCN_ADDIN_CHANNEL_STATIC  0x00001000
#define YCN_ADDIN_CHANNEL_ENTRYEX 0x00008000
#include <freerdp/client.h>
#include <winpr/wtypes.h>
#include <winpr/synch.h>
#include <winpr/thread.h>

#include <stdio.h>
#include <string.h>
#include <stdarg.h>

EXTERN_C IMAGE_DOS_HEADER __ImageBase;

/* ---------------- 常量 ---------------- */

/* 并发内嵌会话上限。
 * 多会话通道并行需要同时挂多条连接；8 路对桌面/Server 场景都够用，
 * 每路约 1 个事件循环线程 + 帧缓冲，再高没有实际收益。
 * 改动此处必须同步 C# 侧 RdpNativeLimits.MaxSessions（Models/RdpChannelPlanner.cs）。 */
#define YCN_MAX_SESSIONS 8
#define YCN_ERRBUF_LEN   512
#define TAG "ycn.rdpnative"

/* ---------------- 会话状态 ---------------- */

typedef enum
{
	YCN_STATE_IDLE = 0,
	YCN_STATE_CONNECTING,
	YCN_STATE_RUNNING,
	YCN_STATE_STOPPING
} YcnState;

/* 输入注入请求：send_* 只入队，由事件循环线程消费——
 * 跨线程直接调 freerdp_input_send_* 会破坏协议流（M1 冒烟实测：
 * send_mouse 时刻恰好 BIO_read retries exceeded 掉线） */
typedef struct
{
	uint32_t flags;
	uint16_t x;
	uint16_t y;
} YcnMouseReq;

typedef struct
{
	int down;
	int extended;
	uint16_t scancode;
} YcnKeyReq;

#define YCN_INPUT_QUEUE_CAP 64

typedef struct
{
	int used;                 /* 槽位占用 */
	int id;                   /* 对外 session id（1..YCN_MAX_SESSIONS） */

	freerdp* instance;
	HANDLE thread;
	HANDLE stop_event;
	volatile LONG state;      /* YcnState */
	volatile LONG disconnected_reported; /* on_disconnected 恰好一次 */

	ycn_rdp_callbacks cb;
	void* user;

	/* 帧缓冲（BGRA32，g_lock 保护） */
	uint8_t* frame;
	uint32_t frame_width;
	uint32_t frame_height;
	uint32_t frame_stride;
	BOOL frame_valid;

	char last_error[YCN_ERRBUF_LEN];
	pEndPaint orig_end_paint;   /* gdi 原生 EndPaint（per-session，多会话不冲突） */
	ULONGLONG connected_at;     /* PostConnect 时刻（RefreshRect 延迟基准） */
	int refresh_requested;      /* 全屏刷新请求已发标志（重连会话服务器不主动推帧） */

	/* 输入队列（g_lock 保护；满则丢弃并计错误） */
	YcnMouseReq mouse_q[YCN_INPUT_QUEUE_CAP];
	int mouse_head;
	int mouse_tail;
	YcnKeyReq key_q[YCN_INPUT_QUEUE_CAP];
	int key_head;
	int key_tail;

	/* 连接参数副本（connect 时深拷贝，C# 侧内存随时可释放） */
	char host[256];
	uint16_t port;
	char username[128];
	char domain[128];
	char password[256];
	uint32_t width;
	uint32_t height;
	uint32_t color_depth;
	int use_nla;
	int allow_selfsigned;
	int enable_audio;
	int use_gfx;
	volatile LONG muted;              /* 1 = 本机静音（拦 device 的 Play/PlayEx 丢弃数据 + SetVolume 置 0） */
	/* ⚠️ 一条 RDP 连接里可能同时存在**两个** rdpsnd 插件实例：
	 *   ① 静态通道 `rdpsnd`（VirtualChannelEntryEx 建的那份）
	 *   ② DVC    `AUDIO_PLAYBACK_DVC`（drdynvc 加载 rdpsnd 客户端时另建的 dynamic 实例）
	 * 服务器通常走 ②（AUDIO_PLAYBACK_DVC），①的 OnOpenCalled 恒 0、收不到任何数据。
	 * 所以两个都要跟踪、都要挂钩，否则静音只挂在"哑"的那份上 → 无效。 */
	#define YCN_RDPSND_SLOTS 4
	void* rdpsnd_plugin[YCN_RDPSND_SLOTS];   /* 各 plugin 指针 */
	void* rdpsnd_device[YCN_RDPSND_SLOTS];   /* 各 device 指针（挂钩用；拿不到时 NULL） */
	void* rdpsnd_orig_set_volume[YCN_RDPSND_SLOTS];
	void* rdpsnd_orig_play[YCN_RDPSND_SLOTS];
	void* rdpsnd_orig_play_ex[YCN_RDPSND_SLOTS];
	int rdpsnd_slot_count;                    /* 已发现的 plugin 数 */
	volatile LONG mute_play_hits;     /* 诊断：Play/PlayEx 钩子被调用次数 */
	volatile LONG mute_setvol_hits;   /* 诊断：静音态下 SetVolume 钩子命中次数 */
	volatile LONG mute_dbg_tick;      /* 诊断：hook 快照节拍计数（限流用） */
	volatile LONG mute_dvc_tick;      /* 诊断：DVC 扫描节拍计数（限流用） */
	volatile LONG gfx_state;       /* 0/1/2/3，见 ycn_rdp_gfx_state */
	volatile LONG dvc_connected;   /* 收到的动态通道连接事件累计数 */
	char dvc_names[256];           /* 已连上的通道名（逗号分隔，诊断用） */
	int addins_rc;                 /* freerdp_client_load_addins 返回值（0 成功 / -1 失败 / -2 未调用） */
	int gfx_init_rc;               /* gdi_graphics_pipeline_init 返回值：0 未调用 / 1 成功 / 2 失败 */
	int gfx_codecs_null;           /* 接入后 gfx->codecs 是否为 NULL（-1 未测）：1 = NULL（解码器没准备好） */
	int gfx_progressive_null;      /* gfx->codecs->progressive 是否为 NULL：1 = NULL（GFX 帧处理会崩的嫌疑点） */
} YcnSession;

static YcnSession g_sessions[YCN_MAX_SESSIONS];
static CRITICAL_SECTION g_lock;
static INIT_ONCE g_init_once = INIT_ONCE_STATIC_INIT;
static char g_global_error[YCN_ERRBUF_LEN]; /* session 未找到时的兜底 */

/* ---------------- 工具 ---------------- */

static void set_session_error(YcnSession* s, const char* fmt, ...)
{
	va_list ap;
	if (!s)
		return;
	EnterCriticalSection(&g_lock);
	va_start(ap, fmt);
	_vsnprintf_s(s->last_error, sizeof(s->last_error), _TRUNCATE, fmt, ap);
	va_end(ap);
	LeaveCriticalSection(&g_lock);
}

static void set_global_error(const char* fmt, ...)
{
	va_list ap;
	EnterCriticalSection(&g_lock);
	va_start(ap, fmt);
	_vsnprintf_s(g_global_error, sizeof(g_global_error), _TRUNCATE, fmt, ap);
	va_end(ap);
	LeaveCriticalSection(&g_lock);
}

/* OpenSSL legacy provider（md4 → NTLM/NLA）需要 OPENSSL_MODULES 指向本 DLL 目录。
 * 必须 _wputenv_s：SetEnvironmentVariableW 不更新 UCRT _environ，OpenSSL getenv 读不到 */
static void setup_openssl_modules(void)
{
	wchar_t path[MAX_PATH];
	wchar_t* slash;
	GetModuleFileNameW((HMODULE)&__ImageBase, path, MAX_PATH);
	slash = wcsrchr(path, L'\\');
	if (slash)
		*slash = L'\0';
	_wputenv_s(L"OPENSSL_MODULES", path);
	SetEnvironmentVariableW(L"OPENSSL_MODULES", path);
}

static BOOL CALLBACK init_once_cb(PINIT_ONCE once, PVOID param, PVOID* context)
{
	WSADATA wsa;
	(void)once;
	(void)param;
	(void)context;

	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
	{
		set_global_error("WSAStartup 失败");
	}
	/* 内建静态通道提供器：不注册则 rdpdr/rdpsnd 被当外部 DLL 找而失败 */
	freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0);
	setup_openssl_modules();
	return 0;
}

static BOOL process_init(void)
{
	InitOnceExecuteOnce(&g_init_once, init_once_cb, NULL, NULL);
	return TRUE;
}

static YcnSession* find_session(int id)
{
	if (id < 1 || id > YCN_MAX_SESSIONS)
		return NULL;
	if (!g_sessions[id - 1].used)
		return NULL;
	return &g_sessions[id - 1];
}

/* ---------------- 帧回调链（gdi EndPaint 铩） ---------------- */

static BOOL ycn_end_paint(rdpContext* context)
{
	YcnSession* s = NULL;
	rdpGdi* gdi;
	int i;

	/* 线性查 session（最多 4 个）；先调 gdi 原生 EndPaint 再拷帧 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);

	if (s && s->orig_end_paint && !s->orig_end_paint(context))
		return FALSE;

	if (!s || !s->instance || !s->instance->context || !s->instance->context->gdi)
		return TRUE;

	gdi = s->instance->context->gdi;
	if (!gdi->primary_buffer || gdi->width <= 0 || gdi->height <= 0)
		return TRUE;

	/* 拷贝到会话帧缓冲（BGRA32） */
	EnterCriticalSection(&g_lock);
	{
		uint32_t stride = (uint32_t)gdi->stride > 0 ? (uint32_t)gdi->stride
		                                            : (uint32_t)gdi->width * 4;
		if (s->frame_width != (uint32_t)gdi->width || s->frame_height != (uint32_t)gdi->height ||
		    !s->frame)
		{
			uint8_t* nb = (uint8_t*)realloc(s->frame, (size_t)stride * gdi->height);
			if (nb)
			{
				s->frame = nb;
				s->frame_width = (uint32_t)gdi->width;
				s->frame_height = (uint32_t)gdi->height;
				s->frame_stride = stride;
			}
		}
		if (s->frame)
		{
			memcpy(s->frame, gdi->primary_buffer, (size_t)s->frame_stride * s->frame_height);
			s->frame_valid = TRUE;
		}
	}
	LeaveCriticalSection(&g_lock);

	if (s->cb.on_frame_ready)
		s->cb.on_frame_ready(s->user, s->id, s->frame_width, s->frame_height, s->frame_stride);
	return TRUE;
}

/* ---------------- FreeRDP 回调 ---------------- */

/* GFX 动态通道连上：把 RdpgfxClientContext 接到 GDI 图形管线（M6）。
 * 之后 GFX 解码帧经 gdi_OutputUpdate 自动 blit 进 primary_buffer 并触发
 * begin/end_paint —— ycn_end_paint 钩子照常收帧，且 EndFrame 语义保证整帧。 */
/* 诊断落盘（定义在下面，这里先声明，回调要用） */
static void ycn_debug_dump_gfx(const YcnSession* s, const char* tag);

/* ---- 诊断：GFX 帧到底用的哪个编解码器 ----
 * codecId：0=UNCOMPRESSED 1=AV1 8=CLEARCODEC 9=CAPROGRESSIVE 10=PLANAR
 *          11=AVC420 13=CAPROGRESSIVE_V2 14=AVC444 15=AVC444v2
 * 若日志里出现 11/14/15，而本机 freerdp 是 WITH_GFX_H264=OFF（没有 h264 解码器），
 * 那就是崩溃的直接原因 —— 服务器无视 AVC_DISABLED 强推 AVC。 */
static pcRdpgfxSurfaceCommand g_orig_gfx_surface = NULL;
static volatile LONG g_gfx_last_codec = -1;

/* 链式挂在 gdi 的实现之前：只记账（诊断已确认服务器发 CAPROGRESSIVE + CLEARCODEC），
 * 不再每帧写文件 —— 同步 I/O 会拖垮帧率。 */
static UINT ycn_gfx_surface_command(RdpgfxClientContext* ctx, const RDPGFX_SURFACE_COMMAND* cmd)
{
	if (cmd)
		InterlockedExchange(&g_gfx_last_codec, (LONG)cmd->codecId);
	if (g_orig_gfx_surface)
		return g_orig_gfx_surface(ctx, cmd);
	return CHANNEL_RC_OK;
}

/* ---- 诊断：GFX 各阶段「面包屑」----
 * 崩溃点定位：钩住 ResetGraphics / StartFrame / EndFrame，进入与退出各落一行盘，
 * 崩溃前最后一行就是元凶所在的那一步。 */
static pcRdpgfxResetGraphics g_orig_reset_graphics = NULL;
static pcRdpgfxStartFrame g_orig_start_frame = NULL;
static pcRdpgfxEndFrame g_orig_end_frame = NULL;

static void ycn_debug_dump_tag(const char* tag)
{
	char path[MAX_PATH] = WINPR_C_ARRAY_INIT;
	FILE* f = NULL;
	const char* override = getenv("YCN_DEBUG_TAG");

	if (override && override[0])
	{
		/* 环境变量指定独立路径：多实例/残留进程混写时用来隔离诊断输出 */
		strncpy_s(path, sizeof(path), override, _TRUNCATE);
	}
	else
	{
		const DWORD n = GetTempPathA(MAX_PATH, path);
		if (n == 0 || n >= MAX_PATH)
			return;
		strcat_s(path, sizeof(path), "ycn_gfx_debug.txt");
	}
	if (fopen_s(&f, path, "a") != 0 || !f)
		return;
	fprintf(f, "[tag] %s\n", tag);
	fclose(f);
}

static UINT ycn_gfx_reset_graphics(RdpgfxClientContext* ctx, const RDPGFX_RESET_GRAPHICS_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_reset_graphics)
	{
		/* ⚠️ gdi_ResetGraphics 在本机这份 freerdp 上**必然 AV**（已用面包屑钉死：
		 * 进去就出不来，异常码 0xC0000005）。SEH 兜住之后 GFX 全帧管线照常工作
		 * （实测帧到达 ~37 次/秒、codec = CAPROGRESSIVE + CLEARCODEC、画面正常），
		 * 所以这里先兜底保证可用；治本要等定位到 gdi_ResetGraphics 内部的具体行。 */
		__try
		{
			rc = g_orig_reset_graphics(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_debug_dump_tag("ResetGraphics-AV(0xC0000005) swallowed");
			rc = CHANNEL_RC_OK; /* 假装成功，别让上层直接断链 */
		}
	}
	return rc;
}

static UINT ycn_gfx_start_frame(RdpgfxClientContext* ctx, const RDPGFX_START_FRAME_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_start_frame)
	{
		__try
		{
			rc = g_orig_start_frame(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_debug_dump_tag("StartFrame-AV swallowed");
			rc = CHANNEL_RC_OK;
		}
	}
	return rc;
}

static UINT ycn_gfx_end_frame(RdpgfxClientContext* ctx, const RDPGFX_END_FRAME_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_end_frame)
	{
		__try
		{
			rc = g_orig_end_frame(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_debug_dump_tag("EndFrame-AV swallowed");
			rc = CHANNEL_RC_OK;
		}
	}
	return rc;
}

/* ---- 本机静音（拦截 rdpsnd 设备的 SetVolume，强制置 0）----
 *
 * 语义：让**本机扬声器**不出声，远端照常发声（不是关掉音频通道）。
 *
 * ❌ 走过的弯路：
 *   ① 用 `WLog_Get("com.freerdp.channels.rdpsnd.client")` 反推插件基址 —— 这个名字是
 *      **全局单例**，多会话拿到同一个指针 → 静音一个通道把别人也静了。
 *   ② 用 `ChannelConnectedEventArgs.pInterface` 拿 rdpsndPlugin —— **它是 NULL**！
 *      静态通道的 pInterface 来自 VirtualChannelInitEx 的 clientContext 参数，
 *      而 rdpsnd 传的就是 nullptr（官方 Windows 客户端也只处理 rail/cliprdr/disp 这些 DVC，
 *      从不碰 rdpsnd）。所以早期 attach 一进门就被 `!e->pInterface` 挡掉，静音完全没反应。
 *   ③ 只调一次 SetVolume(0) —— 服务器后续的 volume PDU 会把它盖回去 → 取消静音也不恢复。
 *
 * ✅ 正解（两步）：
 *   A) 怎么拿到 rdpsndPlugin：从 **`context->channels->openDataList[i].lpUserParam`** 拿。
 *      lpUserParam 就是 VirtualChannelInitEx 的 lpUserParam = rdpsnd 插件本身；
 *      按 `stats.channelName == "rdpsnd"` 匹配那一项即可。
 *      `rdpContext.channels` 是**公开字段**；`rdpChannels` / `CHANNEL_OPEN_DATA` 是私有结构，
 *      但布局稳定，偏移由探针实测（真实私有头 offsetof，见下表）。
 *   B) 拿到 device 后**链式挂钩 device->SetVolume**：静音态把传下来的 value 压成 0
 *      再交原函数 —— 无论谁写音量（服务器 PDU、设备 Open 后的 apply_volume、我们自己的
 *      调用）都逃不掉。每个会话各挂各的 device，互不干扰。
 *
 * 探针实测偏移（含私有头 libfreerdp/core/client.h，绑死 vcpkg FreeRDP 3.32.0）：
 *   offsetof(rdpContext, channels)                = 288
 *   offsetof(rdpChannels, openDataCount)          = 1448
 *   offsetof(rdpChannels, openDataList)           = 1456
 *   sizeof(CHANNEL_OPEN_DATA)                     = 112
 *   offsetof(CHANNEL_OPEN_DATA, lpUserParam)      = 88   ← rdpsndPlugin*
 *   offsetof(CHANNEL_OPEN_DATA, stats.channelName) = 0
 *   CHANNEL_MAX_COUNT                             = 30
 * ⚠️ 换 FreeRDP 版本必须重跑探针核对这组常量。 */
#define YCN_CTX_OFF_CHANNELS          288
#define YCN_CH_OFF_OPENDATA_COUNT     1448
#define YCN_CH_OFF_OPENDATA_LIST      1456
#define YCN_CH_MAX_COUNT              30
#define YCN_OD_SIZE                   112
#define YCN_OD_OFF_LPUSERPARAM        88
#define YCN_OD_OFF_STATS_NAME         0

/* 安全读取 openData 的通道名到 out（最多 8 字节 + 强制 NUL）。
 *
 * ❗ 这里**不能**直接 `memcpy(out, od, 8)`：
 * `statsName` 是 FreeRDP 结构体里的定长 8 字节字段，**不保证 NUL 终止**，
 * 而 od 又是按硬编码偏移（YCN_CH_OFF_OPENDATA_LIST / YCN_OD_SIZE）算出来的裸指针 ——
 * 一旦偏移对不上（换 FreeRDP 版本）或该项是野指针，无条件读满 8 字节就会踩到
 * 不可读页，直接 ACCESS_VIOLATION（0xC0000005）把整个进程带走。
 *
 * 现场证据（2026-10-05 09:30，Application 事件 Id=1000）：
 *   出错模块 ycn_rdp.DLL / 异常 0xC0000005 / 偏移 0x2be5
 *   崩在 ycn_collect_rdpsnd_plugins 开头（.pdata 函数 #64，RVA 0x3baf..0x3f91）
 * 触发路径：开始执行 → 两条内嵌通道连上（**音频开着**）→ rdpsnd 扫描读通道名 → 越界。
 *
 * 所以这里用 SEH 包住：读不到就当"没名字"，绝不让诊断代码把进程带崩。
 * 返回 1 = 读到了（含空串）；返回 0 = 读不了。 */
static int ycn_read_channel_name(const uint8_t* od, char* out, size_t out_size)
{
	size_t i;
	if (!od || !out || out_size == 0)
		return 0;
	out[0] = '\0';
	__try
	{
		/* 逐字节最多读 8 个（statsName 的声明长度），遇到 NUL 提前停。
		 * 逐字节读还有个好处：第 8 字节不可读时，第 0..7 字节已读出来，
		 * __except 兜住后仍然算成功。 */
		for (i = 0; i < 8; i++)
		{
			char c = ((const char*)od)[i];
			out[i] = c;
			if (c == '\0')
				break;
		}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		/* 读不动就返回已经读到的部分（至少保证 NUL 终止） */
	}
	if (i >= out_size)
		i = out_size - 1;
	out[i] = '\0';
	return 1;
}

/* DVC 音频通道（AUDIO_PLAYBACK_DVC）的 rdpsnd 插件**不在** context->channels 里，
 * 而在 drdynvc 的 DVCMAN->plugins 列表里。取值链（探针实测，绑死 FreeRDP 3.32.0）：
 *   context->channels               (+288)
 *   -> rdpChannels.drdynvc          (+5328) = DrdynvcClientContext*
 *   -> DrdynvcClientContext.handle  (+0)    = drdynvcPlugin*
 *   -> drdynvc_plugin.channel_mgr   (+192)  = DVCMAN*
 *   -> DVCMAN.plugins               (+56)   = wArrayList*（元素 = IWTSPlugin*，即 rdpsnd DVC 插件）
 * ⚠️ 不能用 DrdynvcClientContext.custom —— 那是 rdpChannels*（client.c 里设的），不是 DVCMAN。 */
#define YCN_CH_OFF_DRDYNVC            5328
#define YCN_DRDYNVC_OFF_HANDLE        0
#define YCN_DRDYNVC_PLUGIN_OFF_CHMGR  192
#define YCN_DVCMAN_OFF_PLUGINS        56

/* rdpsndPlugin 私有结构里的字段偏移（探针实测，绑死 vcpkg FreeRDP 3.32.0） */
#define YCN_RDPSND_OFFSET_OPENDATA_HANDLE 184   /* DWORD OpenHandle */
#define YCN_RDPSND_OFFSET_NUM_CLIENT_FMT  232   /* UINT16 NumberOfClientFormats */
#define YCN_RDPSND_OFFSET_ATTACHED        236   /* BOOL attached */
#define YCN_RDPSND_OFFSET_DYNAMIC         240   /* BOOL dynamic */
#define YCN_RDPSND_OFFSET_ISOPEN          268   /* BOOL isOpen */
#define YCN_RDPSND_OFFSET_DEVICE          312   /* rdpsndDevicePlugin* device */
#define YCN_RDPSND_OFFSET_APPLYVOLUME     404   /* BOOL applyVolume */
#define YCN_RDPSND_OFFSET_ONOPENCALLED    416   /* BOOL OnOpenCalled */
#define YCN_RDPSND_OFFSET_ASYNC           420   /* BOOL async */

/* 诊断：枚举 context->channels 里所有 openData 项的 channelName + lpUserParam。
 * 目的：确认 rdpsnd / AUDIO_PLAYBACK_DVC 到底是同一个 plugin 还是两个。 */
static void ycn_dump_all_channels(rdpContext* context)
{
	uint8_t* channels;
	int count, i;

	if (!context)
		return;
	channels = *(uint8_t**)((uint8_t*)context + YCN_CTX_OFF_CHANNELS);
	if (!channels)
		return;
	count = *(int*)(channels + YCN_CH_OFF_OPENDATA_COUNT);
	if (count <= 0 || count > YCN_CH_MAX_COUNT)
		return;

	for (i = 0; i < count; i++)
	{
		uint8_t* od = channels + YCN_CH_OFF_OPENDATA_LIST + (size_t)i * YCN_OD_SIZE;
		char dbg[256];
		char safe[9];
		void* lp;
		/* 先读 lpUserParam，再读名字 —— 名字的读走 SEH 安全辅助（可能踩不可读页） */
		lp = *(void**)(od + YCN_OD_OFF_LPUSERPARAM);
		if (!ycn_read_channel_name(od + YCN_OD_OFF_STATS_NAME, safe, sizeof(safe)))
			safe[0] = '\0';
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "ch[%d] name='%s' lpUserParam=%p", i, safe, lp);
		ycn_debug_dump_tag(dbg);
	}
}


/* 按 device 指针反查所属会话与槽位（表很小，线性扫足够）。
 * 找到返回会话指针，*slot 写回槽位下标；找不到返回 NULL。 */
static YcnSession* ycn_session_of_device(void* device, int* slot)
{
	YcnSession* s = NULL;
	int i, k;

	if (slot)
		*slot = -1;
	if (!device)
		return NULL;
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS && !s; i++)
	{
		if (!g_sessions[i].used)
			continue;
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			if (g_sessions[i].rdpsnd_device[k] == device)
			{
				s = &g_sessions[i];
				if (slot)
					*slot = k;
				break;
			}
		}
	}
	LeaveCriticalSection(&g_lock);
	return s;
}

/* ---- 静音的两道闸 ----
 *
 * 光靠 SetVolume 不一定够：`waveOutSetVolume` 在现代 Windows（WASAPI 兼容层）上
 * 对某些音频会话不生效，而且**服务器不一定下发 volume PDU**（不下发则 SetVolume
 * 根本不被调用）。所以静音的**主闸是丢弃音频数据**（Play/PlayEx），
 * SetVolume 只作为副闸（让音量指示也归零、并兼容会走 GetVolume 的路径）。
 *
 * 丢弃数据 = 静音时 Play/PlayEx 直接返回 0（不 waveOutWrite）→ 后端无关、必生效。 */

static BOOL ycn_rdpsnd_set_volume_hook(rdpsndDevicePlugin* device, UINT32 value)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s && InterlockedCompareExchange(&s->muted, 0, 0) != 0)
	{
		value = 0; /* 静音态：无论服务器/设备想设多大，一律 0 */
		InterlockedIncrement(&s->mute_setvol_hits);
	}

	if (s && slot >= 0 && s->rdpsnd_orig_set_volume[slot])
		return IFCALLRESULT(FALSE, ((pcSetVolume)s->rdpsnd_orig_set_volume[slot]), device, value);

	return FALSE;
}

/* Play 挂钩：静音时把 PCM 数据清零后再交后端 —— 既静音又保持 rdpsnd 的正常节奏
 * （rdpsnd 每帧要发 WaveConfirm 回执给服务器；直接把数据丢掉虽然也不报错，但
 *  清零重放能保证后端缓冲/时序完全不受影响，兼容性最好）。
 * 用栈上小缓冲分块清，避免为大小不定的音频数据动态分配。 */
static UINT ycn_rdpsnd_play_hook(rdpsndDevicePlugin* device, const BYTE* data, size_t size)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s)
	{
		InterlockedIncrement(&s->mute_play_hits);
		if (InterlockedCompareExchange(&s->muted, 0, 0) != 0 &&
		    slot >= 0 && s->rdpsnd_orig_play[slot])
		{
			/* 静音：原样调用，但喂全零 PCM */
			BYTE zero[512];
			UINT last = 0;
			size_t off = 0;
			memset(zero, 0, sizeof(zero));
			while (off < size)
			{
				size_t chunk = size - off;
				if (chunk > sizeof(zero))
					chunk = sizeof(zero);
				last = ((pcPlay)s->rdpsnd_orig_play[slot])(device, zero, chunk);
				off += chunk;
			}
			return last;
		}
		if (slot >= 0 && s->rdpsnd_orig_play[slot])
			return ((pcPlay)s->rdpsnd_orig_play[slot])(device, data, size);
	}
	return 0;
}

/* PlayEx 挂钩：同上（rdpsnd 优先用 PlayEx，若存在则 Play 不会被调）。 */
static UINT ycn_rdpsnd_play_ex_hook(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format,
                                    const BYTE* data, size_t size)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s)
	{
		InterlockedIncrement(&s->mute_play_hits);
		if (InterlockedCompareExchange(&s->muted, 0, 0) != 0 &&
		    slot >= 0 && s->rdpsnd_orig_play_ex[slot])
		{
			/* 静音：喂全零 PCM（保持格式/节奏不变，只有内容静音） */
			BYTE zero[512];
			UINT last = 0;
			size_t off = 0;
			memset(zero, 0, sizeof(zero));
			while (off < size)
			{
				size_t chunk = size - off;
				if (chunk > sizeof(zero))
					chunk = sizeof(zero);
				last = ((pcPlayEx)s->rdpsnd_orig_play_ex[slot])(device, format, zero, chunk);
				off += chunk;
			}
			return last;
		}
		if (slot >= 0 && s->rdpsnd_orig_play_ex[slot])
			return ((pcPlayEx)s->rdpsnd_orig_play_ex[slot])(device, format, data, size);
	}
	return 0;
}

/* ---------------------------------------------------------------
 * 自验证：完全在原生侧构造一个假的 rdpsndDevicePlugin，走真实钩子链路，
 * 证明「静音时 Play/PlayEx 收到的是全零数据」。不依赖服务器推音频。
 *
 * 做法：
 *   ① 造一个假 plugin（432 字节，device 字段放我们的假 device）；
 *   ② 造假 device，SetVolume/Play/PlayEx 指向**本测试专用的记账函数**；
 *   ③ 把一个空闲会话的 slot0 指向这套假对象（保留原值，测完还原）；
 *   ④ 静音前调 Play → 记账函数应看到"原始数据"；
 *   ⑤ 静音后再调 Play → 记账函数应看到"全零"。
 * 返回：按位标记，全 1 = 全部通过（见下方 bit 定义）。 */
static volatile LONG g_selftest_play_calls;
static volatile LONG g_selftest_zero_seen;
static volatile LONG g_selftest_data_seen;
static BYTE g_selftest_last[64];
static size_t g_selftest_last_size;

static UINT ycn_selftest_play(rdpsndDevicePlugin* device, const BYTE* data, size_t size)
{
	(void)device;
	InterlockedIncrement(&g_selftest_play_calls);
	{
		size_t i;
		BOOL allzero = TRUE;
		size_t n = size < sizeof(g_selftest_last) ? size : sizeof(g_selftest_last);
		for (i = 0; i < size; i++)
		{
			if (data[i] != 0)
			{
				allzero = FALSE;
				break;
			}
		}
		if (allzero)
			InterlockedIncrement(&g_selftest_zero_seen);
		else
			InterlockedIncrement(&g_selftest_data_seen);
		if (n)
			memcpy(g_selftest_last, data, n);
		g_selftest_last_size = n;
	}
	return 0;
}

static UINT ycn_selftest_play_ex(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format,
                                 const BYTE* data, size_t size)
{
	(void)format;
	return ycn_selftest_play(device, data, size);
}

static BOOL ycn_selftest_setvol(rdpsndDevicePlugin* device, UINT32 value)
{
	(void)device;
	(void)value;
	return TRUE;
}

/* 返回位掩码（全 1 = 通过）：
 *   bit0(1) = 能拿到会话与空闲槽位
 *   bit1(2) = 卸载静音时 Play 收到原始数据（非零）
 *   bit2(4) = 静音时 Play 收到全零
 *   bit3(8) = 静音时 Play 依然被调用（节奏不丢，rdpsnd 回执正常）
 *   bit4(16) = 取消静音后恢复原始数据
 */
YCN_API int ycn_rdp_selftest_mute(void)
{
	int result = 0;
	YcnSession* s = NULL;
	int slot = -1;
	int i;
	int saved_used = 0;
	rdpsndDevicePlugin fakeDev;
	uint8_t fakePlugin[432];
	BYTE pcm[128];
	void* saved_plugin;
	void* saved_device;
	void* saved_orig;

	/* 挑一个会话：优先**没在用**的（自检不该干扰真连接）。
	 * ⚠️ 但 ycn_session_of_device 反查时会跳过 !used 的会话 —— 所以自检期间
	 * 必须把 used 临时置 1（下面还原），否则钩子根本认不出这个 device。 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (!g_sessions[i].used)
		{
			s = &g_sessions[i];
			break;
		}
	}
	if (!s)
	{
		/* 全都用着：退而求其次，借用第一个会话的最后一个槽位 */
		for (i = 0; i < YCN_MAX_SESSIONS; i++)
		{
			if (g_sessions[i].used && g_sessions[i].rdpsnd_slot_count < YCN_RDPSND_SLOTS)
			{
				s = &g_sessions[i];
				break;
			}
		}
	}
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return 0;
	}
	saved_used = s->used;
	s->used = 1; /* 让 ycn_session_of_device 能反查到本会话 */

	memset(&fakeDev, 0, sizeof(fakeDev));
	fakeDev.SetVolume = ycn_selftest_setvol;
	fakeDev.Play = ycn_selftest_play;
	fakeDev.PlayEx = ycn_selftest_play_ex;

	memset(fakePlugin, 0, sizeof(fakePlugin));
	/* device 字段（偏移 312）指向假 device —— 与真实布局一致 */
	*(rdpsndDevicePlugin**)(fakePlugin + YCN_RDPSND_OFFSET_DEVICE) = &fakeDev;

	/* 占用一个槽位并记录原值 */
	slot = s->rdpsnd_slot_count;
	if (slot >= YCN_RDPSND_SLOTS)
	{
		LeaveCriticalSection(&g_lock);
		return 0;
	}
	saved_plugin = s->rdpsnd_plugin[slot];
	saved_device = s->rdpsnd_device[slot];
	saved_orig = s->rdpsnd_orig_set_volume[slot];
	s->rdpsnd_plugin[slot] = fakePlugin;
	s->rdpsnd_device[slot] = &fakeDev;
	s->rdpsnd_orig_set_volume[slot] = NULL;
	s->rdpsnd_orig_play[slot] = NULL;
	s->rdpsnd_orig_play_ex[slot] = NULL;
	s->rdpsnd_slot_count = slot + 1;
	InterlockedExchange(&s->muted, 0);
	LeaveCriticalSection(&g_lock);

	result |= 1; /* 拿到槽位 */

	/* 用真实钩子函数装到假 device 上（这就是线上跑的那段代码） */
	{
		HMODULE m = NULL;
		(void)m;
		s->rdpsnd_orig_set_volume[slot] = (void*)fakeDev.SetVolume;
		fakeDev.SetVolume = ycn_rdpsnd_set_volume_hook;
		s->rdpsnd_orig_play[slot] = (void*)fakeDev.Play;
		fakeDev.Play = ycn_rdpsnd_play_hook;
		s->rdpsnd_orig_play_ex[slot] = (void*)fakeDev.PlayEx;
		fakeDev.PlayEx = ycn_rdpsnd_play_ex_hook;
	}

	/* 造一段非零 PCM */
	for (i = 0; i < (int)sizeof(pcm); i++)
		pcm[i] = (BYTE)(i * 7 + 3);

	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;

	/* ① 未静音 → 应收到原始（非零）数据 */
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_data_seen, 0, 0) == 1 &&
	    InterlockedCompareExchange(&g_selftest_zero_seen, 0, 0) == 0)
		result |= 2;

	/* ② 静音 → 应收到全零，且 Play 仍被调用（节奏不丢） */
	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;
	InterlockedExchange(&s->muted, 1);
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_play_calls, 0, 0) >= 1)
		result |= 8;
	if (InterlockedCompareExchange(&g_selftest_zero_seen, 0, 0) == 1)
		result |= 4;

	/* ③ 取消静音 → 恢复原始数据 */
	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;
	InterlockedExchange(&s->muted, 0);
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_data_seen, 0, 0) == 1)
		result |= 16;

	/* 还原槽位（真机运行时这槽本来可能是空的） */
	EnterCriticalSection(&g_lock);
	s->rdpsnd_plugin[slot] = saved_plugin;
	s->rdpsnd_device[slot] = saved_device;
	s->rdpsnd_orig_set_volume[slot] = saved_orig;
	s->rdpsnd_orig_play[slot] = NULL;
	s->rdpsnd_orig_play_ex[slot] = NULL;
	if (!saved_plugin)
		s->rdpsnd_slot_count = slot; /* 原本就是空的：退回 */
	s->used = saved_used;            /* 还原 used（自检期间临时置 1 过） */
	s->muted = 0;
	LeaveCriticalSection(&g_lock);

	return result;
}


/* 判断某指针是否"像"一个 rdpsndPlugin：取它的 device 字段，看 device 的 SetVolume
 * 是否为落在**本模块之外**的有效函数指针。用于在 openData 里认出所有 rdpsnd 插件
 * （静态那份、DVC 另建的那份，注册名都可能是 "rdpsnd"，只能靠形态认）。 */
static rdpsndDevicePlugin* ycn_probe_rdpsnd_device(void* plugin)
{
	rdpsndDevicePlugin* dev;
	HMODULE mod = NULL;

	if (!plugin)
		return NULL;
	__try
	{
		dev = *(rdpsndDevicePlugin**)((uint8_t*)plugin + YCN_RDPSND_OFFSET_DEVICE);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return NULL;
	}
	if (!dev || !dev->SetVolume)
		return NULL;
	/* SetVolume 落在本模块内 = 布局不对/是我们自己的钩子，跳过 */
	if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(uintptr_t)dev->SetVolume, &mod) &&
	    mod == (HMODULE)(uintptr_t)&__ImageBase)
		return NULL;
	return dev;
}

/* 把候选 plugin 收进空闲槽位（已在槽里的跳过）。返回是否新收。 */
static int ycn_add_slot(YcnSession* s, void* plugin, const char* tag_name)
{
	int k;
	if (!plugin)
		return 0;
	for (k = 0; k < s->rdpsnd_slot_count; k++)
	{
		if (s->rdpsnd_plugin[k] == plugin)
			return 0; /* 已在槽里 */
	}
	if (s->rdpsnd_slot_count >= YCN_RDPSND_SLOTS)
		return 0;
	k = s->rdpsnd_slot_count;
	s->rdpsnd_plugin[k] = plugin;
	s->rdpsnd_device[k] = (void*)ycn_probe_rdpsnd_device(plugin);
	s->rdpsnd_orig_set_volume[k] = NULL;
	s->rdpsnd_orig_play[k] = NULL;
	s->rdpsnd_orig_play_ex[k] = NULL;
	s->rdpsnd_slot_count++;
	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute: collect slot%d src='%s' plugin=%p dev=%p", k,
		            tag_name ? tag_name : "?", plugin, s->rdpsnd_device[k]);
		ycn_debug_dump_tag(dbg);
	}
	return 1;
}

/* 扫描并收集本会话所有 rdpsnd 插件，两条来源都扫：
 *   ① context->channels.openData 里的静态 `rdpsnd` 项（lpUserParam = plugin）
 *   ② drdynvc 的 DVCMAN->plugins 列表里 DVC 音频（AUDIO_PLAYBACK_DVC）那份 —— 这个是
 *      **真正在播的**（服务器走 DVC 音频时，静态那份 device 恒 NULL）。
 * 已收集的槽位不重复添加（否则每拍覆盖会丢已装钩子的记录）。
 * 返回槽位总数。 */
static int ycn_collect_rdpsnd_plugins(YcnSession* s, int verbose)
{
	uint8_t* channels;
	uint8_t* drdynvc_ctx;
	uint8_t* dvcman;
	wArrayList* plugins;

	if (!s || !s->instance || !s->instance->context)
		return 0;
	channels = *(uint8_t**)((uint8_t*)s->instance->context + YCN_CTX_OFF_CHANNELS);
	if (!channels)
		return s->rdpsnd_slot_count;

	/* ① 静态 openData 扫描 */
	{
		int count = 0;
		int i;
		/* count 也是从硬编码偏移读的，一起用 SEH 兜住 */
		__try
		{
			count = *(int*)(channels + YCN_CH_OFF_OPENDATA_COUNT);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			return s->rdpsnd_slot_count;
		}
		if (count > 0 && count <= YCN_CH_MAX_COUNT)
		{
		for (i = 0; i < count; i++)
		{
			uint8_t* od = channels + YCN_CH_OFF_OPENDATA_LIST + (size_t)i * YCN_OD_SIZE;
			void* lp = *(void**)(od + YCN_OD_OFF_LPUSERPARAM);
			char safe[9];
			if (!lp)
				continue;
			/* ⚠️ 名字走 SEH 安全读取：原先的 memcpy(safe, name, 8) 会越界读
			 * （statsName 定长 8 字节不保证 NUL 终止，od 又是指针硬算出来的），
			 * 音频一开这条路径就走到，实测直接把进程带崩（0xC0000005 @ ycn_rdp+0x2be5）。 */
			if (!ycn_read_channel_name(od + YCN_OD_OFF_STATS_NAME, safe, sizeof(safe)))
				continue;
			if (!(strstr(safe, "rdpsnd") || strstr(safe, "AUDIO")))
				continue;
			(void)ycn_add_slot(s, lp, safe);
		}
		}
	}

	/* ② DVC 插件列表扫描（AUDIO_PLAYBACK_DVC 的那份）。
	 * verbose = 前几拍才打详细诊断，避免刷爆文件。
	 *
	 * ⚠️ 整条取值链都是**硬编码偏移裸算**：
	 *     context(+288) -> drdynvc(+5328) -> handle(+0) -> channel_mgr(+192) -> plugins(+56)
	 * 中间任何一环在当前 FreeRDP 版本里布局不同 / 指针被释放 / 落在不可读页，
	 * 逐个 `*(ptr + 偏移)` 就是 ACCESS_VIOLATION。诊断代码绝不该把主程序带崩 ——
	 * 所以整段用 __try 包住：扫不到就当"这次没找到"，槽位保持原样继续跑。 */
	__try
	{
	drdynvc_ctx = *(uint8_t**)(channels + YCN_CH_OFF_DRDYNVC);
	if (verbose)
	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: drdynvc_ctx=%p", (void*)drdynvc_ctx);
		ycn_debug_dump_tag(dbg);
	}
	if (drdynvc_ctx)
	{
		uint8_t* dvcplugin = *(uint8_t**)(drdynvc_ctx + YCN_DRDYNVC_OFF_HANDLE);
		if (verbose)
		{
			char dbg[256];
			_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: ctx=%p dvcplugin=%p",
			            (void*)drdynvc_ctx, (void*)dvcplugin);
			ycn_debug_dump_tag(dbg);
		}
		dvcman = dvcplugin ? *(uint8_t**)(dvcplugin + YCN_DRDYNVC_PLUGIN_OFF_CHMGR) : NULL;
		if (verbose)
		{
			char dbg[256];
			_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: dvcman=%p", (void*)dvcman);
			ycn_debug_dump_tag(dbg);
		}
		if (dvcman)
		{
			plugins = *(wArrayList**)(dvcman + YCN_DVCMAN_OFF_PLUGINS);
			if (verbose)
			{
				char dbg[256];
				_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: plugins=%p count=%zu",
				            (void*)plugins, plugins ? ArrayList_Count(plugins) : 0);
				ycn_debug_dump_tag(dbg);
			}
			if (plugins)
			{
				size_t n = ArrayList_Count(plugins);
				size_t j;
				for (j = 0; j < n; j++)
				{
					void* p = ArrayList_GetItem(plugins, j);
					rdpsndDevicePlugin* dv = ycn_probe_rdpsnd_device(p);
					if (verbose)
					{
						char dbg[256];
						_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: item%zu p=%p dev=%p", j, p,
						            (void*)dv);
						ycn_debug_dump_tag(dbg);
					}
					if (dv)
						(void)ycn_add_slot(s, p, "dvc");
				}
			}
		}
	}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		/* DVC 链路上有不可读内存：这次没扫到，但已收的槽位仍然有效。
		 * 记一笔便于日后核对偏移是否还对得上 FreeRDP 当前版本。 */
		if (verbose)
			ycn_debug_dump_tag("mute-dvc: 扫描时踩到不可读内存（偏移可能已不匹配），本轮跳过 DVC 路径");
	}
	return s->rdpsnd_slot_count;
}

/* 对单个槽位的 device 装钩子。返回 1 = 已挂钩/已是最新，0 = 暂不可用，-1 = 校验失败。 */
static int ycn_rdpsnd_hook_slot(YcnSession* s, int slot)
{
	rdpsndDevicePlugin* device = (rdpsndDevicePlugin*)s->rdpsnd_device[slot];
	rdpsndDevicePlugin* dev;
	HMODULE selfMod = NULL;

	if (!s->rdpsnd_plugin[slot])
		return 0;

	/* 每次都从插件里重新读 device —— device 会在 Close→Free 后被换成新对象，
	 * 缓存旧指针会指向已释放内存。 */
	dev = ycn_probe_rdpsnd_device(s->rdpsnd_plugin[slot]);
	if (dev && s->rdpsnd_device[slot] != (void*)dev)
	{
		s->rdpsnd_device[slot] = (void*)dev;
		s->rdpsnd_orig_set_volume[slot] = NULL;
		s->rdpsnd_orig_play[slot] = NULL;
		s->rdpsnd_orig_play_ex[slot] = NULL;
	}
	device = (rdpsndDevicePlugin*)s->rdpsnd_device[slot];
	if (!device || !device->SetVolume)
		return 0;

	/* 已挂钩？只看「当前 device 上的函数指针就是我的钩子」——不掺 orig 缓存，
	 * 否则重扫时 orig 被清就误判成未挂钩 → 反复重装。 */
	if (device->SetVolume == ycn_rdpsnd_set_volume_hook)
		return 1;

	if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(uintptr_t)device->SetVolume, &selfMod) &&
	    selfMod == (HMODULE)(uintptr_t)&__ImageBase)
	{
		return -1;
	}

	/* 存 orig 前先排雷：若当前指针**已经是本模块的钩子**（上轮挂完 device 地址没变、
	 * 但上一轮被判定为"未挂钩"而重进这里），绝不能把它存成 orig —— 那会让钩子
	 * 转调自己 → 无限递归 / 调用野指针（实测出现过 play=000000E400000001 这种垃圾）。
	 * 判据：指针落在本模块内 = 我们的钩子，此时保留旧 orig 不动。 */
	{
		HMODULE m = NULL;
		if (!(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)device->SetVolume, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_set_volume[slot] = (void*)device->SetVolume;
			device->SetVolume = ycn_rdpsnd_set_volume_hook;
		}
		else
		{
			device->SetVolume = ycn_rdpsnd_set_volume_hook;
		}
	}
	{
		HMODULE m = NULL;
		if (device->Play &&
		    !(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)device->Play, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_play[slot] = (void*)device->Play;
			device->Play = ycn_rdpsnd_play_hook;
		}
		else if (device->Play)
		{
			device->Play = ycn_rdpsnd_play_hook;
		}
	}
	{
		HMODULE m = NULL;
		if (device->PlayEx &&
		    !(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)device->PlayEx, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_play_ex[slot] = (void*)device->PlayEx;
			device->PlayEx = ycn_rdpsnd_play_ex_hook;
		}
		else if (device->PlayEx)
		{
			device->PlayEx = ycn_rdpsnd_play_ex_hook;
		}
	}

	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE,
		            "mute: slot%d HOOKED dev=%p play=%p playEx=%p", slot, (void*)device,
		            (void*)s->rdpsnd_orig_play[slot], (void*)s->rdpsnd_orig_play_ex[slot]);
		ycn_debug_dump_tag(dbg);
	}
	WLog_INFO(TAG, "audio mute: slot%d hooked (device=%p)", slot, (void*)device);
	return 1;
}

/* 解析本会话所有 rdpsnd device 并装钩子。可重入（重复调用安全）。
 * 返回已挂钩/已就绪的槽位数。 */
static int ycn_rdpsnd_hook(YcnSession* s)
{
	int hooked = 0;
	int k;
	long tick;

	if (!s)
		return 0;

	/* 每拍重扫一次槽位（plugin/device 都可能出现/更换；数量很少，开销可忽略） */
	tick = InterlockedIncrement(&s->mute_dbg_tick);
	ycn_collect_rdpsnd_plugins(s, tick <= 5);

	if (tick == 3 && s->instance && s->instance->context)
		ycn_dump_all_channels(s->instance->context);

	for (k = 0; k < s->rdpsnd_slot_count; k++)
	{
		int rc = ycn_rdpsnd_hook_slot(s, k);
		if (rc == 1)
			hooked++;

		/* 诊断快照（前几拍 + 之后每 120 拍一次，避免刷爆） */
		if (tick <= 5 || (tick % 120) == 0)
		{
			uint8_t* p = (uint8_t*)s->rdpsnd_plugin[k];
			char dbg[320];
			if (p)
			{
				_snprintf_s(dbg, sizeof(dbg), _TRUNCATE,
				            "mute-snap#%ld slot%d dync=%d att=%d open=%d onopen=%d isopen=%d "
				            "applyv=%d nfmt=%u dev=%p rc=%d",
				            tick, k,
				            (int)*(BOOL*)(p + YCN_RDPSND_OFFSET_DYNAMIC),
				            (int)*(BOOL*)(p + YCN_RDPSND_OFFSET_ATTACHED),
				            (int)*(DWORD*)(p + YCN_RDPSND_OFFSET_OPENDATA_HANDLE),
				            (int)*(BOOL*)(p + YCN_RDPSND_OFFSET_ONOPENCALLED),
				            (int)*(BOOL*)(p + YCN_RDPSND_OFFSET_ISOPEN),
				            (int)*(BOOL*)(p + YCN_RDPSND_OFFSET_APPLYVOLUME),
				            (unsigned)*(UINT16*)(p + YCN_RDPSND_OFFSET_NUM_CLIENT_FMT),
				            s->rdpsnd_device[k], rc);
				ycn_debug_dump_tag(dbg);
			}
		}
	}
	return hooked;
}

/* 通道连上时把插件/设备指针记下来并挂钩。
 * ⚠️ 不能信 e->pInterface（静态通道这里是 NULL），只借 e->name 记个信号，
 * 真正的指针由 ycn_rdpsnd_hook 从 context->channels 里找。 */
static void ycn_rdpsnd_attach(YcnSession* s, const ChannelConnectedEventArgs* e)
{
	if (!s || !e)
		return;

	/* rdpsnd 连上时设备可能还没 Open（音频协商在稍后）→ 这里能挂就挂，
	 * 挂不上也无妨：ycn_rdp_set_muted 会再试，或下次通道事件再试。 */
	(void)ycn_rdpsnd_hook(s);
}

static void ycn_on_channel_connected(void* ctx, const ChannelConnectedEventArgs* e)
{
	/* ⚠️ 第一个参数是 **rdpContext***，不是 freerdp*！
	 * FreeRDP 的触发点：PubSub_OnChannelConnected(pubSub, instance->context, &e)
	 * （libfreerdp/core/client.c，官方客户端的 handler 也把首参命名为 context）。
	 * 早期版本按 freerdp* 解释 → ((freerdp*)ctx)->context 是垃圾指针 →
	 * 找不到 session → 事件全被丢掉 → rdpgfx 永远接不上 GDI（撕裂的真凶之二）。 */
	rdpContext* context = (rdpContext*)ctx;
	YcnSession* s = NULL;
	int i;

	if (!context || !e || !e->name)
	{
		return;
	}

	/* 这里**不能**检查 context->gdi：早期连上的通道（drdynvc 等）可能发生在
	 * PostConnect 的 gdi_init 之前，被 gdi 检查挡掉就什么诊断都看不到。 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	if (s)
	{
		InterlockedIncrement(&s->dvc_connected);
		if (s->dvc_names[0] != '\0' &&
		    strlen(s->dvc_names) + strlen(e->name) + 2 < sizeof(s->dvc_names))
		{
			strcat_s(s->dvc_names, sizeof(s->dvc_names), ",");
		}
		if (strlen(s->dvc_names) + strlen(e->name) + 1 < sizeof(s->dvc_names))
		{
			strcat_s(s->dvc_names, sizeof(s->dvc_names), e->name);
		}
		if (s->gfx_state == 1 && strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) != 0)
		{
			s->gfx_state = 3; /* 通道建了，但没有 GFX 那条 */
		}
	}
	LeaveCriticalSection(&g_lock);

	if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0 && context->gdi)
	{
		RdpgfxClientContext* gfx = (RdpgfxClientContext*)e->pInterface;
		const BOOL ok = gdi_graphics_pipeline_init(context->gdi, gfx);
		WLog_INFO(TAG, "gfx pipeline: attached to GDI (full-frame mode)");
		if (s)
		{
			s->gfx_init_rc = ok ? 1 : 2;
			s->gfx_codecs_null = (gfx && gfx->codecs) ? 0 : 1;
			s->gfx_progressive_null = (gfx && gfx->codecs && gfx->codecs->progressive) ? 0 : 1;
			InterlockedExchange(&s->gfx_state, ok ? 2 : 4);
			ycn_debug_dump_gfx(s, ok ? "attach-ok" : "attach-fail"); /* 崩溃前落盘 */

			if (ok)
			{
				/* 链式挂钩：记录收到的 GFX 帧用的编解码器（判断是不是 AVC） */
				g_orig_gfx_surface = gfx->SurfaceCommand;
				gfx->SurfaceCommand = ycn_gfx_surface_command;

				/* 面包屑：把崩溃点夹到具体某一步 */
				g_orig_reset_graphics = gfx->ResetGraphics;
				gfx->ResetGraphics = ycn_gfx_reset_graphics;
				g_orig_start_frame = gfx->StartFrame;
				gfx->StartFrame = ycn_gfx_start_frame;
				g_orig_end_frame = gfx->EndFrame;
				gfx->EndFrame = ycn_gfx_end_frame;
			}
		}
	}

	/* rdpsnd 连上后：记下设备插件并装 SetVolume 挂钩。
	 * 必须在这里（而不是只在 ycn_rdp_set_muted 里）—— 用户可能先点静音、通道后到；
	 * 重连时设备会重建，也要重新挂。挂钩装上后，静音态会被**自动**持续执行。 */
	if (s && strcmp(e->name, RDPSND_CHANNEL_NAME) == 0)
	{
		ycn_rdpsnd_attach(s, e);
	}
}

/* FreeRDP 的正式通道加载入口（instance->LoadChannels）。**通道必须在这里加载。**
 *
 * 为什么不能像最初那样在 PreConnect 里调 freerdp_client_load_addins：
 *   freerdp_connect_begin() 的顺序是
 *       line 120  IFCALLRET(instance->PreConnect, ...)      ← 我们原来在这里 load_addins
 *       line 136  utils_reload_channels(instance->context)  ← 之后才真正定 channels
 *   而 utils_reload_channels 会把 context->channels **整个 disconnect/close/free 再重建**，
 *   然后回调 instance->LoadChannels 重新加载。
 *   → 在 PreConnect 里加载 = 往一个马上要被释放的对象上加载，重建后一条通道都不剩：
 *     clientDataCount == 0 → freerdp_channels_post_connect 的循环空转 →
 *     没有任何 ChannelConnected 事件 → rdpgfx 永远接不到 GDI →
 *     服务器只能走 legacy 条带更新（实测 ~800 个碎帧/秒）= 画面撕裂的真正来源。
 *
 * 时机正好：本回调在 PreConnect 之后执行，所以 PreConnect 里设的
 * SupportGraphicsPipeline / SupportDynamicChannels 已经生效，
 * freerdp_client_load_addins 才能自行把 rdpgfx（dynamic）与 drdynvc（static）挂上。 */
static BOOL ycn_load_channels(freerdp* instance)
{
	rdpSettings* settings;
	YcnSession* s = NULL;
	int i;
	BOOL wantAudio;

	if (!instance || !instance->context || !instance->context->channels)
	{
		return FALSE;
	}
	settings = instance->context->settings;
	if (!settings)
	{
		return FALSE;
	}

	/* 取本会话的音频开关（用户配置的 rdp.audio_enabled） */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	wantAudio = (s && s->enable_audio) ? TRUE : FALSE;
	LeaveCriticalSection(&g_lock);

	/* 放行 GFX 需要的两条通道：
	 *   rdpgfx  —— load_addins 的 step 1 按 SupportGraphicsPipeline 自行挂为动态通道
	 *   drdynvc —— step 4 按 SupportDynamicChannels 自行加载（DVC 管理器）
	 *
	 * AudioPlayback 按用户配置放行：rdpsnd 一旦挂上，load_addins 的 step 2 会自己把
	 * DeviceRedirection 拉回 TRUE 并加载 rdpdr（rdpsnd 硬依赖它）—— 这是 FreeRDP 的既定
	 * 行为，不是我们能绕过的。早期版本为避免 rdpdr 干扰画面把音频一起关了（那时候 GFX
	 * 还没通、现象分不清），GFX 修好后重新放开，靠自检确认帧率是否仍稳。 */
	freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, wantAudio);
	freerdp_settings_set_bool(settings, FreeRDP_AudioCapture, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);

	/* 关掉 disp（Microsoft::Windows::RDS::DisplayControl，动态分辨率）：
	 * 我们固定 1920×1080，不需要它；多一条通道只会多几次 ResetGraphics 协商，
	 * 而 gdi_ResetGraphics 在本机这份 freerdp 上是要靠 SEH 兜 AV 的（见钩子注释）。 */
	freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, FALSE);

	return freerdp_client_load_addins(instance->context->channels, settings);
}

/* ---- 诊断：把 GFX 接入结果**立刻落盘** ----
 * 为什么不用 last_error / 导出接口：GFX 半初始化会直接 AV 把进程打死，
 * C# 那边 5 秒一拍的日志根本来不及写。写文件是同步的，崩溃前一定落盘。
 * 只在异常分支调用，正常路径零 I/O（否则同步磁盘写会拖垮帧率）。 */
static void ycn_debug_dump_gfx(const YcnSession* s, const char* tag)
{
	char path[MAX_PATH] = WINPR_C_ARRAY_INIT;
	char line[640] = WINPR_C_ARRAY_INIT;
	FILE* f = NULL;

	if (!s)
		return;
	const DWORD n = GetTempPathA(MAX_PATH, path);
	if (n == 0 || n >= MAX_PATH)
		return;
	strcat_s(path, sizeof(path), "ycn_gfx_debug.txt");

	if (fopen_s(&f, path, "a") != 0 || !f)
		return;

	_snprintf_s(line, sizeof(line), _TRUNCATE,
	            "[%s] gfx_init=%d codecs_null=%d prog_null=%d gfx_state=%d dvc=%u static_ch=%u dyn_ch=%u "
	            "load_addins=%d names=[%s]\n",
	            tag, s->gfx_init_rc, s->gfx_codecs_null, s->gfx_progressive_null, (int)s->gfx_state,
	            (unsigned)s->dvc_connected,
	            s->instance && s->instance->context && s->instance->context->settings
	                ? (unsigned)freerdp_settings_get_uint32(s->instance->context->settings,
	                                                         FreeRDP_StaticChannelCount)
	                : 0u,
	            s->instance && s->instance->context && s->instance->context->settings
	                ? (unsigned)freerdp_settings_get_uint32(s->instance->context->settings,
	                                                         FreeRDP_DynamicChannelCount)
	                : 0u,
	            s->addins_rc, s->dvc_names);
	fputs(line, f);
	fclose(f);
}

static BOOL ycn_pre_connect(freerdp* instance)
{
	YcnSession* s = NULL;
	int i;
	rdpSettings* settings = instance->context->settings;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);

	/* 标准 RDP 安全（SecurityLayer=0 服务器）下 AutoDetect PDU 会引发
	 * RC4 解密流错位（invalid packet signature）——三项全关，M0 实测通过 */
	freerdp_settings_set_bool(settings, FreeRDP_NetworkAutoDetect, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportMultitransport, FALSE);

	/* GFX 全帧管线（M6）：服务器在 AVC 被禁时会协商 progressive（内置解码），
	 * EndFrame 语义 = 整帧完成 → 帧回调拿到的永远是完整帧（撕裂根治）。
	 * 解码器缺失时 caps 自动不含 AVC444，无需外部依赖 */
	if (s->use_gfx)
	{
		freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	}
	freerdp_settings_set_uint32(settings, FreeRDP_MultitransportFlags, 0);
	freerdp_settings_set_bool(settings, FreeRDP_SupportHeartbeatPdu, FALSE);

	/* 设备/剪贴板重定向全关：rdpsnd 会自动把 DeviceRedirection 拉回 TRUE，不受影响 */
	freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectDrives, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectPrinters, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectSmartCards, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectSerialPorts, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, FALSE);

	/* ---- 动态虚拟通道：GFX 全帧管线的地基 ----
	 *
	 * ⚠️ 这里**只开开关**，不要调 freerdp_client_load_addins ——
	 * 真正的加载必须放在 instance->LoadChannels（见 ycn_load_channels 的注释：
	 * PreConnect 之后 utils_reload_channels 会重建 channels，在这里加载等于白加载）。
	 *
	 * 也不要自己 add_static/dynamic_channel 去加 "drdynvc"/"rdpgfx"：
	 * load_addins 内部已按 SupportGraphicsPipeline/SupportDynamicChannels 自行处理，
	 * 手动再加一遍会重名失败，进而让 load_addins 提前 return FALSE。 */
	freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);

	/* 通道连接事件就在 PreConnect 订阅（早于 utils_reload_channels / post_connect）。
	 * GFX 全靠它把 RdpgfxClientContext 接到 GDI 图形管线。 */
	if (s)
	{
		int code = 0;
		if (PubSub_SubscribeChannelConnected(instance->context->pubSub,
		                                     ycn_on_channel_connected) >= 0)
		{
			code |= 2;
		}
		if (freerdp_channels_load_static_addin_entry("rdpgfx", NULL, NULL,
		        YCN_ADDIN_CHANNEL_STATIC | YCN_ADDIN_CHANNEL_ENTRYEX))
		{
			code |= 4;
		}
		if (freerdp_channels_load_static_addin_entry("drdynvc", NULL, NULL,
		        YCN_ADDIN_CHANNEL_STATIC | YCN_ADDIN_CHANNEL_ENTRYEX))
		{
			code |= 8;
		}
		s->addins_rc = code;
	}

	/* 强制 slowpath 输入：标准 RDP 安全（SecurityLayer=0 服务器）+ 重连会话场景下，
	 * fastpath 输入 PDU 在连接后 N 秒发出会被服务器判「协议流错误」并断开
	 * （服务器事件日志 #97；M1 冒烟多轮复现，slowpath 实测稳定） */
	freerdp_settings_set_bool(settings, FreeRDP_FastPathInput, FALSE);
	/* 禁用 auto-reconnect cookie：重连已断开会话时服务器用「断线前保存的加密状态」
	 * 校验后续 PDU，而客户端是全新加密状态 → 任何输入 PDU 都被判协议流错误断开
	 * （M1 冒烟：0 延迟注入稳定/N 秒后注入必掉线，服务器日志 #97 实证）。
	 * 禁掉后重连退化为全新登录（AutoLogon），加密状态两端一致 */
	freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, FALSE);
	/* GFX 管道（无 H.264，仅 planar/thin-client caps）：服务器组策略开了
	 * AVC444ModePreferred，会「等待图形子系统」并卡在会话 passthrough 状态，
	 * 期间任何输入 PDU 都触发服务器 RDP_SEC 状态机错误断开（日志 #97/#226）。
	 * 开 GFX 让图形通道能握手完成。首期仍不解码 H.264（GfxH264 关），
	 * 编码由服务器按 caps 回退到 planar */
	freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxH264, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxPlanar, TRUE);
	return TRUE;
}

static BOOL ycn_post_connect(freerdp* instance)
{
	YcnSession* s = NULL;
	int i;
	rdpSettings* settings = instance->context->settings;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	if (!s)
		return FALSE;

	/* gdi_init 必须在 PostConnect（依赖握手后核心创建的 rdpCache） */
	if (!gdi_init(instance, PIXEL_FORMAT_BGRA32))
	{
		set_session_error(s, "gdi_init 失败");
		return FALSE;
	}

	/* 挂 EndPaint 铩：gdi 实现在 gdi_init 时已注册，存原实现先调再拷帧（per-session） */
	s->orig_end_paint = instance->context->update->EndPaint;
	instance->context->update->EndPaint = ycn_end_paint;

	s->connected_at = GetTickCount64();
	s->refresh_requested = 0;

	/* GFX 通道连接事件的订阅已移到 PreConnect（更早，见那里注释）——
	 * 这里只做加载结果自检：通道有没有真的进 channels（诊断位 addins_rc 的 bit4/bit5）。 */
	if (s)
	{
		if (freerdp_channels_get_static_channel_interface(instance->context->channels,
		                                                  DRDYNVC_SVC_CHANNEL_NAME))
		{
			s->addins_rc |= 16;
		}
		if (freerdp_channels_get_static_channel_interface(instance->context->channels,
		                                                  RDPSND_CHANNEL_NAME))
		{
			s->addins_rc |= 32;
		}
	}

	InterlockedExchange(&s->state, YCN_STATE_RUNNING);

	if (s->cb.on_connected)
		s->cb.on_connected(s->user, s->id,
		                   freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth),
		                   freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight));
	return TRUE;
}

static BOOL ycn_authenticate_ex(freerdp* instance, char** username, char** password, char** domain,
                                rdp_auth_reason reason)
{
	YcnSession* s = NULL;
	int i;
	(void)instance;
	(void)reason;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	if (!s)
		return FALSE;

	*username = _strdup(s->username);
	*password = _strdup(s->password);
	*domain = s->domain[0] ? _strdup(s->domain) : NULL;
	return TRUE;
}

static int ycn_verify_x509(freerdp* instance, const BYTE* data, size_t length,
                           const char* hostname, UINT16 port, DWORD flags)
{
	YcnSession* s = NULL;
	int i;
	(void)instance;
	(void)data;
	(void)length;
	(void)hostname;
	(void)port;
	(void)flags;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	/* allow_selfsigned = 放行（正式指纹 UI 是 M6；M0/M1 环回场景一律放行）。
	 * 3.32 语义：返回非零信任该证书 */
	return s ? (s->allow_selfsigned ? 1 : 0) : 0;
}

/* ---------------- 事件循环线程 ---------------- */

static DWORD WINAPI event_loop(LPVOID arg)
{
	YcnSession* s = (YcnSession*)arg;
	freerdp* instance = s->instance;
	int fail_reason = YCN_DISCONNECT_ERROR;
	char detail[YCN_ERRBUF_LEN];

	detail[0] = '\0';

	if (!freerdp_connect(instance))
	{
		UINT32 err = freerdp_get_last_error(instance->context);
		_snprintf_s(detail, sizeof(detail), _TRUNCATE, "connect: %s (0x%08X)",
		            freerdp_get_last_error_string(err), err);
		fail_reason = YCN_DISCONNECT_ERROR;
		set_session_error(s, "%s", detail);
	}
	else
	{
		/* 事件泵：官方句柄就绪驱动模式（同 wfreerdp/spike）。
		 * **必须**等 transport 句柄就绪才调 check_event_handles——
		 * 无数据时定时轮询 check 会污染 client 加密流：之后任何
		 * 输入 PDU 都被服务器判「协议流错误」断开（M1 冒烟 + 服务器
		 * 事件日志 #97 实证，标准 RDP 安全场景） */
		while (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_STOPPING)
		{
			HANDLE handles[64];
			DWORD count = freerdp_get_event_handles(instance->context, handles, 64);
			if (count == 0)
			{
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "event handles unavailable");
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}
			if (count < 64)
				handles[count++] = s->stop_event;

			DWORD st = WaitForMultipleObjects(count, handles, FALSE, 500);
			if (st == WAIT_FAILED)
			{
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "WaitForMultipleObjects failed");
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}

			/* 消费输入队列（必须在本线程发送，见 YcnMouseReq 注释） */
			for (;;)
			{
				YcnMouseReq m;
				YcnKeyReq k;
				int have_m = 0;
				int have_k = 0;
				EnterCriticalSection(&g_lock);
				if (s->mouse_head != s->mouse_tail)
				{
					m = s->mouse_q[s->mouse_tail];
					s->mouse_tail = (s->mouse_tail + 1) % YCN_INPUT_QUEUE_CAP;
					have_m = 1;
				}
				if (s->key_head != s->key_tail)
				{
					k = s->key_q[s->key_tail];
					s->key_tail = (s->key_tail + 1) % YCN_INPUT_QUEUE_CAP;
					have_k = 1;
				}
				LeaveCriticalSection(&g_lock);
				if (have_m)
				{
					BOOL sent = freerdp_input_send_mouse_event(instance->context->input, m.flags, m.x, m.y);
					WLog_DBG(TAG, "input drain: mouse sent=%d flags=0x%04X", sent, m.flags);
					if (!sent)
						WLog_WARN(TAG, "input drain: mouse NOT sent (回调未注册或队列状态异常)");
				}
				if (have_k)
				{
					UINT32 rdp_scancode = MAKE_RDP_SCANCODE(k.scancode, k.extended ? TRUE : FALSE);
					freerdp_input_send_keyboard_event_ex(instance->context->input,
					                                     k.down ? TRUE : FALSE, TRUE, rdp_scancode);
				}
				if (!have_m && !have_k)
					break;
			}

			if (!freerdp_check_event_handles(instance->context))
			{
				UINT32 err = freerdp_get_last_error(instance->context);
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "transport: %s (0x%08X)",
				            freerdp_get_last_error_string(err), err);
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}

			/* rdpsnd 静音挂钩补装：通道连上时设备往往还没 Open（音频协商在稍后），
			 * 所以每拍（500ms）试一次；device 被换掉时也会重挂（函数内部判指针）。
			 * 必须在**本线程**调 —— 这是 RDP 线程，FreeRDP 结构只在这里安全访问。 */
			(void)ycn_rdpsnd_hook(s);

			/* 重连已有会话时，服务器按 RDP 协议假设「客户端仍持有屏幕缓存」，
			 * 不主动重推桌面帧 —— 恢复瞬间最多一帧（常为纯黑），之后画面永远静止（M5 黑屏根因）。
			 * 激活完成约 1 秒后显式请求一次全屏刷新（Refresh Rectangle PDU，MS-RDPBCGR 2.2.11.2.1），
			 * 服务器随后全量重绘，帧流恢复。只发一次。 */
			if (!s->refresh_requested &&
			    GetTickCount64() - s->connected_at >= 1500)
			{
				s->refresh_requested = 1;
				rdpSettings* settings = instance->context->settings;
				UINT16 desktop_w = (UINT16)freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
				UINT16 desktop_h = (UINT16)freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);
				RECTANGLE_16 full = { 0, 0, desktop_w, desktop_h };
				BOOL sent = instance->context->update->RefreshRect(instance->context, 1, &full);
				WLog_INFO(TAG, "refresh rect: requested full %ux%u sent=%d", desktop_w, desktop_h, sent);
				/* 借 last_error 通道把发送结果带回 C# 日志（GUI 进程 WLog 不可见，M5 调试用） */
				set_session_error(s, "refresh-rect %ux%u sent=%d", desktop_w, desktop_h, (int)sent);
			}

			/* 停止信号：stop_event 已在句柄数组里，被触发时上面 WaitFor 返回后
			 * 本拍 check 照常跑一次，下一拍循环条件退出 */
		}
	}

	/* 收尾：只发一次 on_disconnected */
	if (InterlockedCompareExchange(&s->disconnected_reported, 1, 0) == 0)
	{
		if (s->cb.on_disconnected)
			s->cb.on_disconnected(s->user, s->id, fail_reason, detail);
	}

	/* detach：槽位由 disconnect() 的 join 侧释放，这里不碰 s 的实例字段 */
	return 0;
}

/* ---------------- 导出实现 ---------------- */

YCN_API int ycn_rdp_connect(const ycn_rdp_params* params, const ycn_rdp_callbacks* callbacks, void* user)
{
	int slot = -1;
	int id;
	YcnSession* s;
	freerdp* instance;
	rdpSettings* settings;

	if (!params || !params->host || !params->username || !params->password || !callbacks)
	{
		set_global_error("invalid argument: params/callbacks 为空");
		return YCN_ERR_INVALID_ARG;
	}
	process_init();

	EnterCriticalSection(&g_lock);
	for (id = 1; id <= YCN_MAX_SESSIONS; id++)
	{
		if (!g_sessions[id - 1].used)
		{
			slot = id - 1;
			break;
		}
	}
	if (slot < 0)
	{
		LeaveCriticalSection(&g_lock);
		set_global_error("会话表已满（上限 %d）", YCN_MAX_SESSIONS);
		return YCN_ERR_SESSION_FULL;
	}
	s = &g_sessions[slot];
	memset(s->last_error, 0, sizeof(s->last_error));
	LeaveCriticalSection(&g_lock);

	/* 深拷贝参数（C# 侧内存随时可释放） */
	strncpy_s(s->host, sizeof(s->host), params->host, _TRUNCATE);
	s->port = params->port ? params->port : 3389;
	strncpy_s(s->username, sizeof(s->username), params->username, _TRUNCATE);
	strncpy_s(s->domain, sizeof(s->domain), params->domain ? params->domain : "", _TRUNCATE);
	strncpy_s(s->password, sizeof(s->password), params->password, _TRUNCATE);
	s->width = params->desktop_width ? params->desktop_width : 1280;
	s->height = params->desktop_height ? params->desktop_height : 720;
	s->color_depth = params->color_depth ? params->color_depth : 32;
	s->use_nla = params->use_nla;
	s->allow_selfsigned = params->allow_selfsigned;
	s->enable_audio = params->enable_audio;
	s->use_gfx = params->use_gfx;
	InterlockedExchange(&s->muted, 0); /* 新会话默认不静音（C# 侧若记住状态会在连上后立刻设一次） */
	s->gfx_state = params->use_gfx ? 1 : 0;
	s->dvc_connected = 0;
	s->dvc_names[0] = '\0';
	s->addins_rc = -2; /* 尚未调用 load_addins */
	s->gfx_init_rc = 0;
	s->gfx_codecs_null = -1;
	s->gfx_progressive_null = -1;
	s->cb = *callbacks;
	s->user = user;
	s->frame_valid = FALSE;
	s->frame_width = 0;
	s->frame_height = 0;
	s->frame_stride = 0;
	InterlockedExchange(&s->state, YCN_STATE_CONNECTING);
	InterlockedExchange(&s->disconnected_reported, 0);
	s->mouse_head = 0;
	s->mouse_tail = 0;
	s->key_head = 0;
	s->key_tail = 0;

	instance = freerdp_new();
	if (!instance)
	{
		set_session_error(s, "freerdp_new 失败");
		return YCN_ERR_NO_MEMORY;
	}
	s->instance = instance;
	instance->PreConnect = ycn_pre_connect;
	instance->PostConnect = ycn_post_connect;
	/* ⚠️ GFX 全帧管线（instance->LoadChannels）**暂时不挂**，回退到 legacy 更新。
	 *
	 * 已查实的事实链（2026-10-04）：
	 *   1) 挂上它之后通道确实能加载（load_addins 成功、drdynvc 就位），
	 *      服务器也确实切到了 GFX 编码 —— 但帧一帧都进不来（帧到达 0/秒，画面静止）。
	 *   2) gdi_graphics_pipeline_init 内部用 FREERDP_CODEC_ALL 调
	 *      freerdp_client_codecs_prepare；本机 vcpkg 的 freerdp **没编 H.264 解码器**
	 *      （端口无 h264 feature），这一步大概率直接失败 → GFX 接不上 GDI。
	 *   3) 于是"声明了 GFX 能力 + 建了 DVC"反而让服务器不再走 legacy，退化成零帧。
	 *
	 * 结论：要真正打开 GFX，得先在 vcpkg 里重装带 h264 的 freerdp 再编原生层，
	 * 属于独立一步（不是改几行代码能收口的）。在此之前保持通道不加载 =
	 * 服务器走 legacy bitmap 更新（有画面），撕裂由 C# 侧的**锁内整帧拷贝**兜住。
	 *
	 * 【2026-10-04 00:48 验证结果】挂上它之后进程在连接建立数秒内 **AV 崩溃**
	 * （退出码 0xC0000005），不是"解码器为空"这么温和 —— GFX 路径在本机这份
	 * freerdp 上有更硬的兼容问题（大概率就是缺 H.264/AVC 解码器，GFX 帧处理时踩到
	 * 半初始化的编解码上下文）。下次要动这块，先 vcpkg 装 h264 再复测。 */
	/* ⚠️ 这一行是 GFX 全帧管线的**命门**：不挂它 = 通道一条都加载不上（见 ycn_load_channels 注释） */
	instance->LoadChannels = ycn_load_channels;
	instance->VerifyX509Certificate = ycn_verify_x509;
	instance->AuthenticateEx = ycn_authenticate_ex;

	if (!freerdp_context_new(instance))
	{
		set_session_error(s, "freerdp_context_new 失败");
		freerdp_free(instance);
		s->instance = NULL;
		return YCN_ERR_NO_MEMORY;
	}

	settings = instance->context->settings;
	freerdp_settings_set_string(settings, FreeRDP_ServerHostname, s->host);
	freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, (UINT32)s->port);
	freerdp_settings_set_string(settings, FreeRDP_Username, s->username);
	freerdp_settings_set_string(settings, FreeRDP_Password, s->password);
	freerdp_settings_set_string(settings, FreeRDP_Domain, s->domain[0] ? s->domain : NULL);
	freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, s->width);
	freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, s->height);
	freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, s->color_depth);
	freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, s->enable_audio ? TRUE : FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_AudioCapture, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, s->allow_selfsigned ? TRUE : FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_AutoLogonEnabled, TRUE);

	/* 强制不走代理：FreeRDP 3.x 会读 http_proxy/https_proxy 环境变量把 RDP
	 * 塞进 HTTP CONNECT 隧道（日志可见 proxy_parse_uri），环回/局域网直连
	 * 场景代理层会掐断长连接（M1 冒烟：连接 5 秒后 BIO_read retries exceeded） */
	freerdp_settings_set_uint32(settings, FreeRDP_ProxyType, PROXY_TYPE_NONE);

	/* 安全层选择：-1 自动（nego 全开，服务器自选）；0 禁 NLA；1 强制 NLA */
	if (s->use_nla < 0)
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}
	else if (s->use_nla == 0)
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, FALSE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}
	else
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, FALSE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}

	s->stop_event = CreateEventW(NULL, TRUE, FALSE, NULL);
	if (!s->stop_event)
	{
		set_session_error(s, "CreateEvent 失败");
		freerdp_context_free(instance);
		freerdp_free(instance);
		s->instance = NULL;
		return YCN_ERR_NO_MEMORY;
	}

	s->thread = CreateThread(NULL, 0, event_loop, s, 0, NULL);
	if (!s->thread)
	{
		set_session_error(s, "CreateThread 失败");
		CloseHandle(s->stop_event);
		s->stop_event = NULL;
		freerdp_context_free(instance);
		freerdp_free(instance);
		s->instance = NULL;
		return YCN_ERR_THREAD_FAILED;
	}

	s->used = 1;
	return s->id = (int)(slot + 1);
}

YCN_API void ycn_rdp_disconnect(int session)
{
	YcnSession* s;
	HANDLE thread = NULL;
	HANDLE stop = NULL;
	freerdp* instance = NULL;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
	{
		thread = s->thread;
		stop = s->stop_event;
		instance = s->instance;
	}
	LeaveCriticalSection(&g_lock);

	if (!s)
		return;

	/* 已在收尾则不重复 */
	if (InterlockedCompareExchange(&s->state, YCN_STATE_STOPPING, YCN_STATE_RUNNING) == YCN_STATE_STOPPING)
		return;

	if (stop)
		SetEvent(stop);
	/* 主动触发断开：优雅关闭连接（事件循环会退出） */
	if (instance)
		freerdp_disconnect(instance);

	if (thread)
	{
		/* 回调里禁止调 disconnect（死锁保护由 state 标志保证）；
		 * join 前先放开锁——本函数不持锁进来，安全 */
		WaitForSingleObject(thread, 5000);
		CloseHandle(thread);
	}
	if (stop)
		CloseHandle(stop);

	EnterCriticalSection(&g_lock);
	if (s->instance)
	{
		if (s->instance->context)
		{
			gdi_free(s->instance);
			freerdp_context_free(s->instance);
		}
		freerdp_free(s->instance);
		s->instance = NULL;
	}
	free(s->frame);
	s->frame = NULL;
	s->frame_valid = FALSE;
	s->frame_width = 0;
	s->frame_height = 0;
	s->frame_stride = 0;
	s->thread = NULL;
	s->stop_event = NULL;
	/* 静音挂钩随会话一起作废：device 对象已被 FreeRDP 释放，绝不能再解引用。
	 * ⚠️ 必须在上面 freerdp_context_free 之后清 —— 顺序反了会拿到悬垂指针。 */
	{
		int k;
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			s->rdpsnd_device[k] = NULL;
			s->rdpsnd_plugin[k] = NULL;
			s->rdpsnd_orig_set_volume[k] = NULL;
			s->rdpsnd_orig_play[k] = NULL;
			s->rdpsnd_orig_play_ex[k] = NULL;
		}
		s->rdpsnd_slot_count = 0;
	}
	s->mouse_head = 0;
	s->mouse_tail = 0;
	s->key_head = 0;
	s->key_tail = 0;
	InterlockedExchange(&s->state, YCN_STATE_IDLE);
	s->used = 0;
	LeaveCriticalSection(&g_lock);
}

YCN_API int ycn_rdp_send_mouse(int session, uint32_t flags, uint16_t x, uint16_t y)
{
	YcnSession* s;
	int rc;
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	/* 只入队，事件循环线程负责实际发送（跨线程直发会破坏协议流）。
	 * 合并优化：队尾若是纯 MOVE（无按下/释放位），新 MOVE 直接覆盖——
	 * 拖动时 PointerMoved 高频（100Hz+），逐个转发会淹没事件循环 */
	{
		int next = (s->mouse_head + 1) % YCN_INPUT_QUEUE_CAP;
		if (next == s->mouse_tail)
		{
			LeaveCriticalSection(&g_lock);
			return YCN_ERR_NOT_CONNECTED; /* 队列满：丢帧 */
		}
		if (s->mouse_head != s->mouse_tail)
		{
			int tail = s->mouse_tail;
			uint32_t tailFlags = s->mouse_q[tail].flags;
			/* 仅当新旧 flags 完全相同且是移动类（含 MOVE 位、非按下）时覆盖队尾。
			 * 拖拽 MOVE(0x1800) 也合并（RDP 语义允许丢中间点）；滚轮/按下/释放不动 */
			if (tailFlags == flags && (flags & 0x0800u) != 0 && (flags & 0x8000u) == 0)
			{
				s->mouse_q[tail].x = x;
				s->mouse_q[tail].y = y;
				LeaveCriticalSection(&g_lock);
				return YCN_OK;
			}
		}
		s->mouse_q[s->mouse_head].flags = flags;
		s->mouse_q[s->mouse_head].x = x;
		s->mouse_q[s->mouse_head].y = y;
		s->mouse_head = next;
		rc = YCN_OK;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_send_key(int session, int down, int extended, uint16_t scancode)
{
	YcnSession* s;
	int rc;
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	/* 只入队（extended 位在事件循环线程经 MAKE_RDP_SCANCODE 编码） */
	{
		int next = (s->key_head + 1) % YCN_INPUT_QUEUE_CAP;
		if (next == s->key_tail)
		{
			LeaveCriticalSection(&g_lock);
			return YCN_ERR_NOT_CONNECTED; /* 队列满：丢键 */
		}
		s->key_q[s->key_head].down = down;
		s->key_q[s->key_head].extended = extended;
		s->key_q[s->key_head].scancode = scancode;
		s->key_head = next;
		rc = YCN_OK;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

/* 设置本机静音：静音态由 SetVolume 钩子持续执行（服务器下发的 volume PDU 也盖不掉）。
 * 这里只改标志 + 立刻推一次让当前音量即时生效。
 *
 * ⚠️ 线程安全：本函数从 **UI 线程**调用，而钩子/RDP 结构归 **RDP 线程**（event_loop）所有。
 * 所以这里**绝不**改 `device->SetVolume`（那是 event_loop 的活，避免数据竞争）；
 * 只做两件跨线程安全的事：
 *   ① 原子写 `s->muted`（钩子按它决定是否压 0）；
 *   ② 用记录的**原函数指针**直接推一次目标音量（就是 waveOutSetVolume，任何线程可调）。
 * 钩子的装卸交给 event_loop（500ms 一拍 + 通道事件）。
 * rdpsnd 尚未连上/钩子还没装时静默成功（装好后由 event_loop / 下次调用补上）。
 * muted 非 0 = 静音。 */
YCN_API int ycn_rdp_set_muted(int session, int muted)
{
	YcnSession* s;
	void* orig[YCN_RDPSND_SLOTS];
	void* device[YCN_RDPSND_SLOTS];
	int n = 0, k;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	InterlockedExchange(&s->muted, muted ? 1 : 0);
	/* 锁内重新从 plugin 取当前 device（比缓存的新鲜；plugin 为 NULL 则跳过）。
	 * 仍不能完全消除与 RDP 线程的竞争，但这是"副闸"，拿不准就不推 —— 主闸是 Play 钩子。 */
	for (k = 0; k < YCN_RDPSND_SLOTS; k++)
	{
		rdpsndDevicePlugin* d = ycn_probe_rdpsnd_device(s->rdpsnd_plugin[k]);
		orig[k] = s->rdpsnd_orig_set_volume[k];
		device[k] = (void*)d;
		if (d && orig[k])
			n++;
	}
	LeaveCriticalSection(&g_lock);

	/* 立刻推一次目标音量，让「正在播的声音」即时停/响。
	 * 静音 → 0；取消 → 全量 0xFFFFFFFF。*不*动钩子本身。
	 * ⚠️ 这只是副闸；主闸是 Play 钩子（静音时丢数据），它按 s->muted 自动生效。 */
	for (k = 0; k < YCN_RDPSND_SLOTS; k++)
	{
		if (device[k] && orig[k])
		{
			(void)IFCALLRESULT(FALSE, ((pcSetVolume)orig[k]), (rdpsndDevicePlugin*)device[k],
			                   muted ? 0u : 0xFFFFFFFFu);
		}
	}

	/* 诊断：把「设了静音后 Play/SetVolume 钩子各被调了多少次」落盘。
	 * Play 计数不动 = 服务器根本没推音频（或钩子没挂在这条路径上）；
	 * Play 计数在涨但还有声音 = 静音闸没生效。
	 * hooked 列 = 当前 device->Play/SetVolume 是否就是我们的钩子（静音能否生效的前提）。*/
	{
		char buf[320];
		int hooked[YCN_RDPSND_SLOTS];
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			rdpsndDevicePlugin* d = (rdpsndDevicePlugin*)device[k];
			hooked[k] = (d && d->SetVolume == ycn_rdpsnd_set_volume_hook) ? 1 : 0;
		}
		_snprintf_s(buf, sizeof(buf), _TRUNCATE,
		            "mute: set=%d slots=%d dev0=%p dev1=%p hooked0=%d hooked1=%d "
		            "play_hits=%ld setvol_hits=%ld",
		            muted, n, device[0], device[1], hooked[0], hooked[1],
		            (long)InterlockedCompareExchange(&s->mute_play_hits, 0, 0),
		            (long)InterlockedCompareExchange(&s->mute_setvol_hits, 0, 0));
		ycn_debug_dump_tag(buf);
	}
	return YCN_OK;
}

/* 查询当前静音标志（1 = 静音）。仅用于自检与 C# 侧状态对齐。 */
YCN_API int ycn_rdp_get_muted(int session)
{
	YcnSession* s;
	int muted;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	muted = (int)InterlockedCompareExchange(&s->muted, 0, 0);
	LeaveCriticalSection(&g_lock);
	return muted;
}

YCN_API int ycn_rdp_grab_frame(int session, uint32_t* out_width, uint32_t* out_height,
                               uint32_t* out_stride, const uint8_t** out_data)
{
	YcnSession* s;
	int rc;
	if (!out_width || !out_height || !out_stride || !out_data)
		return YCN_ERR_INVALID_ARG;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (s->frame_valid && s->frame)
	{
		*out_width = s->frame_width;
		*out_height = s->frame_height;
		*out_stride = s->frame_stride;
		*out_data = s->frame;
		rc = YCN_OK;
	}
	else
	{
		rc = YCN_ERR_NOT_CONNECTED;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_copy_frame(int session, uint8_t* dst, uint32_t dst_size,
                               uint32_t* out_width, uint32_t* out_height, uint32_t* out_stride)
{
	YcnSession* s;
	int rc = YCN_ERR_NOT_CONNECTED;
	size_t need;

	if (!dst || dst_size == 0)
		return YCN_ERR_INVALID_ARG;

	/* 拷贝全程持锁：与 ycn_end_paint 里「整帧 memcpy 进 s->frame」互斥，
	 * 因此绝不会读到拷贝到一半的帧（撕裂安全）。 */
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s && s->frame_valid && s->frame && s->frame_height > 0)
	{
		if (out_width)
			*out_width = s->frame_width;
		if (out_height)
			*out_height = s->frame_height;
		if (out_stride)
			*out_stride = s->frame_stride;

		need = (size_t)s->frame_stride * s->frame_height;
		if (need <= (size_t)dst_size)
		{
			memcpy(dst, s->frame, need);
			rc = YCN_OK;
		}
		else
		{
			rc = YCN_ERR_NO_MEMORY; /* 缓冲过小：调用方按 out_* 尺寸重分配后重试 */
		}
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_gfx_state(int session, uint32_t* out_dvc_count)
{
	YcnSession* s;
	int st = -1;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
	{
		st = (int)s->gfx_state;
		if (out_dvc_count)
			*out_dvc_count = (uint32_t)s->dvc_connected;
	}
	LeaveCriticalSection(&g_lock);
	return st;
}

YCN_API void ycn_rdp_dvc_names(int session, char* buf, size_t buflen)
{
	YcnSession* s;

	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
		strncpy_s(buf, buflen, s->dvc_names, _TRUNCATE);
	LeaveCriticalSection(&g_lock);
}

YCN_API void ycn_rdp_diag(int session, char* buf, size_t buflen)
{
	YcnSession* s;
	rdpSettings* settings = NULL;

	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s && s->instance && s->instance->context)
		settings = s->instance->context->settings;
	if (s && settings)
	{
		_snprintf_s(buf, buflen, _TRUNCATE,
		            "load_addins=%d static_ch=%u dyn_ch=%u gfx_state=%d dvc=%u gfx_init=%d codecs_null=%d names=[%s]",
		            s->addins_rc,
		            (unsigned)freerdp_settings_get_uint32(settings, FreeRDP_StaticChannelCount),
		            (unsigned)freerdp_settings_get_uint32(settings, FreeRDP_DynamicChannelCount),
		            (int)s->gfx_state, (unsigned)s->dvc_connected,
		            s->gfx_init_rc, s->gfx_codecs_null, s->dvc_names);
	}
	LeaveCriticalSection(&g_lock);
}

YCN_API void ycn_rdp_last_error(int session, char* buf, size_t buflen)
{
	const char* src = g_global_error;
	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';
	EnterCriticalSection(&g_lock);
	if (session > 0)
	{
		YcnSession* s = find_session(session);
		if (s && s->last_error[0])
			src = s->last_error;
	}
	if (!src || !src[0])
		src = "no error";
	strncpy_s(buf, buflen, src, _TRUNCATE);
	LeaveCriticalSection(&g_lock);
}

YCN_API const char* ycn_rdp_version(void)
{
	return YCN_RDP_VERSION_STRING;
}

/* ---------------- DLL 生命周期 ---------------- */

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
	(void)hinst;
	(void)reserved;
	if (reason == DLL_PROCESS_ATTACH)
	{
		InitializeCriticalSectionAndSpinCount(&g_lock, 400);
	}
	else if (reason == DLL_PROCESS_DETACH)
	{
		DeleteCriticalSection(&g_lock);
	}
	return TRUE;
}
