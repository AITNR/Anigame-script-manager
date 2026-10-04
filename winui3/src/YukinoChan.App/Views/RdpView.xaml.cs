// RdpView：内嵌远程桌面画面控件。
//
// M4 生产渲染：D3D11 SwapChainPanel（交换链 = 远端分辨率；缩放/居中由 CompositionScale
// + 布局完成，GPU 组合器采样）。D3D 初始化失败自动回退 M2 SoftwareBitmap 路线。
// D3D 采用**惰性初始化**：第一帧到达时按真实帧尺寸创建（Connect/AttachClient 两用）。
//
// 输入：指针/键盘事件经 RdpInputMapper 转发；全屏接管见 ChannelsPage 的 LL 钩子。
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using YukinoChan.Models;
using YukinoChan.Services;

namespace YukinoChan.Views
{
    public sealed partial class RdpView : UserControl
    {
        private RdpEmbeddedClient? _client;
        private SoftwareBitmapSource? _bitmapSource;
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private RdpD3DRenderer? _renderer;
        private bool _d3dFailed;               // D3D 初始化失败后本实例不再重试（回退软渲染）
        private bool _initQueued;              // 惰性初始化已排队（防高频帧重复排队泄漏渲染器）
        private bool _renderSuspended;         // 全屏时页面内渲染器挂起，避免双路渲染
        private long _lastFrameArrivedTick;    // 最近一次 end_paint（原生线程写，帧节奏统计用）
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presentTimer;  // 16ms 呈现节拍器（≈60Hz）
        private bool _softRendering;           // 软渲染路线正在拷帧（防 async 重入）
        private bool _renderPathLogged;        // 「当前渲染路径」只写一次日志
        private long _frameArrivals;           // 统计窗口内的帧到达次数（原生线程写）
        private long _maxArrivalGapMs;         // 统计窗口内最大帧间隔（原生线程写）
        private long _statsWindowStart;        // 统计窗口起点（UI 线程写）
        private int _frameSeq;                 // 帧到达序号（原生线程递增）
        private int _presentedSeq;             // 已呈现到的序号（UI 线程写）
        private long _windowStartFrames;       // 统计窗口起点时的 FramesRendered（算呈现帧率用）
        private int _lastGfxState = -1;        // 上次打印过的 GFX 状态
        private uint _lastDvcCount;            // 上次打印过的通道数
        private double _remoteWidth;
        private double _remoteHeight;

        /// <summary>画面刷新次数（诊断/验收计数）。</summary>
        public ulong FramesRendered { get; private set; }

        /// <summary>已转投远端的按键次数（诊断/验收计数）。</summary>
        public ulong KeysForwarded { get; private set; }

        /// <summary>会话断开（含原生 reason 与详情）。</summary>
        public event EventHandler<(int Session, int Reason, string Detail)>? SessionDisconnected;

        /// <summary>全屏切换请求（双击画面或 F11）。由页面/宿主决定是否进入全屏。</summary>
        public event EventHandler? FullScreenToggleRequested;

        /// <summary>诊断日志出口（页面注入 VM.AppendLog）。</summary>
        public Action<string>? DiagnosticLog;

        public RdpView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                IsTabStop = true;
                IsHitTestVisible = true;
            };

            // ---- 指针（M3）----
            PointerMoved += OnPointerMoved;
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheel;
            PointerExited += OnPointerReleased; // 拖拽中移出画面 → 释放按键，防远端卡键

            // ---- 键盘（M3）----
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;

            // ---- 全屏（M3）：双击画面触发 ----
            DoubleTapped += (_, _) => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>连接目标。返回 false 表示发起失败（错误经状态文本显示）。</summary>
        public bool Connect(RdpConnectInfo info)
        {
            DetachClient();

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _client = new RdpEmbeddedClient();
            HookClient(_client);
            StartPresentTimer();

            ShowOverlay("连接中…", progress: true);
            try
            {
                return _client.Connect(info);
            }
            catch (Exception ex)
            {
                ShowOverlay($"连接失败：{ex.Message}", progress: false);
                return false;
            }
        }

        /// <summary>接管外部 client（全屏窗口复用同一会话时用）。</summary>
        public void AttachClient(RdpEmbeddedClient client)
        {
            DetachClient();
            _client = client;
            HookClient(_client);

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            StartPresentTimer();
            ShowOverlay("全屏会话", progress: false, hide: true);
            UpdatePanelLayout();
        }

        /// <summary>只解除事件绑定与画面，不断开会话（交还另一处 RdpView 时用）。</summary>
        public void DetachClient()
        {
            if (_client is not null)
            {
                _client.Connected -= OnClientConnected;
                _client.FrameArrived -= OnFrameArrived;
                _client.DesktopResized -= OnDesktopResized;
                _client.Disconnected -= OnClientDisconnected;
                _client = null;
            }
            _renderer?.Dispose();
            _renderer = null;
            _initQueued = false;
            _d3dFailed = false;
            _softRendering = false;
            _renderPathLogged = false;
            _statsWindowStart = 0;
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _bitmapSource = null;
            _presentTimer?.Stop();
            _presentTimer = null;
            _lastFrameArrivedTick = 0;
            _frameSeq = 0;
            _presentedSeq = 0;
            _windowStartFrames = 0;
            _lastGfxState = -1;
            _lastDvcCount = 0;
            PartFrame.Source = null;
            PartFrame.Visibility = Visibility.Collapsed;
            PartPanel.Visibility = Visibility.Collapsed;
            PartViewbox.Visibility = Visibility.Collapsed;
            FramesRendered = 0;
            KeysForwarded = 0;
            ShowOverlay("未连接", progress: false);
        }

        /// <summary>断开会话并复位画面。幂等。</summary>
        public void Disconnect()
        {
            if (_client is null)
            {
                return;
            }
            UnhookClient(_client);
            _client.Dispose();
            _client = null;
            _renderer?.Dispose();
            _renderer = null;
            _initQueued = false;
            _d3dFailed = false;
            _softRendering = false;
            _renderPathLogged = false;
            _statsWindowStart = 0;
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _bitmapSource = null;
            _presentTimer?.Stop();
            _presentTimer = null;
            _lastFrameArrivedTick = 0;
            _frameSeq = 0;
            _presentedSeq = 0;
            _windowStartFrames = 0;
            _lastGfxState = -1;
            _lastDvcCount = 0;
            PartFrame.Source = null;
            PartFrame.Visibility = Visibility.Collapsed;
            PartPanel.Visibility = Visibility.Collapsed;
            PartViewbox.Visibility = Visibility.Collapsed;
            FramesRendered = 0;
            KeysForwarded = 0;
            ShowOverlay("未连接", progress: false);
        }

        /// <summary>暂停本渲染器（全屏时页面内实例用），恢复用 ResumeRendering。</summary>
        public void SuspendRendering() => _renderSuspended = true;

        public void ResumeRendering() => _renderSuspended = false;

        /// <summary>启动 16ms 呈现节拍器（静默检测 + 兑底节流，见 OnPresentTick）。</summary>
        private void StartPresentTimer()
        {
            if (_presentTimer is not null)
            {
                return;
            }
            _presentTimer = _dispatcher?.CreateTimer();
            if (_presentTimer is null)
            {
                return;
            }
            _presentTimer.Interval = TimeSpan.FromMilliseconds(16);
            _presentTimer.Tick += OnPresentTick;
            _presentTimer.Start();
        }

        /// <summary>当前绑定的客户端（全屏窗口接管同一会话时读取）。</summary>
        public RdpEmbeddedClient? CurrentClient => _client;

        /// <summary>注入鼠标事件（flags 取 YcnPtrFlags 常量）。</summary>
        public bool SendMouse(uint flags, ushort x, ushort y) => _client?.SendMouse(flags, x, y) ?? false;

        /// <summary>注入键盘事件。</summary>
        public bool SendKey(bool down, bool extended, ushort scancode) => _client?.SendKey(down, extended, scancode) ?? false;

        private void HookClient(RdpEmbeddedClient client)
        {
            client.Connected += OnClientConnected;
            client.FrameArrived += OnFrameArrived;
            client.DesktopResized += OnDesktopResized;
            client.Disconnected += OnClientDisconnected;
        }

        private void UnhookClient(RdpEmbeddedClient client)
        {
            client.Connected -= OnClientConnected;
            client.FrameArrived -= OnFrameArrived;
            client.DesktopResized -= OnDesktopResized;
            client.Disconnected -= OnClientDisconnected;
        }

        private void OnClientConnected(object? sender, (int Session, uint Width, uint Height) e)
        {
            _remoteWidth = e.Width;
            _remoteHeight = e.Height;
            var enqueued = _dispatcher?.TryEnqueue(() =>
                ShowOverlay($"已连接 {e.Width}×{e.Height}", progress: false, hide: true));
        }

        private void OnDesktopResized(object? sender, (int Session, uint Width, uint Height) e)
        {
            // 原生线程直接更新：远端分辨率 + D3D 交换链重建；软渲染路线靠下一帧自然适配
            _remoteWidth = e.Width;
            _remoteHeight = e.Height;
            _renderer?.ResizeRemote(e.Width, e.Height);
            var enqueued = _dispatcher?.TryEnqueue(UpdatePanelLayout);
        }

        private void OnFrameArrived(object? sender, RdpFrameEventArgs e)
        {
            if (_renderSuspended)
            {
                return; // 全屏期间页面内渲染器挂起，全屏窗口的渲染器接管
            }

            var client = _client;
            if (client is null || client.Session != e.Session)
            {
                return;
            }

            // 服务器按条带逐步重绘，条带中途快照会撕裂（M5 实测）：这里只记录到达时间，
            // 实际取帧交给呈现节拍器（16ms tick）：等 end_paint 静默 ≥8ms（一轮画完）
            // 或兜底节流 60ms（持续动画 16fps，撕裂均匀化）再抓帧呈现
            var arrival = Environment.TickCount64;
            var prevArrival = System.Threading.Volatile.Read(ref _lastFrameArrivedTick);
            if (prevArrival != 0)
            {
                var gap = arrival - prevArrival;
                if (gap > System.Threading.Interlocked.Read(ref _maxArrivalGapMs))
                {
                    System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, gap);
                }
            }
            System.Threading.Interlocked.Increment(ref _frameArrivals);
            System.Threading.Volatile.Write(ref _lastFrameArrivedTick, arrival);
            // 用序号而不是 bool：bool 的「读-清」之间有竞争窗口，会丢掉正好落在那里的帧
            // （实测：服务器 37 帧/秒 → 呈现只有 31 帧/秒，就是被这个窗口吃掉的）
            System.Threading.Interlocked.Increment(ref _frameSeq);

            if (_remoteWidth == 0 || _remoteHeight == 0)
            {
                _remoteWidth = e.Width;
                _remoteHeight = e.Height;
            }

            // 惰性初始化 D3D（用事件参数里的尺寸，无需 memcpy）；失败后本实例固定走软渲染。
            // 注意：panel 尺寸直接用帧尺寸 —— VM 路径下 Connected 事件可能在挂钩前
            // 已错过（_remoteWidth 为 0），不能依赖它（M5 黑屏根因）
            if (_renderer is null && !_initQueued && !_d3dFailed && !YukinoChan.Services.RdpD3DRenderer.DisableD3D)
            {
                _initQueued = true;
                var initW = e.Width;
                var initH = e.Height;
                var enqueued = _dispatcher?.TryEnqueue(() =>
                {
                    _renderer = new RdpD3DRenderer();
                    if (_renderer.Initialize(PartPanel, initW, initH))
                    {
                        PartPanel.Visibility = Visibility.Visible;
                        PartViewbox.Visibility = Visibility.Visible;
                        PartFrame.Visibility = Visibility.Collapsed;
                        DiagnosticLog?.Invoke($"[embed] D3D 渲染器初始化成功（{initW}×{initH}）");
                        LogRenderPathOnce("D3D11 / SwapChainPanel");
                    }
                    else
                    {
                        DiagnosticLog?.Invoke($"[embed] D3D 初始化失败（{_renderer.LastError}），回退 SoftwareBitmap");
                        _renderer.Dispose();
                        _renderer = null;
                        _d3dFailed = true;
                        _bitmapSource = new SoftwareBitmapSource();
                        PartFrame.Source = _bitmapSource;
                        PartFrame.Visibility = Visibility.Visible;
                    }
                    // 面板 DIP 尺寸 = 帧尺寸；Viewbox(Uniform) 负责缩放与居中
                    PartPanel.Width = initW;
                    PartPanel.Height = initH;
                    PartViewbox.Visibility = PartPanel.Visibility;
                });
                if (enqueued != true)
                {
                    _initQueued = false;
                }
            }

            if (_renderer is { Initialized: true })
            {
                return; // D3D 路线：呈现交给节拍器（OnPresentTick）
            }

            if (!_d3dFailed && !YukinoChan.Services.RdpD3DRenderer.DisableD3D)
            {
                return; // 初始化排队中：下一拍就有了
            }

            // 软渲染回退路线也**不在这里直接渲染** —— 同样交给呈现节拍器节流。
            // （原先这里每帧 TryEnqueue 直渲，等于撕裂修复在回退路线上完全没生效。）
            // 本方法只负责记 _lastFrameArrivedTick，抓帧时机由 OnPresentTick 决定。
        }

        /// <summary>
        /// 呈现节拍器 tick：**固定 60Hz 节拍**（16ms 定时器）。
        ///
        /// 只在「两拍之间收到过新帧」时抓帧呈现（_frameDirty），故实际呈现帧率 =
        /// min(60, 服务器推帧率)；静止画面不重复 Present（重复送同一帧没有意义）。
        ///
        /// 早先那套「等 end_paint 静默 ≥8ms 才算一轮画完」的启发式已删除：服务器持续推帧时
        /// 它永远不成立，只会退化成「每 60ms 兜底抓一次」—— 那正是最后那点撕裂的来源之一。
        /// 「抓到的必然是完整帧」现在由原生侧保证：ycn_rdp_copy_frame 在 g_lock 内整帧拷贝。
        /// </summary>
        private void OnPresentTick(object? sender, object e)
        {
            if (_renderSuspended)
            {
                return;
            }
            var now = Environment.TickCount64;
            EmitFrameRhythmStats(now);

            var seq = System.Threading.Volatile.Read(ref _frameSeq);
            if (seq == _presentedSeq)
            {
                return; // 还没有新帧：保留最后一帧即可
            }
            var client = _client;
            if (client is null)
            {
                return;
            }
            _presentedSeq = seq;

            if (!client.TryCopyFrame(out var w, out var h, out var stride, out var pixels))
            {
                return; // 尺寸切换瞬间等：下一拍再来
            }
            if (_renderer is { Initialized: true })
            {
                if (_renderer.Present(pixels, w, h, stride))
                {
                    FramesRendered++;
                }
                else
                {
                    DiagnosticLog?.Invoke($"[embed] Present 失败：{_renderer.LastError}");
                    _d3dFailed = true; // 本帧起回退软渲染（renderer 失效不再重试）
                    _renderer.Dispose();
                    _renderer = null;
                    _bitmapSource = new SoftwareBitmapSource();
                    PartFrame.Source = _bitmapSource;
                    PartFrame.Visibility = Visibility.Visible;
                    PartPanel.Visibility = Visibility.Collapsed;
                    PartViewbox.Visibility = Visibility.Collapsed;
                }
            }
            else if (_bitmapSource is not null && !_softRendering)
            {
                // 软渲染回退路线：同一套节拍（否则回退后撕裂原样回来）
                LogRenderPathOnce("SoftwareBitmap（软渲染回退，无 D3D）");
                _ = RenderFrameAsync(client.Session);
            }
        }

        /// <summary>把当前实际渲染路径写一次日志（撕裂排查的第一手依据）。</summary>
        private void LogRenderPathOnce(string path)
        {
            if (_renderPathLogged)
            {
                return;
            }
            _renderPathLogged = true;
            DiagnosticLog?.Invoke($"[embed] 当前渲染路径：{path}");
        }

        /// <summary>
        /// 每 5 秒把「帧到达节奏 / 实际呈现帧率」写一行日志（撕裂与帧率定位用）。
        ///
        /// 判读方式：条带（legacy bitmap）路径下服务器把一次桌面更新拆成大量碎帧，
        /// 到达频率会远高于画面帧率、且间隔参差；走 GFX 全帧管线时每次 end_paint
        /// 就是完整一帧，到达频率贴近真实画面帧率、间隔整齐。
        /// 「呈现」一栏是节拍器真正 Present 出去的帧率，上限就是节拍器频率（16ms ≈ 60Hz）。
        /// </summary>
        private void EmitFrameRhythmStats(long now)
        {
            if (_statsWindowStart == 0)
            {
                _statsWindowStart = now;
                _windowStartFrames = (long)FramesRendered;
                return;
            }
            var elapsed = now - _statsWindowStart;
            if (elapsed < 5000)
            {
                return;
            }
            var arrivals = System.Threading.Interlocked.Read(ref _frameArrivals);
            var maxGap = System.Threading.Interlocked.Read(ref _maxArrivalGapMs);
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _statsWindowStart = now;

            var presented = (long)FramesRendered - _windowStartFrames;
            _windowStartFrames = (long)FramesRendered;

            if (_client is { } gfxClient)
            {
                var st = gfxClient.GfxState(out var dvc);
                if (st != _lastGfxState || dvc != _lastDvcCount)
                {
                    _lastGfxState = st;
                    _lastDvcCount = dvc;
                    var desc = st switch
                    {
                        0 => "未请求图形管线",
                        1 => "已订阅通道事件，但一条动态通道都没连上 —— 服务器没给 GFX",
                        2 => "GFX 通道已接入 GDI（全帧模式）",
                        3 => "动态通道连上了，但没有 GFX 那条（通道名不匹配）",
                        4 => "GFX 通道已连上，但 gdi_graphics_pipeline_init 失败（看 gfx_init / codecs_null）",
                        _ => "会话未知",
                    };
                    DiagnosticLog?.Invoke(
                        $"[embed] GFX 管线状态：{st}（{desc}）｜{gfxClient.Diag()}");
                }
            }

            DiagnosticLog?.Invoke(
                $"[embed] 帧到达节奏：{arrivals} 次 / {elapsed / 1000.0:F0}s" +
                $"（≈{arrivals * 1000.0 / elapsed:F1} 次/秒，最大间隔 {maxGap} ms）｜" +
                $"呈现 ≈{presented * 1000.0 / elapsed:F1} 帧/秒");
        }

        private async System.Threading.Tasks.Task RenderFrameAsync(int session)
        {
            if (_softRendering)
            {
                return; // 上一帧还在拷：下一拍再来
            }
            _softRendering = true;
            try
            {
                var client = _client;
                if (client is null || client.Session != session)
                {
                    return;
                }

                if (!client.TryCopyFrame(out var w, out var h, out var stride, out var pixels))
                {
                    return;
                }

                // pixels 是复用缓冲，长度可能大于本帧实际字节数 —— 必须按 stride*height 切
                var byteCount = checked((int)(stride * h));
                var buffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels, 0, byteCount);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    buffer, BitmapPixelFormat.Bgra8, (int)w, (int)h, BitmapAlphaMode.Ignore);
                if (_bitmapSource is not null)
                {
                    await _bitmapSource.SetBitmapAsync(bitmap);
                    FramesRendered++;
                }
            }
            catch
            {
                // 尺寸切换瞬间可能抛参数异常，丢帧即可
            }
            finally
            {
                _softRendering = false;
            }
        }

        private void OnClientDisconnected(object? sender, (int Session, int Reason, string Detail) e)
        {
            var enqueued = _dispatcher?.TryEnqueue(() =>
            {
                ShowOverlay($"已断开：{e.Detail}", progress: false);
                SessionDisconnected?.Invoke(this, e);
            });
        }

        /// <summary>按 letterbox 计算面板 DIP 尺寸与 CompositionScale（swap 像素↔DIP）。</summary>
        private void UpdatePanelLayout()
        {
            if (_remoteWidth == 0 || _remoteHeight == 0)
            {
                return;
            }
            // 面板 DIP 尺寸 = 远端分辨率（整数）；等比缩放+居中全部交给 Viewbox(Uniform)，
            // CompositionScale 由框架按渲染变换自动维护（手动 COM 设置会被布局重置 → 1:1 裁切）
            PartPanel.Width = _remoteWidth;
            PartPanel.Height = _remoteHeight;
            PartViewbox.Visibility = PartPanel.Visibility;
        }

        private void ShowOverlay(string text, bool progress, bool hide = false)
        {
            PartStatusText.Text = text;
            PartProgress.IsActive = progress;
            PartProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            PartOverlay.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- 输入映射（M3）----
        private bool _leftDown;      // 左键按下（拖拽 = 移动时带 Button1 down 位）
        private bool _rightDown;
        private bool _middleDown;

        /// <summary>当前指针是否可直接转发远端（画面未就绪/遮罩在时不转发）。</summary>
        private bool InputForwardingEnabled => _client is not null && PartOverlay.Visibility == Visibility.Collapsed;

        private bool TryForwardPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, uint extraFlags)
        {
            var client = _client;
            if (client is null || !InputForwardingEnabled)
            {
                return false;
            }
            var pt = e.GetCurrentPoint(this);
            var (x, y) = RdpInputMapper.MapPointerToRemote(
                pt.Position.X, pt.Position.Y, ActualWidth, ActualHeight, _remoteWidth, _remoteHeight);
            return client.SendMouse(extraFlags, x, y);
        }

        private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var flags = YcnPointerFlags.Move | (_leftDown ? YcnPointerFlags.Button1 : 0);
            TryForwardPointer(e, flags);
        }

        private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Focus(FocusState.Pointer);
            var props = e.GetCurrentPoint(this).Properties;
            uint flags;
            if (props.IsRightButtonPressed)
            {
                flags = YcnPointerFlags.Button2 | YcnPointerFlags.Down;
                _rightDown = true;
            }
            else if (props.IsMiddleButtonPressed)
            {
                flags = YcnPointerFlags.Button3 | YcnPointerFlags.Down;
                _middleDown = true;
            }
            else
            {
                flags = YcnPointerFlags.Button1 | YcnPointerFlags.Down;
                _leftDown = true;
            }
            e.Handled = TryForwardPointer(e, flags);
        }

        private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            uint flags;
            if (props.IsRightButtonPressed || _rightDown)
            {
                flags = YcnPointerFlags.Button2;
                _rightDown = false;
            }
            else if (props.IsMiddleButtonPressed || _middleDown)
            {
                flags = YcnPointerFlags.Button3;
                _middleDown = false;
            }
            else
            {
                flags = YcnPointerFlags.Button1;
                _leftDown = false;
            }
            e.Handled = TryForwardPointer(e, flags);
        }

        private void OnPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var client = _client;
            if (client is null || !InputForwardingEnabled)
            {
                return;
            }
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            var (flags, encoded) = RdpInputMapper.MapWheel(delta);
            if (flags == 0)
            {
                return;
            }
            var pt = e.GetCurrentPoint(this);
            var (x, y) = RdpInputMapper.MapPointerToRemote(
                pt.Position.X, pt.Position.Y, ActualWidth, ActualHeight, _remoteWidth, _remoteHeight);
            // 滚轮 delta 编码在 PTR_FLAGS 的 0x0000FF00 段（MS-RDPBCGR 2.2.8.1.1.1.1）
            client.SendMouse(flags | (encoded << 8), x, y);
            e.Handled = true;
        }

        private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // F11：全屏切换
            if ((ulong)e.Key == 0x7A)
            {
                e.Handled = true;
                FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!InputForwardingEnabled)
            {
                return;
            }
            var (sc, ext) = RdpInputMapper.MapKey((ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, (ulong)e.Key);
            if (sc == 0)
            {
                return;
            }
            if (_client?.SendKey(true, ext, sc) == true)
            {
                KeysForwarded++;
                e.Handled = true;
            }
        }

        private void OnKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var (sc, ext) = RdpInputMapper.MapKey((ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, (ulong)e.Key);
            if (sc == 0)
            {
                return;
            }
            if (_client?.SendKey(false, ext, sc) == true)
            {
                KeysForwarded++;
                e.Handled = true;
            }
        }
    }
}
