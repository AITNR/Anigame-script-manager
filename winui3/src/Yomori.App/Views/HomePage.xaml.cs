// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Yomori.Helpers;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 主页：一张大图，右键更换。
///
/// 这一页以前是「总览卡片 + 会话通道列表」，2026-10-23 按需求整体换掉：
/// 状态看右侧执行状态栏、通道走左侧导航，主页只留一张图。
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>允许当首页图的文件大小上限。再大就不是"显示一下"，而是把进程内存拿去解码了。</summary>
    private const long MaxBannerBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 解码宽度上限（物理像素）。只限宽、高按原比例推 —— 一张 6000×4000 的原图
    /// 整幅解码要 ~90MB，界面上根本用不到那么多像素。
    /// </summary>
    private const int BannerDecodeWidth = 2048;

    public HomePage()
    {
        InitializeComponent();
        ShowProblemReportIfNeeded();

        // 图片加载会 await 解码，构造里不能 await，发了就走（异常全在内部接住）
        _ = UpdateBannerAsync();
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 上次运行异常报告 ----------------

    private void ShowProblemReportIfNeeded()
    {
        var report = VM.LoadAbnormalReport();
        if (report is null)
        {
            ProblemReportBar.IsOpen = false;
            return;
        }

        ProblemReportText.Text = report.Summary;
        ProblemReportBar.IsOpen = true;
    }

    private void OnProblemReportClosed(InfoBar sender, object args) => VM.DismissAbnormalReport();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.LogDir);

    // ---------------- 首页大图 ----------------

    /// <summary>随程序发布的默认首页图（<c>assets/home_banner.jpg</c>，与 exe 同级落地）。</summary>
    private static string DefaultBannerPath => Path.Combine(AppPaths.AssetsDir, "home_banner.jpg");

    /// <summary>
    /// 按配置重新加载首页图。
    ///
    /// 优先级：配置里指定的图 → 随程序发布的默认图 → 占位。
    /// 配置的图**只是找不到**（被删/移动/盘没挂）时静默回退默认图，不打扰；
    /// 但"找到了却读不出来/打不开"是真问题，必须写清原因，否则用户只会看到一片空白。
    /// </summary>
    private async Task UpdateBannerAsync()
    {
        var configured = (VM.Config.HomeBannerPath ?? string.Empty).Trim();
        var useDefault = configured.Length == 0 || !File.Exists(configured);
        var path = useDefault ? DefaultBannerPath : configured;

        if (!File.Exists(path))
        {
            SetPlaceholder(
                "还没有设置首页图片",
                "在任意位置点右键，选一张图片放到这里。");
            return;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBannerBytes)
            {
                SetPlaceholder(
                    "这张图太大了",
                    $"它有 {info.Length / (1024 * 1024)} MB，首页图请控制在 32 MB 以内。");
                return;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            SetPlaceholder("这张图读不出来", ex.Message);
            return;
        }

        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = BannerDecodeWidth,
        };

        try
        {
            using var stream = new InMemoryRandomAccessStream();

            // 走内存流而不是 new BitmapImage(new Uri(path))：后者会把源文件握住不放，
            // 用户想删/移动那张图时会失败。
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            SetPlaceholder(
                useDefault ? "默认首页图打不开" : "这张图打不开",
                $"支持 png / jpg / bmp / gif / webp。{ex.Message}");
            return;
        }

        ApplyBanner(bitmap);
    }

    /// <summary>
    /// 把解码好的图挂到界面上。
    ///
    /// 单独抽一层是为了收口线程：<c>SetSourceAsync</c> 的续体不保证还在 UI 线程上，
    /// 而 Image.Source 是 WinRT 属性，在别的线程上碰它 = COMException 0x8001010E。
    /// </summary>
    private void ApplyBanner(BitmapImage bitmap)
    {
        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => ApplyBanner(bitmap));
            return;
        }

        BannerImage.Source = bitmap;
        BannerImage.Visibility = Visibility.Visible;
        BannerPlaceholder.Visibility = Visibility.Collapsed;
        BannerHint.Visibility = Visibility.Visible;
    }

    /// <summary>退回占位：清掉图（否则旧的图会留在后面），并写上为什么没有图。</summary>
    private void SetPlaceholder(string title, string hint)
    {
        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => SetPlaceholder(title, hint));
            return;
        }

        BannerImage.Source = null;
        BannerImage.Visibility = Visibility.Collapsed;
        BannerPlaceholder.Visibility = Visibility.Visible;
        BannerHint.Visibility = Visibility.Collapsed;
        PlaceholderTitle.Text = title;
        PlaceholderHint.Text = hint;
    }

    /// <summary>用的就是默认图时，「恢复默认图片」没有意义，灰掉。</summary>
    private void OnBannerMenuOpening(object sender, object e) =>
        ClearBannerItem.IsEnabled = (VM.Config.HomeBannerPath ?? string.Empty).Trim().Length > 0;

    private async void OnChangeBannerImage(object sender, RoutedEventArgs e)
    {
        // PickOpenFileAsync 已经把结果搬回 UI 线程了（见 DialogHelper 里的说明），
        // 这里拿到路径就能直接改界面。
        var path = await DialogHelper.PickOpenFileAsync(
            ("图片", ".png"),
            ("图片", ".jpg"),
            ("图片", ".jpeg"),
            ("图片", ".bmp"),
            ("图片", ".gif"),
            ("图片", ".webp"));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        VM.SaveHomeBanner(path);
        await UpdateBannerAsync();
    }

    private async void OnRestoreDefaultBanner(object sender, RoutedEventArgs e)
    {
        VM.SaveHomeBanner(string.Empty);
        await UpdateBannerAsync();
    }
}
