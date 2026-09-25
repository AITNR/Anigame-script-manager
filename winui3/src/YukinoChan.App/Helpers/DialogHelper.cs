// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace YukinoChan.Helpers;

/// <summary>
/// 文件选择器与消息框封装。
/// 必须用 Windows App SDK 1.8 的新选择器（Microsoft.Windows.Storage.Pickers）：
/// 旧的 Windows.Storage.Pickers 在"以管理员身份运行"的进程里会静默失败（点了没反应），
/// 对过滤器格式也苛刻（"." 这类写法直接抛异常，被 async void 吞掉后同样表现为没反应）。
/// 新 API 通过 WindowId 关联窗口，且不设过滤器时默认显示所有文件，两个坑都没有。
/// </summary>
public static class DialogHelper
{
    public static async Task<string?> PickSaveFileAsync(string suggestedName, params (string Label, string Pattern)[] filters)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return null;
        }

        try
        {
            var picker = new FileSavePicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };

            // 不设置 FileTypeChoices 时对话框本身就允许"所有文件 (*.*)"，通配项无需也不能显式添加
            foreach (var (label, pattern) in filters)
            {
                if (IsWildcard(pattern))
                {
                    continue;
                }

                picker.FileTypeChoices.Add(label, new List<string> { pattern });
            }

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件选择器", $"保存对话框出错：{ex.Message}");
            return null;
        }
    }

    public static async Task<string?> PickOpenFileAsync(params (string Label, string Pattern)[] filters)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return null;
        }

        try
        {
            var picker = new FileOpenPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            // 不设置 FileTypeFilter 时默认显示所有文件 (*.*)；通配项跳过，只保留具体扩展名的过滤
            foreach (var (_, pattern) in filters)
            {
                if (IsWildcard(pattern))
                {
                    continue;
                }

                picker.FileTypeFilter.Add(pattern);
            }

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件选择器", $"打开对话框出错：{ex.Message}");
            return null;
        }
    }

    private static bool IsWildcard(string pattern) =>
        pattern is "." or "*" or "*.*";

    public static async Task<ContentDialogResult> ShowMessageAsync(
        string title,
        string message,
        string closeText = "知道了",
        string? primaryText = null,
        string? secondaryText = null)
    {
        if (App.MainWindow is null)
        {
            return ContentDialogResult.None;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeText,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            XamlRoot = App.MainWindow.Content.XamlRoot,
        };

        return await dialog.ShowAsync();
    }
}
