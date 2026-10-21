using System.Reflection;
using System.Threading.Tasks;
using AutoReconnect.Scripts.MenuRejoin;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace AutoReconnect.Scripts.Net;

/// <summary>
/// v0.9.37 —— 重连流程进行时，吞掉原版 <see cref="NErrorPopup"/> 错误弹窗。
///
/// 借鉴 QuickLink 的 <c>NErrorPopup.Create(NetErrorInfo)</c> Prefix 拦截：
/// 我们的重连流程（对局内 <see cref="ReconnectRunner"/> / 菜单重连
/// <see cref="MenuRejoinFlow"/>）自带浮层与诊断回执，此时原版弹窗只会：
///   · 盖在我们的浮层上面，玩家看到的是「vanilla 错误文案」而非重连进度；
///   · 打断 <c>JoinFlow</c> 收尾路径（vanilla 的 RunInProgress 分支会自己
///     <c>Disconnect</c>，把我们刚握好的连接拆掉）。
/// 因此：仅当「我们的流程正在进行中」才抑制；空闲时原版行为一律放行，
/// 绝不改变未装/关闭本 mod 时的游戏表现。
/// </summary>
[HarmonyPatch]
internal static class ErrorPopupGuardPatch
{
    [HarmonyTargetMethod]
    private static MethodInfo? TargetMethod()
    {
        // 精确锁定 Create(NetErrorInfo) 重载（Create 共有 3 个重载）。
        return AccessTools.Method(typeof(NErrorPopup), "Create", new[] { typeof(NetErrorInfo) });
    }

    [HarmonyPrefix]
    private static bool Prefix(NetErrorInfo info, ref NErrorPopup? __result)
    {
        try
        {
            bool inOurFlow = HostInfoTracker.IsReconnecting || MenuRejoinFlow.IsRejoining;
            if (!inOurFlow)
                return true; // 不在我们的流程里——原版弹窗照常

            var reason = ReconnectDiagnostics.TryGetReason(info);
            Diag.Log($"[v0.9.37] 重连流程进行中，抑制原版错误弹窗（reason={reason?.ToString() ?? "读不出"}）。" +
                     "错误详情已由重连浮层/诊断承接。");
            __result = null;
            return false;
        }
        catch
        {
            // 守卫本身绝不能成为崩溃源——任何异常都放行原版行为。
            return true;
        }
    }
}
