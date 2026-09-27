// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using YukinoChan.Services;

namespace YukinoChan.Helpers;

/// <summary>
/// 文件选择器与消息框封装。
/// 必须用 Windows App SDK 1.8 的新选择器（Microsoft.Windows.Storage.Pickers）：
/// 旧的 Windows.Storage.Pickers 在"以管理员身份运行"的进程里会静默失败（点了没反应），
/// 对过滤器格式也苛刻（"." 这类写法直接抛异常，被 async void 吞掉后同样表现为没反应）。
/// 新 API 通过 WindowId 关联窗口，且不设过滤器时默认显示所有文件，两个坑都没有。
///
/// 【对话框纪律】WinUI 同一时刻只允许一个 ContentDialog 打开：第二个 <c>ShowAsync</c> 直接抛
/// COMException（"Only a single ContentDialog can be open at any time"）。而工程里大量调用是
/// <c>_ = DialogHelper.ShowMessageAsync(...)</c> 这种"发了不管"的写法 —— 异常没人接，
/// 未观察异常直接掀掉进程。所以**所有** ContentDialog 都必须从 <see cref="ShowAsync"/> 走：
/// 排队进入 + 异常就地吞掉落盘，绝不再让调用方拿到异常。
/// </summary>
public static class DialogHelper
{
    /// <summary>全局对话框闸门：保证同一时刻只有一个 ContentDialog 存活。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeText,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
        };

        return await ShowAsync(dialog);
    }

    /// <summary>
    /// 唯一允许打开 ContentDialog 的入口：补 XamlRoot → 排队 → 显示 → 吞异常。
    /// 调用方拿到的永远是结果（失败时是 <see cref="ContentDialogResult.None"/>），不会拿到异常。
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is null && App.MainWindow?.Content?.XamlRoot is { } root)
        {
            dialog.XamlRoot = root;
        }

        // 窗口还没建好 / 已经关掉：直接放弃，别让调用方因为"没反应"再抛一次
        if (dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 关键：吞掉。调用方多是 fire-and-forget，抛出去就是未观察异常 = 崩溃。
            AppPaths.WriteStartupError(ex);
            return ContentDialogResult.None;
        }
        finally
        {
            Gate.Release();
        }
    }
}
