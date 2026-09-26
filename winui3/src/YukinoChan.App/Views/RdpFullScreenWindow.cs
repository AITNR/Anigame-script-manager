// 全屏窗口：创建自己的 RdpView 并接管同一 RdpEmbeddedClient（画面会话不中断）。
// WinUI 3 不允许把可视树元素跨窗口移动，因此全屏 ≠ 移动控件，而是 = 第二个渲染器接管。
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using YukinoChan.Services;

namespace YukinoChan.Views
{
    internal sealed class RdpFullScreenWindow : Window
    {
        public RdpView View { get; }

        public RdpFullScreenWindow(RdpEmbeddedClient client)
        {
            View = new RdpView();
            View.AttachClient(client);
            Content = View;
            Title = "雪乃酱 · 远程桌面（全屏）";

            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }
}
