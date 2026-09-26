// WH_KEYBOARD_LL 低级键盘钩子（计划书 §5.3.1 / §6 M3）。
// 全屏且画面聚焦时：所有击键（含 Win / Alt+Tab / Ctrl+Esc 系统组合）转投远端并拦截本地；
// **生命周期纪律**：退出全屏 / 失焦 / 断开连接 → 立即 Remove()（挂/摘各写一条日志）。
// 注意：必须在带消息泵的线程（UI 线程）Install；回调同步执行，不可做耗时操作。
using System;
using System.Runtime.InteropServices;

namespace YukinoChan.Services
{
    /// <summary>
    /// 返回 true = 拦截该键（已转投远端）；false = 放行给本地系统。
    /// 参数：(virtualKey, hardwareScanCode, isExtendedKey, isKeyDown)。
    /// </summary>
    internal delegate bool LowLevelKeyInterceptor(ulong virtualKey, ushort scanCode, bool isExtendedKey, bool isKeyDown);

    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private IntPtr _hook;
        private readonly LowLevelKeyInterceptor _interceptor;
        private readonly Action<string> _log;

        private LowLevelKeyboardProc? _proc; // 保活，防 GC

        public bool Active => _hook != IntPtr.Zero;

        public LowLevelKeyboardHook(LowLevelKeyInterceptor interceptor, Action<string> log)
        {
            _interceptor = interceptor ?? throw new ArgumentNullException(nameof(interceptor));
            _log = log ?? (_ => { });
        }

        public void Install()
        {
            if (Active)
            {
                return;
            }
            _proc = HookProc;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            _log(_hook != IntPtr.Zero ? "键盘钩子已挂（全屏接管生效）" : "键盘钩子挂载失败");
        }

        public void Remove()
        {
            if (_hook == IntPtr.Zero)
            {
                return;
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _log("键盘钩子已摘（退出全屏接管）");
        }

        private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                // KBDLLHOOKSTRUCT：vkCode(0) / scanCode(8) / flags(16)，LLKHF_EXTENDED = 0x01
                var vk = (ulong)Marshal.ReadInt32(lParam);
                var scanCode = (ushort)Marshal.ReadInt32(lParam, 8);
                var flags = Marshal.ReadInt32(lParam, 16);
                var extended = (flags & 0x01) != 0;
                var msg = wParam.ToInt64();
                var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                try
                {
                    if (_interceptor(vk, scanCode, extended, isDown))
                    {
                        return new IntPtr(1); // 拦截
                    }
                }
                catch
                {
                    // 回调异常绝不外泄，放行保底
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            Remove();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    }
}
