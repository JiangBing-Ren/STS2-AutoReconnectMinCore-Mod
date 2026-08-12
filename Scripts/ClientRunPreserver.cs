using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.9.34 — 客机「主动退出对局」后常驻内存的活 run 持有器。
///
/// ## 需求（用户 2026-08-12 硬性语义）
/// 客机主动退出对局时，**直接退回主菜单、不停在「重连中」浮层自动重拨**；但 run 不销毁，
/// 常驻内存，等主机从 Steam 再次邀请时**零重建复用（保真）**。run 仅在两种情况下销毁：
/// ① 进入新多人对局；② 关闭游戏。
///
/// ## 实现原理（已反编译验证）
/// 主动退出时（<c>LocalPlayerDisconnected</c> reason=Quit / <c>DisconnectWasSelfInitiated</c>），
/// 在 <c>RunManager.CleanUp</c> 把 <c>State</c> 置 null 之前，捕获**活 RunState 对象引用**存此处。
/// <c>CleanUp</c> 只把 <c>State = null</c>、并 Dispose 各 Synchronizer / NetService，
/// **并不 Dispose RunState 数据对象本身**（地图 / RNG / 玩家 / 当前房间 / Modifier / 事件效果…）。
/// 因此本处持有的引用使对象不被 GC 回收，重连时把同一对象交给
/// <c>SetUpSavedMultiplayer(state, …)</c> → 直接 <c>State = state</c>，地图光标 / RNG / 事件已结算效果
/// 全部原样保留（零重建、根治菜单重建节点切换黑屏）。
///
/// ## 同一局判定
/// 用 <c>HostInfoTracker.LobbyId</c> 比对：主动退出时记录 lobby id，主机再次邀请（同一 Steam lobby）
/// 时 lobby id 一致才复用，避免误把旧 run 复用进一个完全不同的新对局。
/// </summary>
public static class ClientRunPreserver
{
    /// <summary>主动退出时捕获的活 RunState 对象引用（零重建重锚的源）。</summary>
    public static RunState? PreservedRun { get; private set; }

    /// <summary>捕获时所在 Steam lobby id，用于重邀时判定是否为同一局。</summary>
    public static string? PreservedLobbyId { get; private set; }

    /// <summary>是否存在可复用的保留 run。</summary>
    public static bool HasPreserved => PreservedRun != null;

    /// <summary>
    /// 主动退出时调用：捕获当前活 run + 当前 lobby id。
    /// 必须在 <c>RunManager.CleanUp()</c>（置 State=null）之前调用。
    /// </summary>
    public static void Capture()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                Diag.Log("[ClientRunPreserver] Capture 失败：RunManager.State 已为空，无活 run 可保留。");
                return;
            }
            PreservedRun = state;
            PreservedLobbyId = HostInfoTracker.LobbyId;
            Diag.Log($"[ClientRunPreserver] 已捕获活 run（lobby={PreservedLobbyId}），待主机邀请零重建复用。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[ClientRunPreserver] Capture 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 重邀的 lobby 是否与保留 run 的 lobby 一致（即「同一局」）。
    /// 任一侧 lobby id 为空（捕获失败 / 当前未连）时保守返回 false，交由常规重建路径。
    /// </summary>
    public static bool MatchesCurrentLobby(string? currentLobbyId)
    {
        if (PreservedRun == null) return false;
        if (string.IsNullOrEmpty(PreservedLobbyId) || string.IsNullOrEmpty(currentLobbyId)) return false;
        return PreservedLobbyId == currentLobbyId;
    }

    /// <summary>清除保留 run（已消费 / 进入新对局 / lobby 不一致）。</summary>
    public static void Clear()
    {
        if (PreservedRun != null || PreservedLobbyId != null)
            Diag.Log("[ClientRunPreserver] 清除保留 run（已消费 / 进入新对局 / 不同 lobby）。");
        PreservedRun = null;
        PreservedLobbyId = null;
    }
}
