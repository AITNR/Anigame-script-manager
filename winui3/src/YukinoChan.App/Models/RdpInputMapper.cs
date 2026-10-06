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

        /// <summary>单次滚轮事件的绝对旋转单位上限。正常一格=120，64 格=7680 单位已经远超
        /// 人手能一帧滚出的量；再大的 delta 一律夹住，避免失控输入灌爆输入队列。</summary>
        private const long MaxTotalWheelUnits = YcnPointerFlags.WheelRotationMax * 64L;

        /// <summary>滚轮 delta（WinUI MouseWheelDelta，+120=向上一步）→ (flags, rotationUnits, repeats)。
        ///
        /// ⚠️⚠️ **单位是「旋转单位」不是「格数」**（MS-RDPBCGR 2.2.8.1.1.1.1 WheelRotationMask）。
        /// 服务器按 **120 旋转单位 = 1 格** 解释这个字段（FreeRDP 官方示例 `rotationUnits = 0x0078`）。
        /// 曾经的 bug：这里发的是 `|delta| / 120`，也就是格数 —— 1 格只发 1 个单位，
        /// 服务器当成 1/120 格，症状是**方向对了但上滑距离只有正常的 1/120**（用户实测反馈）。
        /// WinUI 的 MouseWheelDelta 本身就是 120/格，**直接当旋转单位用，不要再除 120**。
        /// 顺带好处：高精度触控板的小 delta（±15/±30 等亚格量）不再被当噪声丢掉，能平滑传给服务器。
        ///
        /// ⚠️ **旋转单位绝不能左移**：bit8=WHEEL_NEGATIVE、bit9=WHEEL，移上去等于改写方向标志本身
        /// （曾经的 bug：调用点写 `flags | (units &lt;&lt; 8)`，向上/向下都退化成 0x0300）。
        ///
        /// ⚠️ **超出 0xFF 的量不能 clamp 了事**：bit8 归 WHEEL_NEGATIVE 所有，每事件最多 0xFF 单位，
        /// 而一格就要 120 单位（两格 240 已超）。所以这里**拆成 repeats 个事件重发**并把总量均摊，
        /// 保证滚过的总距离与本地一致 —— 否则快速滚动/触控板甩动依旧表现为「距离太短」。
        /// </summary>
        public static (uint Flags, uint RotationUnits, int Repeats) MapWheel(int wheelDelta)
        {
            // Math.Abs(int.MinValue) 会溢出成负数，必须先转 long
            var total = Math.Abs((long)wheelDelta);
            if (total == 0)
            {
                return (0, 0, 0);
            }

            if (total > MaxTotalWheelUnits)
            {
                total = MaxTotalWheelUnits;
            }

            // 向上取整的事件数，再把总量均摊到各事件 —— 每片都非零且都 ≤ WheelRotationMax
            const uint cap = YcnPointerFlags.WheelRotationMax;
            var repeats = (int)((total + cap - 1) / cap);
            if (repeats < 1)
            {
                repeats = 1;
            }

            var units = (uint)((total + repeats - 1) / repeats);

            uint flags = wheelDelta > 0 ? 0u : YcnPointerFlags.WheelNegative;
            return (YcnPointerFlags.Wheel | flags, units, repeats);
        }
    }

    /// <summary>PTR_FLAGS 组合常量（与 FreeRDP input.h 同值；单测引用用，避免 _smoke 链 interop）。
    /// 完整定义见 MS-RDPBCGR 2.2.8.1.1.1.1 TS_POINTER_EVENT。</summary>
    public static class YcnPointerFlags
    {
        public const uint Move = 0x0800;
        public const uint Down = 0x8000;
        public const uint Button1 = 0x1000;
        public const uint Button2 = 0x2000;
        public const uint Button3 = 0x4000;
        public const uint Wheel = 0x0200;
        public const uint WheelNegative = 0x0100;
        public const uint WheelHorizontal = 0x0400;

        /// <summary>滚轮旋转单位字段（协议名义掩码 0x01FF = bit0-8）。与上面三个标志位**叠加**而非互斥——
        /// 协议规定滚轮事件里只有 WHEEL/HWHEEL + WHEEL_NEGATIVE + 本字段有效。
        /// ⚠️ 名义 9 位但 bit8 被 WHEEL_NEGATIVE 占着，故实际可用数量位只有 bit0-7。</summary>
        public const uint WheelRotationMask = 0x01FF;

        /// <summary>单次滚轮事件能携带的旋转单位上限 0xFF。
        /// 协议写的是 0x01FF，但 bit8 归 WHEEL_NEGATIVE 所有 —— 取值越过 0xFF 会把方向标志顶开
        /// （向上变向下）。所以 255 是硬上限：够放两格（240），再多的量由 MapWheel 拆成多次事件。</summary>
        public const uint WheelRotationMax = 0xFF;

        /// <summary>Windows 标准滚轮单位：1 格（detent）= 120 旋转单位。</summary>
        public const uint WheelUnitsPerDetent = 120;
    }
}
