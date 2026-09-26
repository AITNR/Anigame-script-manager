// RDP 输入映射（计划书 §6 M3 / §5.3）。
// 纯函数、无 WinUI 依赖 —— _smoke 直接链入做完整单测表。
//
// 键盘：WinUI 的 KeyRoutedEventArgs.KeyStatus.ScanCode 已是硬件扫描码（Set 1），
//       IsExtendedKey 标扩展键；本类把它规范化为 RDP 口径（基础码 + extended 位），
//       并对少数键（PrintScreen / Pause / Numpad Enter 等）做修正。
//       RDP 协议只认扫描码不认 VK——这是 MS-RDPBCGR 2.2.8.1.1.2 的硬约束。
// 鼠标：控件内 DIP 坐标 → letterbox 反算 → 远程像素（0..remote-1，uint16 截断前 clamp）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace YukinoChan.Models
{
    public static class RdpInputMapper
    {
        private const uint MAPVK_VK_TO_VSC = 0;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        /// <summary>ScanCode 缺失（WinUI 3 某些路径 KeyStatus.ScanCode == 0）时的 Win32 兜底。</summary>
        private static ushort FallbackScanCodeFromVk(ulong virtualKey)
        {
            var v = MapVirtualKeyW((uint)(virtualKey & 0xFF), MAPVK_VK_TO_VSC);
            return (ushort)(v & 0xFF);
        }
        // ---- 固定 VK → (Set1 扫描码, extended) 表（仅扩展键与易错键；
        //      其余键走硬件扫描码直通或 MapVirtualKey 兜底，见 MapKey）----
        private static readonly Dictionary<ulong, (ushort Code, bool Extended)> FixedTable
            = new()
            {
                [0x25] = (0x4B, true),   // VK_LEFT
                [0x26] = (0x48, true),   // VK_UP
                [0x27] = (0x4D, true),   // VK_RIGHT
                [0x28] = (0x50, true),   // VK_DOWN
                [0x21] = (0x49, true),   // VK_PRIOR（PageUp）
                [0x22] = (0x51, true),   // VK_NEXT（PageDown）
                [0x24] = (0x47, true),   // VK_HOME
                [0x23] = (0x4F, true),   // VK_END
                [0x2D] = (0x52, true),   // VK_INSERT
                [0x2E] = (0x53, true),   // VK_DELETE
                [0xA3] = (0x1D, true),   // VK_RCONTROL
                [0xA5] = (0x38, true),   // VK_RMENU（Right Alt）
                [0x5B] = (0x5B, true),   // VK_LWIN
                [0x5C] = (0x5C, true),   // VK_RWIN
                [0x5D] = (0x5D, true),   // VK_APPS（菜单键）
                [0x2C] = (0x37, true),   // VK_SNAPSHOT（PrintScreen）
                [0x6F] = (0x35, true),   // VK_DIVIDE（小键盘 /）
            };

        /// <summary>
        /// 把一次按键规范化为 (基础扫描码, extended)。
        /// scanCode 为 WinUI KeyStatus.ScanCode（Set 1 硬件码）；virtualKey 为 Key 枚举整数值。
        /// 优先级：固定表 > 硬件码直通（+扩展位）> MapVirtualKey 兜底（WinUI ScanCode 常为 0）。
        /// </summary>
        public static (ushort ScanCode, bool Extended) MapKey(ushort scanCode, bool isExtendedKey, ulong virtualKey)
        {
            // 小键盘 Enter：硬件码 0x1C + IsExtendedKey=true（与主回车区分）
            if (virtualKey == 0x0D && isExtendedKey)
            {
                return (0x1C, true);
            }

            if (FixedTable.TryGetValue(virtualKey, out var fixedEntry))
            {
                return fixedEntry;
            }

            // WinUI 3 在不少来源下 KeyStatus.ScanCode == 0 —— 用 MapVirtualKey 从 VK 兜底
            if (scanCode == 0 && virtualKey != 0)
            {
                var fb = FallbackScanCodeFromVk(virtualKey);
                return fb != 0 ? (fb, isExtendedKey) : ((ushort)0, false);
            }

            return (scanCode, isExtendedKey);
        }

        /// <summary>letterbox（Stretch=Uniform）几何：缩放比与居中偏移。</summary>
        public static (double Scale, double OffsetX, double OffsetY) ComputeLetterbox(
            double viewWidth, double viewHeight, double remoteWidth, double remoteHeight)
        {
            if (viewWidth <= 0 || viewHeight <= 0 || remoteWidth <= 0 || remoteHeight <= 0)
            {
                return (0, 0, 0);
            }
            var scale = Math.Min(viewWidth / remoteWidth, viewHeight / remoteHeight);
            return (scale, (viewWidth - remoteWidth * scale) / 2, (viewHeight - remoteHeight * scale) / 2);
        }

        /// <summary>控件内 DIP 坐标 → 远程桌面像素（含 letterbox 反算 + 边界 clamp）。</summary>
        public static (ushort X, ushort Y) MapPointerToRemote(
            double dipX, double dipY, double viewWidth, double viewHeight,
            double remoteWidth, double remoteHeight)
        {
            var (scale, offX, offY) = ComputeLetterbox(viewWidth, viewHeight, remoteWidth, remoteHeight);
            if (scale <= 0)
            {
                return (0, 0);
            }
            var x = (dipX - offX) / scale;
            var y = (dipY - offY) / scale;
            var cx = Math.Clamp(x, 0, remoteWidth - 1);
            var cy = Math.Clamp(y, 0, remoteHeight - 1);
            return ((ushort)Math.Round(cx), (ushort)Math.Round(cy));
        }

        /// <summary>滚轮 delta（WinUI MouseWheelDelta，+120=向上一步）→ (flags, encodedDelta)。
        /// MS-RDPBCGR：wheelDelta 以 120 为一步，取值范围 0..0xFF；向下再叠 WHEEL_NEGATIVE。</summary>
        public static (uint Flags, uint EncodedDelta) MapWheel(int wheelDelta)
        {
            var steps = (uint)Math.Clamp(Math.Abs(wheelDelta) / 120, 0, 0xFF);
            if (steps == 0)
            {
                return (0, 0);
            }
            uint flags = wheelDelta > 0 ? 0u : YcnPointerFlags.WheelNegative;
            return (YcnPointerFlags.Wheel | flags, steps);
        }
    }

    /// <summary>PTR_FLAGS 组合常量（与 C# interop 的 YcnPtrFlags 同值；单测引用用，避免 _smoke 链 interop）。</summary>
    public static class YcnPointerFlags
    {
        public const uint Move = 0x0800;
        public const uint Down = 0x8000;
        public const uint Button1 = 0x1000;
        public const uint Button2 = 0x2000;
        public const uint Button3 = 0x4000;
        public const uint Wheel = 0x0200;
        public const uint WheelNegative = 0x0100;
    }
}
