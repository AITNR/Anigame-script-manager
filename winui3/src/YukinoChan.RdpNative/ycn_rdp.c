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
static void ycn_on_channel_connected(void* ctx, const ChannelConnectedEventArgs* e)
{
	freerdp* instance = (freerdp*)ctx;

	if (!instance || !instance->context || !instance->context->gdi || !e || !e->name)
	{
		return;
	}

	if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
	{
		gdi_graphics_pipeline_init(instance->context->gdi,
		                           (RdpgfxClientContext*)e->pInterface);
		WLog_INFO(TAG, "gfx pipeline: attached to GDI (full-frame mode)");
	}
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

	/* 加载客户端通道（rdpsnd 音频等）。失败不阻断连接，仅记错误 */
	if (!freerdp_client_load_addins(instance->context->channels, instance->context->settings))
		freerdp_set_last_error_log(instance->context, ERRCONNECT_CONNECT_FAILED);

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

	/* GFX 全帧管线：订阅动态通道连接事件（M6）。GFX 通道握手完成后框架把
	 * RdpgfxClientContext 交给我们，接到 GDI 管线后解码帧自动 blit 进
	 * primary_buffer 并走 begin/end_paint —— 帧回调链路零改动 */
	if (s->use_gfx)
	{
		PubSub_SubscribeChannelConnected(instance->context->pubSub, ycn_on_channel_connected);
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
