using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.9.8 —— 防护基游戏宝藏房 relic 界面的可空 NetId 异常。
///
/// 基游戏 <c>NHandImageCollection</c> 在 <c>_Input</c> / <c>UpdateHandVisibility</c> /
/// <c>OnInputStateChanged</c> / <c>ProcessGuiFocus</c> 中直接对
/// <c>LocalContext.NetId</c>（<c>ulong?</c>）取 <c>.Value</c>。
///
/// 当本地玩家 NetId 尚未赋值时（本模组做检查点回退、或客机整局重建的瞬时窗口，
/// 玩家正处于宝藏房 relic 界面，且远端/本地鼠标移动触发输入回调），<c>.Value</c> 抛
/// <c>InvalidOperationException: Nullable object must have a value</c>——
/// 这是本次多人回退测试中「玩家在宝藏房时回退/重建」必然触发的那条异常。
/// 原版单人 / 正常联机不会进入该窗口（NetId 在对局内始终有效），故基游戏从未暴露。
///
/// 修复：上述回调开头若 <c>LocalContext.NetId</c> 无值则直接跳过该帧
/// （此帧没有本地手牌可更新，NetId 就绪后下一帧自然恢复正常），
/// 既消除异常又完全不影响正常显示与手柄/鼠标手型光标。
/// </summary>
internal static class TreasureRoomHandNullGuard
{
    [HarmonyPatch(typeof(NHandImageCollection), "UpdateHandVisibility")]
    public static class GuardUpdateHandVisibility
    {
        public static bool Prefix() => LocalContext.NetId.HasValue;
    }

    [HarmonyPatch(typeof(NHandImageCollection), "OnInputStateChanged")]
    public static class GuardOnInputStateChanged
    {
        public static bool Prefix() => LocalContext.NetId.HasValue;
    }

    [HarmonyPatch(typeof(NHandImageCollection), "ProcessGuiFocus")]
    public static class GuardProcessGuiFocus
    {
        public static bool Prefix() => LocalContext.NetId.HasValue;
    }

    [HarmonyPatch(typeof(NHandImageCollection), "_Input")]
    public static class GuardInput
    {
        public static bool Prefix() => LocalContext.NetId.HasValue;
    }
}
