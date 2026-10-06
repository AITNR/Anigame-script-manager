// 键盘 toggle 状态（NumLock / CapsLock / ScrollLock）→ RDP TS_SYNCHRONIZE 位掩码。
//
// 为什么需要这个类（不是可选优化）：小键盘 0-9 与 Insert/Home/PgUp/End/PgDn/↑↓←→ 在
// Set1 扫描码里**完全共用**（Numpad1 与 End 同为 0x4F，Numpad0 与 Insert 同为 0x52……），
// 唯一的区分是 extended 位 **加上服务器侧的 NumLock 状态**。服务器拿不到本机锁状态就只能
// 按自己那份解释：NumLock 状态不一致时，按小键盘 1 会被当成 End，光标乱跳。
//
// 位定义见 FreeRDP input.h / MS-RDPBCGR 2.2.8.1.1.3.1.1.1，每位只在对应锁**开启**时置 1
// ——这是状态快照，不是「按一下」的动作（所以全关 = 传 0，是合法值而非「不更新」）。
//
// 纯逻辑、无 WinRT 依赖 —— _smoke 直接链入做单测（本机锁状态读取用 Win32 GetKeyState，
// 放在 RdpEmbeddedClient 侧调用，本类只做状态→位掩码的映射）。
using System.Runtime.InteropServices;

namespace YukinoChan.Models
{
    /// <summary>三个锁定键的状态快照。</summary>
    public readonly record struct KeyboardToggleState(bool NumLock, bool CapsLock, bool ScrollLock)
    {
        // FreeRDP input.h 的 KBD_SYNC_*（与 MS-RDPBCGR 一致）
        public const uint SyncScrollLock = 0x00000001;
        public const uint SyncNumLock = 0x00000002;
        public const uint SyncCapsLock = 0x00000004;
        public const uint SyncKanaLock = 0x00000008;
        public const uint All = SyncScrollLock | SyncNumLock | SyncCapsLock | SyncKanaLock;

        /// <summary>
        /// 转成 TS_SYNCHRONIZE 的位掩码。
        /// Kana 位不进快照：本机是中文布局，该锁无意义，恒为 0（留常量是为了 ABI 对齐可读）。
        /// </summary>
        public uint ToWireFlags()
        {
            uint f = 0;
            if (ScrollLock) f |= SyncScrollLock;
            if (NumLock) f |= SyncNumLock;
            if (CapsLock) f |= SyncCapsLock;
            return f;
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private const int VkNumLock = 0x90;
        private const int VkCapsLock = 0x14;
        private const int VkScrollLock = 0x91;

        /// <summary>
        /// 读取本机当前锁状态。GetKeyState 返回值的 **bit0** 才是开/关（1=开），
        /// 高位是「按下后是否切换过」——用错位会得到相反语义（这就是必须钉一条行为断言的原因）。
        /// </summary>
        public static KeyboardToggleState SnapshotLocal()
            => new(
                (GetKeyState(VkNumLock) & 1) != 0,
                (GetKeyState(VkCapsLock) & 1) != 0,
                (GetKeyState(VkScrollLock) & 1) != 0);
    }
}
