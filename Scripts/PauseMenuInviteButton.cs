using System;
using System.Reflection;
using JmcModLib.UI.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.9.25-core — 局内暂停菜单「邀请重连」按钮（基于 JML 的 <see cref="PauseMenuButtonAttribute"/>）。
///
/// 语义：房主（或任意联机玩家）在暂停菜单点击「邀请重连」→ 解析当前 Steam 大厅 ID →
/// 弹出 Steam 好友邀请面板（<see cref="SteamFriends.ActivateGameOverlayInviteDialog"/>）。
/// 掉线队友在 Steam 弹窗里点接受 → 回到原大厅 → 走既有自动重连/回局流程，无需手动重新开黑。
///
/// 为什么用「弹 Steam 邀请面板」而不是「自动给指定玩家发邀请」：
///   · 面板由 Steam 官方 UI 渲染，玩家可自由选择好友（含掉线者），无需维护「谁掉线了」的名单；
///   · 不依赖反射去枚举大厅成员，稳；Steam 会正确处理「已在局内/不可邀请」等情况。
///
/// 注意（JML 约定）：attribute 扫描由 JML 的 AttributeRouter 在 Core 注册时自动完成
/// （订阅 ModRegistry.OnRegistered → ScanAssembly），无需手动注册。
/// </summary>
public static class PauseMenuInviteButton
{
    /// <summary>JML 按钮去重键（同 assembly 内唯一；不同 mod 可同名共存）。</summary>
    public const string ButtonKey = "autoreconnectmincore.pause_menu.invite";

    /// <summary>
    /// 按钮点击入口。
    /// JML 要求：static、非泛型、0 或 1 参数（PauseMenuButtonContext）、返回 void 或 Task。
    /// 这里用 0 参数 + void：公开签名不暴露任何 JML 类型，
    /// 保证引用 Core 的 AutoReconnectMin (Main) 编译时无需解析 JML。
    /// </summary>
    [PauseMenuButton("邀请重连", Key = ButtonKey, Order = 10, CloseMenuOnClick = true)]
    public static void InviteFromPauseMenu()
    {
        try
        {
            var lobbyId = ResolveCurrentLobbyId();
            if (lobbyId == 0)
            {
                Diag.Log("[PauseMenuInvite] 当前不在联机对局中（无大厅 ID），忽略邀请。");
                ReconnectDiagnostics.ShowPopup(
                    "邀请重连",
                    "当前不在联机对局中，无法发送邀请。",
                    withRetryButton: false,
                    dedupKey: "pause_menu_invite_no_lobby");
                return;
            }

            Diag.Log($"[PauseMenuInvite] 弹出 Steam 邀请面板（lobby {lobbyId}）...");
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobbyId));
        }
        catch (Exception ex)
        {
            Diag.Log($"[PauseMenuInvite] 触发异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析当前 Steam 大厅 ID（主机/客机通用）：
    ///   1) HostInfoTracker.LobbyId —— 客机在 ConnectToLobby 时已捕获（最可靠）；
    ///   2) RunManager.NetService → NetHostGameService.NetHost (SteamHost) —— 主机端从 Steam 传输层提取；
    ///   3) RunManager.RunLobby 反射兜底 —— 防御性遍历常见命名。
    /// 找不到返回 0。
    /// </summary>
    public static ulong ResolveCurrentLobbyId()
    {
        // 1) 客机连接时已捕获的大厅 ID
        if (ulong.TryParse(HostInfoTracker.LobbyId, out var tracked) && tracked != 0)
            return tracked;

        // 2) 主机：从 NetService → NetHost (SteamHost) 提取大厅 ID
        try
        {
            var rm = RunManager.Instance;
            if (rm != null)
            {
                var netService = rm.GetType().GetProperty("NetService",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(rm);
                if (netService != null)
                {
                    var lobbyId = TryExtractLobbyIdFromNetService(netService);
                    if (lobbyId != 0) return lobbyId;
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[PauseMenuInvite] NetService 路径异常：{ex.GetType().Name}: {ex.Message}");
        }

        // 3) RunManager.RunLobby 反射兜底（防御性遍历常见命名）
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return 0;

            var lobby = rm.GetType().GetProperty("RunLobby",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(rm);
            if (lobby == null) return 0;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var name in new[]
                     {
                         "LobbyId", "LobbyID", "SteamLobbyId", "SteamLobbyID",
                         "LobbySteamId", "LobbySteamID", "SteamId", "ID",
                     })
            {
                try
                {
                    var prop = lobby.GetType().GetProperty(name, flags);
                    if (TryParseUlong(prop?.GetValue(lobby), out var id))
                    {
                        Diag.Log($"[PauseMenuInvite] RunLobby.{name} 属性命中大厅 ID = {id}");
                        return id;
                    }

                    var field = lobby.GetType().GetField(name, flags);
                    if (TryParseUlong(field?.GetValue(lobby), out id))
                    {
                        Diag.Log($"[PauseMenuInvite] RunLobby.{name} 字段命中大厅 ID = {id}");
                        return id;
                    }
                }
                catch { }
            }

            // 所有已知命名都未命中——dump RunLobby 全部成员供诊断
            Diag.Log("[PauseMenuInvite] RunLobby 反射未找到大厅 ID，dump 全部成员：");
            DumpMembers(lobby, "RunLobby");
        }
        catch (Exception ex)
        {
            Diag.Log($"[PauseMenuInvite] ResolveCurrentLobbyId 反射异常：{ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// 从 NetService（NetHostGameService / NetClientGameService）提取 Steam 大厅 ID。
    /// 主机端 NetHost 是 SteamHost，内部持有 lobby CSteamID。
    /// </summary>
    private static ulong TryExtractLobbyIdFromNetService(object netService)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var svcType = netService.GetType();

        // NetHostGameService → NetHost → SteamHost → lobby ID
        // NetClientGameService → NetClient → SteamClient → 也可能有 lobby 引用
        foreach (var transportPropName in new[] { "NetHost", "NetClient" })
        {
            try
            {
                var transportProp = svcType.GetProperty(transportPropName, flags);
                var transport = transportProp?.GetValue(netService);
                if (transport == null) continue;

                Diag.Log($"[PauseMenuInvite] NetService.{transportPropName} type={transport.GetType().FullName}");

                // 在 transport（SteamHost/SteamClient）上找 lobby ID
                foreach (var name in new[]
                         {
                             "_lobbyId", "_lobbyID", "_steamLobbyId", "_currentLobby",
                             "LobbyId", "LobbyID", "Lobby", "CurrentLobby",
                             "_lobby", "_currentLobbyId",
                         })
                {
                    try
                    {
                        var field = transport.GetType().GetField(name, flags);
                        if (TryParseUlong(field?.GetValue(transport), out var id))
                        {
                            Diag.Log($"[PauseMenuInvite] {transportPropName}.{name} 字段命中大厅 ID = {id}");
                            return id;
                        }

                        var prop = transport.GetType().GetProperty(name, flags);
                        if (TryParseUlong(prop?.GetValue(transport), out id))
                        {
                            Diag.Log($"[PauseMenuInvite] {transportPropName}.{name} 属性命中大厅 ID = {id}");
                            return id;
                        }
                    }
                    catch { }
                }

                // 未命中——dump transport 全部成员供诊断
                Diag.Log($"[PauseMenuInvite] {transportPropName} 已知命名未命中，dump 全部成员：");
                DumpMembers(transport, transportPropName);
            }
            catch (Exception ex)
            {
                Diag.Log($"[PauseMenuInvite] {transportPropName} 提取异常：{ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>Dump 对象全部属性和字段（诊断用）。</summary>
    private static void DumpMembers(object obj, string label)
    {
        if (obj == null) return;
        var type = obj.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Diag.Log($"--- DUMP {label} ({type.FullName}) ---");
        foreach (var prop in type.GetProperties(flags))
        {
            try
            {
                var value = prop.GetValue(obj);
                var valStr = value == null ? "null" : $"{value.GetType().Name}: {value}";
                Diag.Log($"  PROP {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
            }
            catch (Exception ex) { Diag.Log($"  PROP {prop.Name} = ERROR: {ex.Message}"); }
        }
        foreach (var field in type.GetFields(flags))
        {
            try
            {
                var value = field.GetValue(obj);
                var valStr = value == null ? "null" : $"{value.GetType().Name}: {value}";
                Diag.Log($"  FIELD {field.Name} ({field.FieldType.Name}) = {valStr}");
            }
            catch (Exception ex) { Diag.Log($"  FIELD {field.Name} = ERROR: {ex.Message}"); }
        }
        Diag.Log($"--- END DUMP {label} ---");
    }

    private static bool TryParseUlong(object? value, out ulong result)
    {
        result = 0;
        if (value == null) return false;
        if (value is ulong ul) { result = ul; return ul != 0; }
        if (value is CSteamID sid && sid.IsValid()) { result = sid.m_SteamID; return true; }
        return ulong.TryParse(value.ToString(), out result) && result != 0;
    }
}
