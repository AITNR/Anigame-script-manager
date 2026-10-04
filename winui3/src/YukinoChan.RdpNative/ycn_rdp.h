/* ============================================================
 * YukinoChan.RdpNative — C ABI 头（计划书 §4，API 冻结）
 *
 * 冻结约定（§4.2）：本文件的导出函数签名冻结后不加不改；
 * C# 侧 Services/RdpNativeInterop.cs 的 DllImport 声明与本文件
 * 由 _smoke 的「RdpNativeAbiCheck」做字符串级一致性断言。
 *
 * 线程模型（C# 侧必须遵守）：
 *   - on_* 回调全部在原生事件循环线程触发（不是 UI 线程），
 *     C# 侧自行调度回 DispatcherQueue；
 *   - on_frame_ready 只是「有新帧」通知，数据要立刻 ycn_rdp_grab_frame
 *     拷走——下一次 grab / disconnect 后旧指针失效；
 *   - 回调里**禁止**调用 ycn_rdp_disconnect（会死锁），如需断开
 *     请投递回自己的调度线程再调。
 *
 * 构建：vcpkg FreeRDP 3.32.0（C:\Users\AITNR\WorkBuddy\_tools\vcpkg），
 * 详见 rdp-embed-freerdp-plan.md 附录 C。
 * ============================================================ */
#ifndef YCN_RDP_H
#define YCN_RDP_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef YCN_RDP_EXPORTS
#define YCN_API __declspec(dllexport)
#else
#define YCN_API __declspec(dllimport)
#endif

#define YCN_RDP_VERSION_STRING "0.1.0-m1"

/* ---- 返回码（函数返回 int；负值为错误） ---- */
#define YCN_OK                     0
#define YCN_ERR_INVALID_ARG     (-1)   /* 参数为空/非法 */
#define YCN_ERR_NO_MEMORY       (-2)
#define YCN_ERR_SESSION_FULL    (-3)   /* 会话表已满（上限 4） */
#define YCN_ERR_SESSION_NOT_FOUND (-4)
#define YCN_ERR_ALREADY_CONNECTED (-5) /* 同一实例重复 connect */
#define YCN_ERR_CONNECT_FAILED  (-6)   /* freerdp_connect 失败（细节看 last_error） */
#define YCN_ERR_THREAD_FAILED   (-7)
#define YCN_ERR_NOT_CONNECTED   (-8)   /* 会话未就绪，操作被拒 */

/* ---- on_disconnected 的 reason ---- */
#define YCN_DISCONNECT_LOCAL    1      /* 本端主动 ycn_rdp_disconnect */
#define YCN_DISCONNECT_REMOTE   2      /* 服务器/网络侧断开 */
#define YCN_DISCONNECT_ERROR    3      /* 协议层错误导致断开 */

/* ---- 连接参数（§4.1 平铺布局，C# StructLayout.Sequential 对齐） ---- */
typedef struct ycn_rdp_params
{
    const char* host;              /* "127.0.0.1"；不带端口 */
    uint16_t    port;              /* 0 = 3389 */
    const char* username;
    const char* domain;            /* 可 NULL */
    const char* password;          /* 来自 RdpCredentialStore.CredRead */
    uint32_t    desktop_width;     /* 0 = 1280 */
    uint32_t    desktop_height;    /* 0 = 720 */
    uint32_t    color_depth;       /* 0 = 32 */
    int         use_nla;           /* -1 自动 / 0 禁 / 1 强制 */
    int         allow_selfsigned;  /* 1 = 放行自签证书（环回场景；指纹 UI 是 M6） */
    int         enable_audio;      /* 1 = 远端声音在本机播放（rdpsnd 原生侧直放） */
    int         use_gfx;           /* 1 = 启用 GFX 全帧管线（撕裂根治；服务器自动协商 progressive/AVC） */
} ycn_rdp_params;

/* ---- 回调表（全部可为 NULL；事件不丢，见计划书 §4「事件不会丢」） ---- */
typedef struct ycn_rdp_callbacks
{
    /* 连接建立（能力协商完成，画面尺寸已定） */
    void (*on_connected)(void* user, int session, uint32_t width, uint32_t height);
    /* 有新帧可取（高频；数据通过 ycn_rdp_grab_frame 拷贝） */
    void (*on_frame_ready)(void* user, int session, uint32_t width, uint32_t height, uint32_t stride);
    /* 远端桌面分辨率变更 */
    void (*on_desktop_resize)(void* user, int session, uint32_t width, uint32_t height);
    /* 断开（每会话恰好一次；主动/被动/错误共用，用 reason 区分） */
    void (*on_disconnected)(void* user, int session, int reason, const char* detail);
    /* 非致命错误通报（会话可能继续存活） */
    void (*on_error)(void* user, int session, int code, const char* message);
} ycn_rdp_callbacks;

/* ---- 导出函数（§4.2 冻结）。YCN_API 置于类型前（避免 declarator 位置歧义） ---- */

/* 建立会话：立即返回，连接在后台线程进行；成功返回 session id(>0)，失败返回负错误码 */
YCN_API int ycn_rdp_connect(const ycn_rdp_params* params, const ycn_rdp_callbacks* callbacks, void* user);

/* 断开会话（语义 = mstsc「断开连接」：会话保留、进程存活）。幂等；未知 session 静默返回 */
YCN_API void ycn_rdp_disconnect(int session);

/* 输入注入。flags = FreeRDP PTR_FLAGS_*（C# 侧定义同名常量） */
YCN_API int ycn_rdp_send_mouse(int session, uint32_t flags, uint16_t x, uint16_t y);

/* 键盘注入。scancode 为 RDP 扫描码（VSC），extended 为扩展键位 */
YCN_API int ycn_rdp_send_key(int session, int down, int extended, uint16_t scancode);

/* 本机静音（只影响本机播放，远端照常发声）。muted 非 0 = 静音。
 * 立即生效、不需要重连：内部对 rdpsnd 设备的 SetVolume 做**链式挂钩**，
 * 静音态下任何音量写入（含服务器下发的 volume PDU）都会被压成 0；取消静音则摘钩子并还原全量。
 * rdpsnd 通道尚未连上时静默成功 —— 连上后会自动装钩子（见 ycn_on_channel_connected）。 */
YCN_API int ycn_rdp_set_muted(int session, int muted);

/* 查询本机静音标志（1 = 静音；负值为错误码）。供自检与 C# 侧状态对齐。 */
YCN_API int ycn_rdp_get_muted(int session);

/* 取当前帧。成功返回 YCN_OK；out_data 有效期到下一次 grab / disconnect。
 * stride 单位字节，像素格式 BGRA32（与 D3D11 B8G8R8A8 / SoftwareBitmap Bgra8 对齐）
 *
 * ⚠️ 只返回内部缓冲的裸指针：读者在**锁外**读取，与 end_paint 的 memcpy 写入存在竞态，
 * 高帧率动画下会读到「上下半不同帧」的画面（极轻的撕裂）。新代码请用 ycn_rdp_copy_frame。 */
YCN_API int ycn_rdp_grab_frame(int session, uint32_t* out_width, uint32_t* out_height,
                               uint32_t* out_stride, const uint8_t** out_data);

/* 取当前帧并**在 g_lock 内**拷贝到调用方缓冲（撕裂安全，推荐用这个）。
 * dst_size 需 >= stride*height；不足时返回 YCN_ERR_NO_MEMORY（用 out_* 里的真实尺寸可判出差异）。
 * 与 grab 的唯一区别就是拷贝发生在锁内，因此不会与 end_paint 的整帧写入交叠。 */
YCN_API int ycn_rdp_copy_frame(int session, uint8_t* dst, uint32_t dst_size,
                               uint32_t* out_width, uint32_t* out_height, uint32_t* out_stride);

/* 取最近一次错误的可读描述（UTF-8）。session 未知时返回全局最后错误 */
YCN_API void ycn_rdp_last_error(int session, char* buf, size_t buflen);

/* 诊断：GFX 图形管线状态。
 *   0 = use_gfx 关闭（没请求图形管线）
 *   1 = 已请求并订阅了通道事件，但**始终没收到任何动态通道连接事件**（DVC 没协商上）
 *   2 = GFX 通道已连上并接入了 GDI 管线（全帧模式已生效）
 *   3 = 收到过通道连接事件，但没有一条叫 RDPGFX_DVC_CHANNEL_NAME（通道名不匹配）
 *   4 = GFX 通道连上了，但 gdi_graphics_pipeline_init 返回失败（配 ycn_rdp_diag 的 gfx_init/codecs_null 看原因）
 * 用来区分「服务器没给 GFX」和「我们没接住 GFX」。 */
YCN_API int ycn_rdp_gfx_state(int session, uint32_t* out_dvc_count);

/* 诊断：已连上的通道名（逗号分隔，UTF-8）。用来确认服务器到底给没给 rdpgfx。 */
YCN_API void ycn_rdp_dvc_names(int session, char* buf, size_t buflen);

/* 诊断：一次性输出通道加载状态（load_addins 返回值 / 静态与动态通道计数 / GFX 状态 / 通道名）。 */
YCN_API void ycn_rdp_diag(int session, char* buf, size_t buflen);

/* 版本串（诊断 + 冒烟断言用） */
YCN_API const char* ycn_rdp_version(void);

/* 静音链路自验证（不依赖服务器推音频，纯本地）。
 * 内部构造一个假 rdpsnd 插件/设备、挂上与线上完全相同的钩子，然后
 *   ① 未静音时 Play 收到原始 PCM
 *   ② 静音时 Play 收到**全零** PCM，且仍被调用（rdpsnd 的 WaveConfirm 回执节奏不变）
 *   ③ 取消静音后恢复原始 PCM
 * 返回位掩码（31 = 全部通过）：
 *   0x01 拿到可用的会话槽位
 *   0x02 未静音时收到原始数据
 *   0x04 静音时收到全零数据
 *   0x08 静音时 Play 仍被调用
 *   0x10 取消静音后恢复原始数据
 * 用途：冒烟测试直接断言 (rc & 0x1F) == 0x1F，证明静音闸本身没写错。 */
YCN_API int ycn_rdp_selftest_mute(void);

#ifdef __cplusplus
}
#endif

#endif /* YCN_RDP_H */
