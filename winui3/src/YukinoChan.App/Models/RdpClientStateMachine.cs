// RDP 内嵌客户端状态机（计划书 §4 / §6 M2）
// 纯函数、无 WinUI 依赖 —— _smoke 直接链入做迁移表单测。
namespace YukinoChan.Models
{
    /// <summary>内嵌客户端状态。</summary>
    public enum RdpClientState
    {
        /// <summary>未连接（初始/复位）。</summary>
        Idle,
        /// <summary>连接进行中（ycn_rdp_connect 已发起）。</summary>
        Connecting,
        /// <summary>已连接（画面在流，可注入输入）。</summary>
        Connected,
        /// <summary>已断开（本端主动或对端关闭；可再次 Connect）。</summary>
        Disconnected,
        /// <summary>失败（连接失败等；可再次 Connect）。</summary>
        Failed,
    }

    /// <summary>驱动状态迁移的事件。</summary>
    public enum RdpClientEvent
    {
        /// <summary>调用方请求连接（含重连）。</summary>
        ConnectRequested,
        /// <summary>原生侧 on_connected。</summary>
        NativeConnected,
        /// <summary>原生侧 on_disconnected（主动/被动/错误共用）。</summary>
        NativeDisconnected,
        /// <summary>连接发起失败（ycn_rdp_connect 返回负值）。</summary>
        ConnectRejected,
    }

    /// <summary>状态迁移表（纯函数）。非法迁移保持原状态并返回 false。</summary>
    public static class RdpClientStateMachine
    {
        public static RdpClientState Transition(RdpClientState state, RdpClientEvent evt)
        {
            return (state, evt) switch
            {
                (RdpClientState.Idle, RdpClientEvent.ConnectRequested) => RdpClientState.Connecting,
                (RdpClientState.Disconnected, RdpClientEvent.ConnectRequested) => RdpClientState.Connecting,
                (RdpClientState.Failed, RdpClientEvent.ConnectRequested) => RdpClientState.Connecting,

                (RdpClientState.Connecting, RdpClientEvent.NativeConnected) => RdpClientState.Connected,
                (RdpClientState.Connecting, RdpClientEvent.NativeDisconnected) => RdpClientState.Failed,
                (RdpClientState.Connecting, RdpClientEvent.ConnectRejected) => RdpClientState.Failed,

                (RdpClientState.Connected, RdpClientEvent.NativeDisconnected) => RdpClientState.Disconnected,

                // 非法迁移：保持原状态
                _ => state,
            };
        }

        /// <summary>迁移是否合法（单测用）。</summary>
        public static bool IsValid(RdpClientState from, RdpClientEvent evt)
            => Transition(from, evt) != from || IsTerminalFor(evt, from);

        private static bool IsTerminalFor(RdpClientEvent evt, RdpClientState state)
            => (state, evt) switch
            {
                (RdpClientState.Connected, RdpClientEvent.NativeConnected) => true,
                (RdpClientState.Idle, RdpClientEvent.NativeDisconnected) => true,
                _ => false,
            };
    }
}
