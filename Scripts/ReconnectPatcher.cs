using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts;

/// <summary>
/// Captures host info when a multiplayer run is set up.
/// Triggered on both fresh starts and loaded runs.
/// </summary>
[HarmonyPatch(typeof(RunManager), "SetUpSavedMultiplayer")]
internal static class SetUpSavedMultiplayerPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Diag.Log("SetUpSavedMultiplayer patch fired!");
        HostInfoTracker.CaptureFromGame();
    }
}

/// <summary>
/// Captures host info when a run is launched (covers edge cases).
/// </summary>
[HarmonyPatch(typeof(RunManager), "Launch")]
internal static class RunLaunchedPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Diag.Log("Launch patch fired!");
        HostInfoTracker.CaptureFromGame();
    }
}

/// <summary>
/// v0.6.1 (Bug A 修复) — 捕获真实的 Steam 大厅 ID。
/// 客户端每次连接大厅都会经过 SteamClient.ConnectToLobby(ulong lobbyId, CancellationToken)：
///   - 通过好友加入：ConnectToLobbyOwnedByFriend → ConnectToLobby(真实大厅ID)
///   - 直接加入大厅：SteamClientConnectionInitializer.FromLobby → ConnectToLobby
/// 长断线后 SteamFriends.GetFriendGamePlayed 会失败（主机好友游戏信息不再报告有效 STS2 大厅），
/// 使 FromPlayer 路径在 ConnectToLobbyOwnedByFriend 直接返回 InvalidJoin。
/// 捕获此 lobbyId 后，重连改用 FromLobby(lobbyId) 直连，跳过 GetFriendGamePlayed 检查。
/// </summary>
[HarmonyPatch]
internal static class SteamClientConnectToLobbyCapturePatch
{
    [HarmonyTargetMethod]
    private static MethodInfo? TargetMethod()
    {
        var steamClientType = System.Type.GetType(
            "MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient, MegaCrit.Sts2.Core",
            throwOnError: false)
            ?? AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient");
        if (steamClientType == null) return null;

        return AccessTools.Method(steamClientType, "ConnectToLobby",
                   new[] { typeof(ulong), typeof(System.Threading.CancellationToken) })
               ?? AccessTools.Method(steamClientType, "ConnectToLobby");
    }

    [HarmonyPrefix]
    private static void Prefix(ulong lobbyId)
    {
        try
        {
            HostInfoTracker.CaptureLobbyId(lobbyId);
        }
        catch { }
    }
}

/// <summary>
/// v0.1.8 — Track whether the disconnect was user-initiated (normal exit)
/// to avoid triggering reconnection when the user deliberately exits.
/// Patches SteamClient.DisconnectFromHostInternal which has a selfInitiated bool.
/// </summary>
[HarmonyPatch]
internal static class DisconnectSelfInitiatedPatch
{
    [HarmonyTargetMethod]
    private static MethodInfo TargetMethod()
    {
        var steamClientType = System.Type.GetType(
            "MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient, MegaCrit.Sts2.Core",
            throwOnError: false);
        if (steamClientType != null)
        {
            var method = steamClientType.GetMethod("DisconnectFromHostInternal",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null) return method;
        }

        // Fallback: resolve at patch time
        return AccessTools.Method("MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient:DisconnectFromHostInternal");
    }

    [HarmonyPrefix]
    private static void Prefix(object[] __args)
    {
        try
        {
            // __args contains: (SteamDisconnectionReason reason, string debugReason, bool now, bool selfInitiated)
            // The selfInitiated param is the last bool
            if (__args.Length >= 4 && __args[3] is bool selfInitiated && selfInitiated)
            {
                Diag.Log("DisconnectSelfInitiated: User-initiated disconnect detected, suppressing reconnection");
                HostInfoTracker.DisconnectWasSelfInitiated = true;
            }
        }
        catch { }
    }
}

/// <summary>
/// v0.1.8 — Patches RunManager.LocalPlayerDisconnected(NetErrorInfo) instead of
/// ReturnToMainMenuWithError. This is more precise: only fires on actual disconnects
/// (not other error scenarios), and we can check the error info.
///
/// v0.9.7 —— 两处关键修复：
///   ① 形参类型必须与原方法**逐字一致**：<c>RunManager.LocalPlayerDisconnected(NetErrorInfo info)</c>
///      是值类型（struct）参数。此前写成 <c>NetErrorInfo? info</c>（Nullable&lt;T&gt;），
///      Harmony 直接 Ldarg 把 32 字节的 NetErrorInfo 塞进 Nullable&lt;NetErrorInfo&gt; 槽位，
///      字段整体错位一格 —— 于是 <c>_connectionReason</c> 读到的其实是 <c>_steamReason</c> 的字节，
///      GetReason() 在 ConnectionFailureReason 的 switch 上抛
///      “Non-exhaustive switch expression … Unmatched value was 1012”。
///      改回非空 struct 后，断开原因才是真的。
///   ② 主机检查点回退会用 <see cref="NetError.StateDivergence"/> 把所有客机请离
///      （BaseGame RunManager.cs:1549）。这类断开**必须**走无缝整局重建：
///      原版流程只会把客机丢回主菜单，而主菜单重连在主机处于战斗中时会被直接拒绝。
///      所以 StateDivergence 一律强制走 ReconnectRunner，忽略「用户主动退出」标志与自动重连总开关。
/// </summary>
[HarmonyPatch(typeof(RunManager), "LocalPlayerDisconnected")]
internal static class LocalPlayerDisconnectedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NetErrorInfo info)
    {
        try
        {
            var reason = ReconnectDiagnostics.TryGetReason(info);
            bool isDivergence = reason == NetError.StateDivergence;

            Diag.Log($"LocalPlayerDisconnected fired: WasInMultiplayer={HostInfoTracker.WasInMultiplayer} " +
                     $"IsReconnecting={HostInfoTracker.IsReconnecting} SelfInitiated={HostInfoTracker.DisconnectWasSelfInitiated} " +
                     $"Reason={(reason.HasValue ? reason.Value.ToString() : "读不出")}");

            if (HostInfoTracker.IsReconnecting)
            {
                Diag.Log("Already reconnecting, blocking");
                return false;
            }

            if (!HostInfoTracker.WasInMultiplayer)
            {
                Diag.Log("Not multiplayer, allowing normal flow");
                return true;
            }

            // [v0.9.32-core] 正常结束 / 退出对局，绝不触发重连：
            //   ① 主机是联机权威，没有可重连的“服务器”；主机的 LocalPlayerDisconnected 只代表本局结束，
            //      重连既无意义又会错误弹出“重连尝试”框（主机无主机 Steam ID / 大厅可连）。
            //   ② QuitGameOver / Quit / HostAbandoned 都表示对局或会话“正常结束”（失败/胜利/主动退出/主机放弃），
            //      不存在“在途可重连对局”，重连必 InvalidJoin。这与 DisconnectWasSelfInitiated 同理，
            //      但由 reason 显式判定更可靠——QuitGameOver 不会置 SelfInitiated 标志，之前因此漏判而弹重连。
            if (IsLocalPlayerHost())
            {
                Diag.Log("Local 是主机：权威端无需重连，放行原版对局结束流程。");
                HostInfoTracker.Reset();
                return true;
            }
            if (reason == NetError.QuitGameOver || reason == NetError.Quit || reason == NetError.HostAbandoned)
            {
                Diag.Log($"正常结束/退出（{reason}），放行原版流程，不弹重连。");
                HostInfoTracker.Reset();
                return true;
            }

            // 用户主动退出（菜单/设置）→ 放行原版流程。
            // 但 StateDivergence 绝不可能是本地主动退出，标志若为 true 只可能是残留，直接无视。
            if (HostInfoTracker.DisconnectWasSelfInitiated && !isDivergence)
            {
                Diag.Log("User-initiated exit, allowing normal flow");
                HostInfoTracker.Reset();
                return true;
            }

            if (isDivergence)
            {
                Diag.Log("[AutoReconnect] 断开原因 = StateDivergence（多半是主机做了检查点回退）。" +
                         "这类断开原版处理不了（只会退回主菜单，且主机在战斗中时主菜单重连会被拒），" +
                         "强制走对局内无缝重连 + 整局重建。");
            }
            else
            {
                Diag.Log("Unexpected disconnect — starting reconnection flow");

                // v0.7.2 —— 自动重连总开关。关闭时，放行原版 LocalPlayerDisconnected 流程，
                // 退回主菜单，由 MenuRejoinFlow 走主机存档对局（原生读档）重建。
                // 注意：StateDivergence 不受此开关约束（见上分支）。
                if (!ReconnectService.AutoReconnectEnabled)
                {
                    Diag.Log("[AutoReconnect] 自动重连已关闭，放行原版流程退回主菜单（MenuRejoin 语境）。");
                    return true;
                }
            }

            HostInfoTracker.IsReconnecting = true;

            var reconnectNode = new ReconnectRunner();
            reconnectNode.Name = "AutoReconnectMin_Runner";
            // [v0.9.28-core] 恢复 v0.9.0-min 行为：仅当断开原因是 StateDivergence 才强制整局重建（慢路径）。
            // 配合 Main 的「客机掉线 → 主机自动回退到检查点」：主机每次回退都用 StateDivergence 请离客机，
            // 因此普通掉线重连一律 isDivergence=true → 慢路径从检查点重建 → 主机、客机 RNG 计数一致 → 不再分歧。
            // 非 StateDivergence 的断开（如僵尸/超时、主机未回退）走快路径（只换 NetService），
            // 此时若慢路径反而会带上“主机实时态”而非检查点 → RNG 错位 → 校验分歧死循环（Bug2 的真凶）。
            // 备注：快路径（只换 NetService）在游戏中战斗同步 handler 注册于旧 NetService 的场景下有“假连”风险，
            // 但 StateDivergence 触发后主机已回退、客机随后必再收到一次 StateDivergence 走慢路径自愈，
            // 故恢复 isDivergence 比 v0.9.26 的“始终慢路径”更安全（后者在主机未回退时会陷入分歧死循环）。
            reconnectNode.ForceFullRebuild = isDivergence;
            // 断线瞬间 NetService 仍在，捕获主机 Steam ID 供 FromPlayer 重建连接。
            try
            {
                if (RunManager.Instance is { NetService: MegaCrit.Sts2.Core.Multiplayer.NetClientGameService csv })
                    reconnectNode.HostSteamId = csv.HostNetId;
            }
            catch { }
            AddToSceneTree(reconnectNode);

            return false; // Block normal LocalPlayerDisconnected flow
        }
        catch (Exception ex)
        {
            Diag.Log($"LocalPlayerDisconnected patch exception: {ex}");
            return true;
        }
    }

    /// <summary>
    /// [v0.9.32-core] 判定本地玩家是否为联机主机。
    /// 主机是权威端，没有可重连的“服务器”，其 LocalPlayerDisconnected 只代表本局结束，不应触发重连。
    /// 通过 RunManager.Instance.NetService 的实际运行时类型判定：主机为 NetHostGameService，客机为 NetClientGameService
    /// （日志 CaptureFromGame 的 DUMP 已证实两者类型名）。用类型名比较，避免依赖 NetGameType 枚举命名空间。
    /// 任何读取失败都保守返回 false（交由下方 reason 抑制兜底，避免误判成客机而错误重连）。
    /// </summary>
    private static bool IsLocalPlayerHost()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return false;
            var ns = rm.NetService;
            if (ns == null) return false;
            return ns.GetType().Name == "NetHostGameService";
        }
        catch
        {
            return false;
        }
    }

    private static void AddToSceneTree(Godot.Node node)
    {
        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        tree?.Root.AddChild(node);
    }
}
