// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YukinoChan.Models;
using YukinoChan.Services;

namespace YukinoChan.Views;

/// <summary>
/// 「关于 → 开源许可」对话框：列出随包分发的第三方组件与各自许可证。
///
/// <para><b>这不是装饰</b>。包里实际带着 FreeRDP / WinPR / OpenSSL 的 DLL，三者都是
/// Apache-2.0，该许可证 §4 要求分发时随附许可证副本 —— 用户得能在程序里找到"用了什么、
/// 什么许可"。完整原文（几万字）不适合塞进弹窗，所以这里只列清单，
/// 另给一个入口打开发布包里的 <c>THIRD-PARTY-NOTICES.txt</c>。</para>
///
/// <para>与工程里其他弹窗一样用代码构建：所有 ContentDialog 都"临时造一个、ShowAsync
/// 完就扔"，且必须走 <see cref="DialogHelper.ShowAsync"/> 的全局闸门（同一时刻只允许一个
/// ContentDialog，第二个直接抛 COMException）。</para>
///
/// <para><b>清单数据来自嵌入资源</b>（<see cref="ThirdPartyNotices"/>），不是写在这里的
/// 字面量 —— 与打包脚本共用 <c>winui3/third_party/components.json</c>，改一处两边都变。</para>
/// </summary>
public sealed class ThirdPartyNoticesDialog : ContentDialog
{
    public ThirdPartyNoticesDialog()
    {
        Title = "第三方组件与许可";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Close;

        var panel = new StackPanel
        {
            Spacing = 10,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "本程序在分发时包含下列第三方组件。各组件的完整许可证原文见程序目录下的 "
                   + ThirdPartyNotices.FullNoticeFileName + "。",
            TextWrapping = TextWrapping.Wrap,
        });

        if (ThirdPartyNotices.LoadError is { Length: > 0 } error)
        {
            // 清单读不出来就明说。空着列表比报错更糟 —— 用户会以为"原来没用第三方组件"。
            panel.Children.Add(new TextBlock
            {
                Text = "组件清单读取失败：" + error,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            });
        }

        AppendGroup(panel, "原生层（随安装包分发的运行库）", ThirdPartyNotices.Native, isNative: true);
        AppendGroup(panel, "托管层（.NET / NuGet 包）", ThirdPartyNotices.Managed, isNative: false);

        // 完整原文入口。开发期这个文件不存在（它由打包脚本生成、只落在发布目录），
        // 那时候不显示按钮，改说明去哪儿找 —— 不做一个点了没反应的按钮。
        if (ThirdPartyNotices.HasFullNotice)
        {
            var open = new HyperlinkButton
            {
                Content = "查看完整许可证原文（" + ThirdPartyNotices.FullNoticeFileName + "）",
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            open.Click += OnOpenFullNotice;
            panel.Children.Add(open);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "完整许可证原文未找到。发布包里它就在程序目录下：\n"
                       + ThirdPartyNotices.FullNoticePath,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.6,
                IsTextSelectionEnabled = true,
            });
        }

        // ContentDialog 在窄窗口 / 小屏上会溢出，内容自己带滚动条
        Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>
    /// 追加一组组件。空组（清单读取失败）直接跳过，不留一个只有标题的空壳。
    /// </summary>
    private static void AppendGroup(
        StackPanel panel,
        string title,
        IReadOnlyList<ThirdPartyComponent> components,
        bool isNative)
    {
        if (components.Count == 0)
        {
            return;
        }

        panel.Children.Add(Separator());

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        foreach (var component in components)
        {
            panel.Children.Add(RenderComponent(component, isNative));
        }
    }

    private static UIElement RenderComponent(ThirdPartyComponent component, bool isNative)
    {
        var box = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 4, 0, 4),
        };

        // 名字 + 许可证一行：名字是主信息，许可证紧随其后淡一点
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        head.Children.Add(new TextBlock
        {
            Text = component.Name,
            TextWrapping = TextWrapping.Wrap,
        });
        head.Children.Add(new TextBlock
        {
            Text = component.License,
            FontSize = 12,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (component.BuildOnly)
        {
            head.Children.Add(new TextBlock
            {
                Text = "（构建期工具，不随包分发）",
                FontSize = 12,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        box.Children.Add(head);

        if (isNative && component.Files is { Count: > 0 })
        {
            box.Children.Add(new TextBlock
            {
                Text = "分发文件：" + string.Join("、", component.Files),
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }

        if (!string.IsNullOrEmpty(component.Purpose))
        {
            box.Children.Add(new TextBlock
            {
                Text = component.Purpose,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return box;
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 2),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        Opacity = 0.25,
    };

    private void OnOpenFullNotice(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ThirdPartyNotices.FullNoticePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // 打不开就当没点过？不行 —— 用户点这个就是为了看原文，必须给个反馈。
            // 但这是一个已经 ShowAsync 的 ContentDialog，不能再弹一个；
            // 把提示写进按钮自身的 ToolTip 里，同时让按钮文字说明失败。
            if (sender is HyperlinkButton button)
            {
                button.Content = "打不开（" + ex.Message + "）—— 请手动打开程序目录下的 "
                                 + ThirdPartyNotices.FullNoticeFileName;
            }
        }
    }
}
