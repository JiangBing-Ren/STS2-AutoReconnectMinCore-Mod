using System;

namespace AutoReconnect.Scripts;

/// <summary>
/// [v0.9.10] 客机连接稳定性监护。
///
/// 当本机（主机）刚踢掉客机（StateDivergence）或检测到客机掉线时，标记一段“不稳定窗口”。
/// 在此期间若主机尝试进入战斗类节点（Monster/Elite/Boss），Main 模组的战斗入口守卫
/// 会暂缓进入——因为基游戏在客机槽位半残（null player，正断开/重连中）时建立 BOSS 战斗会抛
/// <c>ArgumentNullException: player</c> → 黑屏（见 2026-08-10 实测 godot.log:35155-35168）。
///
/// 这是【触发面缓解】，不是修基游戏崩溃本身：不稳定窗口过后主机可正常进入战斗节点。
/// 窗口长度覆盖一次踢人/掉线后客机从“正在断开”到“完全移除或重连就绪”的过渡期。
/// </summary>
public static class ClientStateGuard
{
    // 默认不稳定窗口：覆盖客机从“正在断开”到“完全移除或重连就绪”的过渡期。
    private static readonly TimeSpan DefaultUnstable = TimeSpan.FromSeconds(6);
    private static DateTime _unstableUntilUtc = DateTime.MinValue;

    public static void MarkUnstable(TimeSpan? duration = null)
    {
        var d = duration ?? DefaultUnstable;
        var until = DateTime.UtcNow + d;
        if (until > _unstableUntilUtc) _unstableUntilUtc = until;
        Diag.Log($"[ClientStateGuard] 标记客机状态不稳定，持续到 {until:HH:mm:ss}（约 {d.TotalSeconds:0}s）。");
    }

    public static bool IsUnstable => DateTime.UtcNow < _unstableUntilUtc;
}
