// RdpD3DRenderer：M4 生产渲染（计划书 §5.2 / §6 M4）。
//
// 架构（防退化，改前必读）：
//   - swap chain 尺寸 == 远端帧分辨率（CopyResource 同尺寸，**绝不**让 backbuffer 与
//     staging 尺寸不一致——那是不对齐 CopyResource，未定义行为，表现为竖直条纹）；
//   - 「缩放 + 黑边居中」由 XAML 层完成：SwapChainPanel（DIP 尺寸 = 远端分辨率）包在
//     Viewbox(Stretch=Uniform) 里，框架自动维护 CompositionScale 并由 DWM 组合器采样缩放；
//     不要改回手动 COM SetCompositionScale——它会被 XAML 布局重置为 1，画面变 1:1 裁切；
//   - Present(0,0)：不等待 vsync——vsync 阻塞会卡住 FreeRDP 事件循环线程（网络/输入同线程）；
//   - 整条管线在原生帧回调线程上跑，零 UI 线程参与；失败回退 M2 SoftwareBitmap 路线。
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace YukinoChan.Services
{
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISwapChainPanelNative
    {
        [PreserveSig]
        void SetSwapChain(IntPtr swapChain);
    }

    internal sealed class RdpD3DRenderer : IDisposable
    {
        /// <summary>--embed-nod3d 时强制 SoftwareBitmap 路线（诊断开关）。</summary>
        public static bool DisableD3D { get; } = Array.Exists(
            Environment.GetCommandLineArgs(), a => a == "--embed-nod3d");
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGISwapChain1? _swapChain;
        private ID3D11Texture2D? _staging;
        private ID3D11Texture2D? _backBuffer;
        private IntPtr _panelPtr;                 // SwapChainPanel 原生指针（SetCompositionScale 用）
        private uint _remoteWidth;
        private uint _remoteHeight;

        public bool Initialized { get; private set; }

        public string? LastError { get; private set; }

        /// <summary>创建设备/交换链（远端分辨率）并绑定到 SwapChainPanel。</summary>
        public bool Initialize(Microsoft.UI.Xaml.Controls.SwapChainPanel panel,
            uint remoteWidth, uint remoteHeight)
        {
            try
            {
                var rc = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                    out var device);
                if (rc.Failure || device is null)
                {
                    rc = D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Warp,
                        DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0 },
                        out device);
                    if (rc.Failure || device is null)
                    {
                        LastError = "D3D11CreateDevice failed (HW+WARP)";
                        return false;
                    }
                }

                _device = device;
                _context = device.ImmediateContext;

                var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();

                _remoteWidth = remoteWidth;
                _remoteHeight = remoteHeight;

                var desc = new SwapChainDescription1
                {
                    Width = remoteWidth,
                    Height = remoteHeight,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                };
                _swapChain = factory.CreateSwapChainForComposition(device, desc);
                if (_swapChain is null)
                {
                    LastError = "CreateSwapChainForComposition returned null";
                    return false;
                }

                _panelPtr = ((IWinRTObject)panel).NativeObject.ThisPtr;
                var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
                Marshal.QueryInterface(_panelPtr, ref iid, out var nativePtr);
                try
                {
                    var panelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(nativePtr);
                    panelNative.SetSwapChain(_swapChain.NativePointer);
                }
                finally
                {
                    Marshal.Release(nativePtr);
                }

                CreateStaging();
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                Initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private void CreateStaging()
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = _remoteWidth,
                Height = _remoteHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        /// <summary>远端分辨率变化：重建交换链与 staging（同尺寸原则）。</summary>
        public bool ResizeRemote(uint width, uint height)
        {
            if (!Initialized || (_remoteWidth == width && _remoteHeight == height))
            {
                return true;
            }
            try
            {
                _backBuffer?.Dispose();
                _backBuffer = null;
                _swapChain!.ResizeBuffers(0, width, height, Format.B8G8R8A8_UNorm, 0);
                _remoteWidth = width;
                _remoteHeight = height;
                CreateStaging();
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "ResizeRemote: " + ex.Message;
                return false;
            }
        }

        /// <summary>上传一帧（同尺寸 CopyResource）并呈现。失败返回 false。</summary>
        public bool Present(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride)
        {
            if (!Initialized || _staging is null || _backBuffer is null || _context is null)
            {
                LastError = "renderer not initialized";
                return false;
            }
            if (width != _remoteWidth || height != _remoteHeight)
            {
                if (!ResizeRemote(width, height))
                {
                    return false;
                }
            }

            try
            {
                // 上传远端帧：Map/Unmap 手动拷贝。不用 UpdateSubresource 的 span 重载——
                // 每帧 new 的数组若在原生执行期间被 GC 回收会 AV（M4 崩溃 bug），fixed 显式固定
                var mapped = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    fixed (byte* src = pixels)
                    {
                        var dst = (byte*)mapped.DataPointer;
                        var rowBytes = (int)(width * 4);
                        if ((uint)mapped.RowPitch == stride)
                        {
                            Buffer.MemoryCopy(src, dst, (long)mapped.RowPitch * height, (long)stride * height);
                        }
                        else
                        {
                            for (var row = 0; row < height; row++)
                            {
                                Buffer.MemoryCopy(
                                    src + (ulong)(stride * row),
                                    dst + (ulong)(mapped.RowPitch * row),
                                    rowBytes,
                                    rowBytes);
                            }
                        }
                    }
                }
                _context.Unmap(_staging, 0);
                _context.CopyResource(_backBuffer, _staging);
                // SyncInterval=0：立即返回不等待 vsync（vsync 阻塞会卡住 FreeRDP 事件循环线程）
                _swapChain.Present(0, 0);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            Initialized = false;
            _staging?.Dispose();
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _staging = null;
            _backBuffer = null;
            _swapChain = null;
            _context = null;
            _device = null;
            _panelPtr = IntPtr.Zero;
        }
    }
}
