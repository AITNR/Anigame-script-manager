// RDP 内嵌客户端：ycn_rdp.dll 的 C# 生命周期封装（计划书 §6 M2）。
// 纯 .NET、不碰 XAML —— 帧数据以「原生指针 + 尺寸」形式经 FrameArrived 事件外送，
// 由 UI 层（RdpView）负责调度到 UI 线程并渲染。
//
// 线程约定：
//   - Connected/FrameArrived/Disconnected/ErrorOccurred 可能来自原生事件循环线程；
//   - 消费方若要碰 UI，必须自行切 DispatcherQueue；
//   - 回调里禁止调用 Disconnect()（原生侧死锁保护，见 ycn_rdp.h）。
using System;
using System.Runtime.InteropServices;
using YukinoChan.Models;
using YukinoChan.Services;

namespace YukinoChan.Services
{
    /// <summary>连接参数（结构化，避免散参）。</summary>
    public sealed record RdpConnectInfo
    {
        public required string Host { get; init; }
        public ushort Port { get; init; } = 3389;
        public required string Username { get; init; }
        public string? Domain { get; init; }
        public required string Password { get; init; }
        public uint DesktopWidth { get; init; } = 1280;
        public uint DesktopHeight { get; init; } = 720;
        public bool AllowSelfsigned { get; init; } = true;
        public bool EnableAudio { get; init; } = true;
        public bool UseGfx { get; init; }
    }

    /// <summary>一帧就绪的通知（携带可立即 grab 的会话）。</summary>
    public sealed class RdpFrameEventArgs : EventArgs
    {
        public int Session { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
        public uint Stride { get; init; }
    }

    /// <summary>
    /// 内嵌 RDP 客户端。一次实例对应一条会话；Disconnected/Failed 后可重用实例再次 Connect。
    /// </summary>
    public sealed class RdpEmbeddedClient : IDisposable
    {
        // ---- P/Invoke 回调必须保活，否则 GC 回收后原生回调野指针 ----
        private YcnRdpCallbacks? _nativeCallbacks;
        private YcnCallbacks.OnConnected? _onConnected;
        private YcnCallbacks.OnFrameReady? _onFrameReady;
        private YcnCallbacks.OnDesktopResize? _onResize;
        private YcnCallbacks.OnDisconnected? _onDisconnected;
        private YcnCallbacks.OnError? _onError;

        private readonly object _gate = new();
        private RdpClientState _state = RdpClientState.Idle;
        private int _session;

        /// <summary>抓帧复用缓冲（避免每帧分配 8MB 触发 GC）。长度 ≥ 最近一帧的 stride*height。</summary>
        private byte[] _frameBuffer = Array.Empty<byte>();

        public RdpClientState State { get { lock (_gate) { return _state; } } }
        public int Session => _session;

        /// <summary>会话建立（画面尺寸已定）。可能在原生线程触发。</summary>
        public event EventHandler<(int Session, uint Width, uint Height)>? Connected;
        /// <summary>有新帧可取。高频、可能在原生线程触发；用 TryGrabFrame 拷贝。</summary>
        public event EventHandler<RdpFrameEventArgs>? FrameArrived;
        /// <summary>远端桌面分辨率变更（M4：D3D 交换链需要跟着 Resize）。</summary>
        public event EventHandler<(int Session, uint Width, uint Height)>? DesktopResized;
        /// <summary>断开（每会话恰好一次）。reason 见 YcnDisconnectReason。</summary>
        public event EventHandler<(int Session, int Reason, string Detail)>? Disconnected;
        /// <summary>非致命错误通报。</summary>
        public event EventHandler<(int Session, int Code, string Message)>? ErrorOccurred;

        /// <summary>发起连接。成功返回 true 并进入 Connecting；失败返回 false 并进入 Failed。</summary>
        public bool Connect(RdpConnectInfo info)
        {
            if (info is null)
            {
                return false;
            }

            lock (_gate)
            {
                var next = RdpClientStateMachine.Transition(_state, RdpClientEvent.ConnectRequested);
                if (next == _state)
                {
                    return false; // 非法迁移（如重复 Connect）
                }
                _state = next;
            }

            _onConnected = (user, session, w, h) =>
            {
                lock (_gate) { _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.NativeConnected); }
                Connected?.Invoke(this, (session, w, h));
            };
            _onFrameReady = (user, session, w, h, stride) =>
                FrameArrived?.Invoke(this, new RdpFrameEventArgs { Session = session, Width = w, Height = h, Stride = stride });
            _onResize = (user, session, w, h) =>
                DesktopResized?.Invoke(this, (session, w, h));
            _onDisconnected = (user, session, reason, detail) =>
            {
                lock (_gate) { _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.NativeDisconnected); }
                Disconnected?.Invoke(this, (session, reason, detail ?? string.Empty));
            };
            _onError = (user, session, code, message) =>
                ErrorOccurred?.Invoke(this, (session, code, message ?? string.Empty));

            _nativeCallbacks = new YcnRdpCallbacks
            {
                Connected = _onConnected,
                FrameReady = _onFrameReady,
                DesktopResize = _onResize,
                Disconnected = _onDisconnected,
                Error = _onError,
            };

            var p = new YcnRdpParams
            {
                Host = info.Host,
                Port = info.Port,
                Username = info.Username,
                Domain = info.Domain,
                Password = info.Password,
                DesktopWidth = info.DesktopWidth,
                DesktopHeight = info.DesktopHeight,
                ColorDepth = 32,
                UseNla = -1,
                AllowSelfsigned = info.AllowSelfsigned ? 1 : 0,
                EnableAudio = info.EnableAudio ? 1 : 0,
                UseGfx = info.UseGfx ? 1 : 0,
            };

            var cb = _nativeCallbacks.Value;
            int session = RdpNativeInterop.ycn_rdp_connect(ref p, ref cb, IntPtr.Zero);
            if (session <= 0)
            {
                lock (_gate) { _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.ConnectRejected); }
                var buf = new byte[512];
                RdpNativeInterop.ycn_rdp_last_error(0, buf, (UIntPtr)buf.Length);
                throw new InvalidOperationException(
                    $"ycn_rdp_connect 失败（{session}）：{System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0')}");
            }

            _session = session;
            return true;
        }

        /// <summary>断开会话（语义 = mstsc「断开连接」）。幂等。</summary>
        public void Disconnect()
        {
            int session;
            lock (_gate)
            {
                if (_state is RdpClientState.Idle or RdpClientState.Disconnected or RdpClientState.Failed)
                {
                    return;
                }
                session = _session;
            }
            RdpNativeInterop.ycn_rdp_disconnect(session);
        }

        /// <summary>拷贝当前帧到托管数组。返回 false 表示当前无帧/未连接。</summary>
        public bool TryCopyFrame(out uint width, out uint height, out uint stride, out byte[] pixels)
        {
            width = height = stride = 0;
            pixels = Array.Empty<byte>();

            // 第一步只问尺寸：拿不到就快速失败，也顺便决定缓冲大小
            var rc = RdpNativeInterop.ycn_rdp_grab_frame(_session, out var w, out var h, out var st, out _);
            if (rc != 0 || w == 0 || h == 0 || st == 0)
            {
                return false;
            }

            var size = checked((int)(st * h));
            if (_frameBuffer.Length < size)
            {
                _frameBuffer = new byte[size];
            }

            // 第二步在原生锁内整帧拷贝：与 end_paint 的写入互斥，
            // 所以绝不会读到「拷贝到一半」的半成品帧（撕裂根源）。
            unsafe
            {
                fixed (byte* dst = _frameBuffer)
                {
                    rc = RdpNativeInterop.ycn_rdp_copy_frame(_session, dst, (uint)size, out w, out h, out st);
                }
            }
            if (rc != 0 || w == 0 || h == 0 || st == 0)
            {
                return false;
            }

            width = w;
            height = h;
            stride = st;
            // 注意：pixels 是复用缓冲，长度可能大于 stride*height —— 消费方按 stride/width/height 取值
            pixels = _frameBuffer;
            return true;
        }

        /// <summary>
        /// GFX 图形管线状态（诊断）：
        /// 0 未请求 / 1 已订阅但没有任何动态通道连上（服务器没给 GFX）/ 2 已接入 GDI（全帧模式）
        /// / 3 收到了动态通道但没有 GFX 那条 / -1 会话未知。
        /// </summary>
        public int GfxState(out uint dvcCount)
            => RdpNativeInterop.ycn_rdp_gfx_state(_session, out dvcCount);

        /// <summary>已连上的通道名（诊断，逗号分隔）。空串 = 一条都没连上。</summary>
        public string DvcNames()
        {
            var buf = new byte[256];
            RdpNativeInterop.ycn_rdp_dvc_names(_session, buf, (UIntPtr)buf.Length);
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0)
            {
                len = buf.Length;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>原生通道加载诊断（一行文本：load_addins 返回值 / 通道计数 / GFX 状态 / 通道名）。</summary>
        public string Diag()
        {
            var buf = new byte[512];
            RdpNativeInterop.ycn_rdp_diag(_session, buf, (UIntPtr)buf.Length);
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0)
            {
                len = buf.Length;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>注入鼠标事件（flags 取 YcnPtrFlags）。</summary>
        public bool SendMouse(uint flags, ushort x, ushort y)
            => RdpNativeInterop.ycn_rdp_send_mouse(_session, flags, x, y) == 0;

        /// <summary>注入键盘事件（VSC 扫描码；扩展键如方向键/Right Ctrl 传 extended=true）。</summary>
        public bool SendKey(bool down, bool extended, ushort scancode) => SendKeyRc(down, extended, scancode) == 0;

        /// <summary>注入键盘事件并返回原生返回码（诊断用）。</summary>
        public int SendKeyRc(bool down, bool extended, ushort scancode)
            => RdpNativeInterop.ycn_rdp_send_key(_session, down ? 1 : 0, extended ? 1 : 0, scancode);

        /// <summary>
        /// 本机静音 / 取消静音（远端照常发声，只是本机不放）。
        /// 立即生效、不需要重连；原生侧对 rdpsnd 设备的 SetVolume 做了链式挂钩，
        /// 静音态下服务器下发的音量 PDU 也盖不掉。只影响**本会话**，不波及其他通道。
        /// rdpsnd 通道还没连上时也安全（连上后原生侧会自动装钩子）。
        /// </summary>
        public bool SetMuted(bool muted)
            => RdpNativeInterop.ycn_rdp_set_muted(_session, muted ? 1 : 0) == 0;

        /// <summary>当前静音标志（原生侧为准）。会话不存在时返回 false。</summary>
        public bool IsMuted
        {
            get
            {
                var rc = RdpNativeInterop.ycn_rdp_get_muted(_session);
                return rc == 1;
            }
        }

        public static string GetLastError(int session)
        {
            var buf = new byte[512];
            RdpNativeInterop.ycn_rdp_last_error(session, buf, (UIntPtr)buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
