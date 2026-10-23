using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Combat;

/// <summary>
/// 一个「回合开头」的战斗快照。
///
/// 只服务于「战斗中掉线 → 重连回来 → 回到本回合开头重打」这一件事，
/// 因此不做历史列表、不做任意回合寻址（那是 TurnRewindMp 的场景，代价是 184KB 的
/// SnapshotManager）。这里只留回合号 + 全量状态 + 校验和 + 怪物意图。
/// </summary>
public sealed class CombatTurnSnapshot
{
    /// <summary>回合号（<see cref="CombatState.RoundNumber"/>）。跨端对齐的唯一坐标。</summary>
    public int RoundNumber { get; init; }

    /// <summary>
    /// 全量战斗状态。<see cref="NetFullCombatState.FromRun"/> 抓的就是游戏自己的
    /// 校验快照（HP/block/powers/能量/phase/turn/各卡堆/orbs/potions/relics/RNG）。
    /// </summary>
    public NetFullCombatState? FullState { get; init; }

    /// <summary>
    /// 抓取瞬间的校验和，等于 <c>ChecksumTracker.GenerateChecksum(FullState)</c>。
    /// 与主机回传的 combatState 校验和比对，即可判定两端是否在同一锚点上。
    /// </summary>
    public uint Checksum { get; init; }

    /// <summary>
    /// 怪物意图与状态机快照。
    ///
    /// ## 为什么必须单独抓（TurnRewindMp 的教训）
    ///
    /// <see cref="NetFullCombatState"/> 只覆盖**参与校验和**的字段。怪物下一步要出什么招
    /// （<c>NextMove</c>）、状态机停在哪（<c>CurrentState</c>）、是否已出过首招
    /// （<c>_performedFirstMove</c>）**都不在校验和里**。
    /// 只还原 <see cref="FullState"/> 会出现「两端校验和一致、但怪物下一步行动不同」——
    /// 当下看不出问题，过一两个回合必然真分歧。
    /// TurnRewindMp 为此抓了 184KB 的 extras，我们只取影响玩法最直接的一小撮。
    /// </summary>
    public List<CreatureIntentSnapshot> Intents { get; init; } = new();

    /// <summary><c>CombatState._nextCreatureId</c>：召唤物 id 分配游标。</summary>
    public uint NextCreatureId { get; init; }
}

/// <summary>
/// 单个生物的「校验和之外」的运行时状态。
/// </summary>
public sealed class CreatureIntentSnapshot
{
    /// <summary>在 <c>CombatState.Creatures</c> 中的下标，用作主匹配键。</summary>
    public int Index { get; init; }

    /// <summary><see cref="Creature.CombatId"/>，辅助匹配。</summary>
    public uint? CombatId { get; init; }

    /// <summary>怪物 ModelId（玩家生物为 null）。</summary>
    public string? MonsterId { get; init; }

    /// <summary>下一步意图的 state id（<c>MonsterState.Id</c>）。</summary>
    public string? NextMoveId { get; init; }

    /// <summary>状态机当前状态的 id。</summary>
    public string? CurrentStateId { get; init; }

    public bool PerformedFirstMove { get; init; }
    public bool SpawnedThisTurn { get; init; }
    public bool IsPerformingMove { get; init; }
    public bool IsStunned { get; init; }
}

/// <summary>
/// 战斗回合快照仓库：在每个回合开头抓一份，只保留最近若干个。
///
/// 订阅方式：<c>CombatManager</c> 的 CombatSetUp / TurnStarted / CombatEnded / CombatWon
/// 都是**公开 event**（<c>[E add/remove]</c>），直接 <c>GetEvent</c> 即可；
/// 保留反射私有委托字段的兜底，防止版本变更改成纯字段。
/// </summary>
public static class CombatTurnSnapshots
{
    /// <summary>保留的快照数量上限。只需覆盖「当前回合 + 掉线/切换瞬间的前一回合」。</summary>
    private const int MaxSnapshots = 3;

    private static readonly List<CombatTurnSnapshot> _snapshots = new();
    private static bool _subscribed;
    private static bool _capturing;

    /// <summary>订阅 CombatManager 的回合事件。幂等，可重复调用。</summary>
    public static void Install()
    {
        if (_subscribed) return;
        _subscribed = true;

        Subscribe("CombatSetUp", new Action<CombatState>(OnCombatSetUp));
        Subscribe("TurnStarted", new Action<CombatState>(OnTurnStarted));
        Subscribe("CombatEnded", new Action<CombatRoom>(OnCombatEnded));
        Subscribe("CombatWon", new Action<CombatRoom>(OnCombatEnded));
        Diag.Log("[TurnSnapshot] 已订阅 CombatManager 回合事件。");
    }

    /// <summary>按回合号取快照；没找到返回 null。</summary>
    public static CombatTurnSnapshot? Find(int roundNumber)
        => _snapshots.FirstOrDefault(s => s.RoundNumber == roundNumber);

    /// <summary>当前最新的一份快照。</summary>
    public static CombatTurnSnapshot? Latest
        => _snapshots.Count > 0 ? _snapshots[_snapshots.Count - 1] : null;

    /// <summary>清空（战斗开始/结束时调用）。</summary>
    public static void Clear()
    {
        _snapshots.Clear();
        Diag.Log("[TurnSnapshot] 快照已清空。");
    }

    /// <summary>供日志展示：当前留了哪几个回合的快照。</summary>
    public static string Describe()
        => _snapshots.Count == 0
            ? "(空)"
            : string.Join(", ", _snapshots.Select(s => $"r{s.RoundNumber}/cs={s.Checksum:x8}"));

    private static void OnCombatSetUp(CombatState _) => Clear();
    private static void OnCombatEnded(CombatRoom _) => Clear();

    private static void OnTurnStarted(CombatState state)
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            if (CombatManager.Instance == null || !CombatManager.Instance.IsInProgress) return;
            // 只抓玩家侧回合：撤回的目标是「本回合开头」，锚点必须在玩家回合。
            if (state.CurrentSide != CombatSide.Player) return;

            var runState = state.RunState;
            if (runState == null) return;

            var full = NetFullCombatState.FromRun(runState, null);
            uint checksum = 0;
            try
            {
                checksum = RunManager.Instance?.ChecksumTracker?.GenerateChecksum(full) ?? 0;
            }
            catch (Exception ex)
            {
                Diag.Log($"[TurnSnapshot] 校验和生成失败（记为 0）：{ex.Message}");
            }

            var snap = new CombatTurnSnapshot
            {
                RoundNumber = state.RoundNumber,
                FullState = full,
                Checksum = checksum,
                Intents = CaptureIntents(state),
                NextCreatureId = ReadNextCreatureId(state)
            };

            _snapshots.Add(snap);
            while (_snapshots.Count > MaxSnapshots) _snapshots.RemoveAt(0);

            Diag.Log($"[TurnSnapshot] 捕获回合开头快照：round={snap.RoundNumber}, checksum={checksum:x8}, " +
                     $"生物 {snap.Intents.Count}；现有 {Describe()}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[TurnSnapshot] 捕获回合快照失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>
    /// 抓取每个生物的意图与状态机位置。全部走 get-only 属性 + 反射读私有字段，
    /// 抓不到就记 null/默认，回滚时跳过——宁可少还原一项，也不能让抓快照本身抛异常。
    /// </summary>
    private static List<CreatureIntentSnapshot> CaptureIntents(CombatState state)
    {
        var list = new List<CreatureIntentSnapshot>();
        var creatures = state.Creatures.ToList();
        for (int i = 0; i < creatures.Count; i++)
        {
            var creature = creatures[i];
            var monster = creature.Monster;
            var sm = monster?.MoveStateMachine;

            string? nextMoveId = null;
            try { nextMoveId = monster?.NextMove?.Id; } catch { }

            string? currentStateId = null;
            bool performedFirstMove = false;
            if (sm != null)
            {
                try
                {
                    var cur = ReflectionHelper.GetFieldValue(sm, new[] { "_currentState" });
                    currentStateId = (cur as MonsterState)?.Id;
                }
                catch { }
                try
                {
                    performedFirstMove =
                        ReflectionHelper.GetFieldValue(sm, new[] { "_performedFirstMove" }) is bool b && b;
                }
                catch { }
            }

            list.Add(new CreatureIntentSnapshot
            {
                Index = i,
                CombatId = creature.CombatId,
                MonsterId = monster?.Id?.Entry,
                NextMoveId = nextMoveId,
                CurrentStateId = currentStateId,
                PerformedFirstMove = performedFirstMove,
                SpawnedThisTurn = monster?.SpawnedThisTurn ?? false,
                IsPerformingMove = monster?.IsPerformingMove ?? false,
                IsStunned = creature.IsStunned
            });
        }
        return list;
    }

    private static uint ReadNextCreatureId(CombatState state)
    {
        try
        {
            return ReflectionHelper.GetFieldValue(state, new[] { "_nextCreatureId" }) is uint v ? v : 0u;
        }
        catch
        {
            return 0u;
        }
    }

    /// <summary>
    /// 挂一个处理器到 CombatManager 的同名事件/委托字段上。
    /// 先按 public event 找，找不到再按私有委托字段 Delegate.Combine。
    /// </summary>
    private static void Subscribe(string name, Delegate handler)
    {
        try
        {
            var instance = CombatManager.Instance;
            if (instance == null) return;

            var evt = typeof(CombatManager).GetEvent(name);
            if (evt != null)
            {
                evt.AddEventHandler(instance, handler);
                return;
            }

            var field = AccessTools.Field(typeof(CombatManager), name);
            if (field == null)
            {
                Diag.Log($"[TurnSnapshot] 未找到 CombatManager.{name}（事件与字段都没有）。");
                return;
            }
            field.SetValue(instance, Delegate.Combine(field.GetValue(instance) as Delegate, handler));
        }
        catch (Exception ex)
        {
            Diag.Log($"[TurnSnapshot] 订阅 CombatManager.{name} 失败：{ex.Message}");
        }
    }
}
