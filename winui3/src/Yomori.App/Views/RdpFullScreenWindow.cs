// 全屏窗口：创建自己的 RdpView 并接管同一 RdpEmbeddedClient（画面会话不中断）。
// WinUI 3 不允许把可视树元素跨窗口移动，因此全屏 ≠ 移动控件，而是 = 第二个渲染器接管。
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Yomori.Services;

namespace Yomori.Views
{
    internal sealed class RdpFullScreenWindow : Window
    {
        public RdpView View { get; }

        /// <summary>
        /// 本窗口的顶层 HWND。键盘钩子用它判断「全屏窗口是不是前台」——
        /// 不是前台就不能接管键盘（见 <see cref="RdpFullScreenCoordinator"/>）。
        /// </summary>
        public IntPtr Handle { get; }

        public RdpFullScreenWindow(RdpEmbeddedClient client)
        {
            View = new RdpView();
            View.AttachClient(client);
            Content = View;
            Title = "Yomori · 远程桌面（全屏）";

            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            // 必须在窗口已实现（有 HWND）之后取；WinUI 的 Window 只有一个顶层 HWND，
            // 画面内容走合成器，不会产生子 HWND，所以直接比这一个句柄就够了。
            Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
    }
}
