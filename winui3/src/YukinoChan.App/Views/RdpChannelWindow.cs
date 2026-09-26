// -*- coding: utf-8 -*-
// 通道独立窗口（M6）：把某条通道的画面弹到一个**普通可缩放窗口**里，
// 用户自己并排摆放多路画面 —— 比网格更灵活，且每路仍是独立渲染。
//
// 与 RdpFullScreenWindow 同源：都是「新窗口新建 RdpView 接管同一个 client」。
// WinUI 3 不允许把可视树元素跨窗口移动，所以"弹出" ≠ 搬运控件，而是第二个渲染器接管。
// 区别只在于这里不设全屏、不装键盘钩子（那是"全屏操作"才需要的）。
using Microsoft.UI.Xaml;
using YukinoChan.Services;

namespace YukinoChan.Views
{
    internal sealed class RdpChannelWindow : Window
    {
        public RdpView View { get; }

        public RdpChannelWindow(RdpEmbeddedClient client, string channelName)
        {
            View = new RdpView();
            View.DiagnosticLog = App.ViewModel.AppendLog;
            View.AttachClient(client);
            Content = View;
            Title = $"雪乃酱 · {channelName}";

            // 默认给一个能看清整屏又不铺满的尺寸；用户可自由缩放 / 并排
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1024, 640));
        }

        /// <summary>
        /// 解下画面（关窗时**先**调用）：把 client 交还给页面网格，
        /// 否则页面重新挂载时会出现两个渲染器抢同一个 client。
        /// </summary>
        public void DetachView() => View.DetachClient();
    }
}
