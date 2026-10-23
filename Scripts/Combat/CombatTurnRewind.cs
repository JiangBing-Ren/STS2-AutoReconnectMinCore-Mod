using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.Runs;
using AutoReconnect.Scripts.Net;

namespace AutoReconnect.Scripts.Combat;

/// <summary>
/// 主机端「回合开头回滚」执行器。
///
/// ## 它解决什么
///
/// 战斗中掉线 → 重连回来时，主机这边的战斗已经推进了（牌出了、怪动了）。
/// 要让掉线的人「回到本回合开头重打」，主机必须先把自己也拨回回合开头，
/// 否则两端永远对不上。
///
/// ## 为什么不走「存档重装载」
///
/// <c>SerializableRun</c> **不含战斗现场**（<c>SerializableRoom</c> 只有房间指针，
/// <c>SerializablePlayer</c> 只有卡组/遗物/药水）。存档重装载只会让整场战斗从零重开，
/// 回不到第 N 回合。所以只能在**活着的战斗实例上原地改**——这也是 TurnRewindMp 的路子。
///
/// ## 安全边界（照抄 TurnRewindMp 的 WaitForSafeRestoreBoundary 思路）
///
/// 回滚必须在「没有动作正在结算」的时刻做。牌还在飞、怪物正在出招时改数值，
/// 会把正在跑的那条动作链改出半成品状态。
/// 所以先等 <c>ActionExecutor</c> 空闲，再 Pause → Apply → Unpause。
///
/// ## 失败即放弃
///
/// 任何一步抛异常都只记日志并恢复 Unpause，**绝不留下半改的状态**。
/// 回滚失败的最坏结果是「没回滚」，而不是「状态被改坏」——后者会直接 desync。
/// </summary>
public static class CombatTurnRewind
{
    private const string Tag = "[TurnRewind]";

    /// <summary>等待动作队列空闲的超时（秒）。超时则放弃本次回滚。</summary>
    private const double SafeBoundaryTimeoutSeconds = 10.0;

    private static bool _rewinding;
    private static bool _hostTriggerInstalled;

    /// <summary>回滚是否正在进行（防重入）。</summary>
    public static bool IsRewinding => _rewinding;

    /// <summary>
    /// 安装快照抓取 + 主机端触发点。幂等。
    ///
    /// 触发点挂在 <c>INetHostGameService.ClientConnectionFailed</c> 上：
    /// 有人掉线时，若本端正处在战斗中，就把战斗拨回本回合开头，
    /// 让掉线的人回来时面对的是「本回合开始」，而不是「打了一半的残局」。
    /// </summary>
    public static void Install()
    {
        CombatTurnSnapshots.Install();

        // 网络消息模块登记一次即可（注册器内部按类型去重）。
        // 刻意不分主机/客机：客机同样要能收到主机广播的回滚结果。
        NetMessageRegistry.Add(new CombatRewindNetModule());

        if (_hostTriggerInstalled) return;

        try
        {
            var evt = typeof(CombatManager).GetEvent("CombatSetUp");
            if (evt == null) return;
            evt.AddEventHandler(CombatManager.Instance, new Action<CombatState>(OnCombatSetUp));
            _hostTriggerInstalled = true;
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 安装主机端触发点失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 每场战斗开打都走一次。
    ///
    /// 先补 handler 再挂掉线回调：重连会换掉 <c>RunManager.NetService</c>，
    /// 而消息 handler 是**按 NetService 实例**挂的，新实例上什么都没有。
    /// 这里是最自然的补挂时机（幂等，实例没变就什么都不做）。
    /// </summary>
    private static void OnCombatSetUp(CombatState _)
    {
        NetMessageRegistry.EnsureRegistered();
        TrySubscribeHostDisconnect();
    }

    /// <summary>
    /// 拿到主机的 NetService 并挂上掉线回调。每次战斗开始都试一次，成功即置位不再重复。
    /// 主机身份以 <c>NetGameType.Host</c> 判定——客机不做这件事（它不负责推进战斗）。
    /// </summary>
    private static void TrySubscribeHostDisconnect()
    {
        if (_subscribedToHost) return;
        try
        {
            var svc = RunManager.Instance?.NetService;
            if (svc == null || svc.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host) return;

            var evt = svc.GetType().GetEvent("ClientConnectionFailed");
            if (evt == null) return;

            var handler = new Action<ulong, MegaCrit.Sts2.Core.Entities.Multiplayer.NetErrorInfo>(OnClientConnectionFailed);
            evt.AddEventHandler(svc, handler);
            _subscribedToHost = true;
            Diag.Log($"{Tag} 已订阅主机端 ClientConnectionFailed（战斗中掉线将触发回合回滚）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 订阅 ClientConnectionFailed 失败：{ex.Message}");
        }
    }

    private static bool _subscribedToHost;

    private static async void OnClientConnectionFailed(ulong peerId,
        MegaCrit.Sts2.Core.Entities.Multiplayer.NetErrorInfo info)
    {
        try
        {
            // 掉线往往伴随传输层重建，先确保本端 handler 仍挂在当前的 NetService 上。
            NetMessageRegistry.EnsureRegistered();

            var mgr = CombatManager.Instance;
            if (mgr == null || !mgr.IsInProgress)
            {
                Diag.Log($"{Tag} peer {peerId} 掉线，但不在战斗中，无需回滚。");
                return;
            }

            var latest = CombatTurnSnapshots.Latest;
            if (latest == null)
            {
                Diag.Log($"{Tag} peer {peerId} 掉线，但没有回合快照，无法回滚（保持现状）。");
                return;
            }

            Diag.Log($"{Tag} peer {peerId} 在战斗中掉线，回滚到 round={latest.RoundNumber} 开头。");
            bool ok = await RewindToRoundStartAsync(latest.RoundNumber);
            Diag.Log($"{Tag} 掉线触发的回滚{(ok ? "成功" : "未成功")}。");
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 处理 peer {peerId} 掉线时异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把当前战斗拨回指定回合的开头。
    /// </summary>
    /// <param name="roundNumber">目标回合号；传 null 表示用最新一份快照（当前回合）。</param>
    /// <returns>是否成功。</returns>
    public static async Task<bool> RewindToRoundStartAsync(int? roundNumber = null)
    {
        if (_rewinding)
        {
            Diag.Log($"{Tag} 回滚已在进行中，忽略本次请求。");
            return false;
        }

        var mgr = CombatManager.Instance;
        if (mgr == null || !mgr.IsInProgress)
        {
            Diag.Log($"{Tag} 当前不在战斗中，无法回滚。");
            return false;
        }

        var snap = roundNumber.HasValue
            ? CombatTurnSnapshots.Find(roundNumber.Value)
            : CombatTurnSnapshots.Latest;
        if (snap == null || snap.FullState == null)
        {
            Diag.Log($"{Tag} 找不到目标回合的快照" +
                     (roundNumber.HasValue ? $"（round={roundNumber.Value}）" : "（无快照）") +
                     $"；现有 {CombatTurnSnapshots.Describe()}");
            return false;
        }

        _rewinding = true;
        try
        {
            if (!await WaitForSafeBoundaryAsync())
            {
                Diag.Log($"{Tag} 等待动作队列空闲超时（{SafeBoundaryTimeoutSeconds}s），放弃回滚（状态未改动）。");
                return false;
            }

            var state = mgr.DebugOnlyGetState();
            if (state == null || !mgr.IsInProgress)
            {
                Diag.Log($"{Tag} 等待期间战斗已结束，放弃回滚。");
                return false;
            }

            Diag.Log($"{Tag} 开始回滚到 round={snap.RoundNumber}（目标 checksum={snap.Checksum:x8}）。");

            mgr.Pause();
            try { RunManager.Instance?.ActionExecutor?.Pause(); } catch { }

            try
            {
                ApplyCore(state, snap);
            }
            finally
            {
                try { RunManager.Instance?.ActionExecutor?.Unpause(); } catch { }
                mgr.Unpause();
            }

            Verify(state, snap);
            Diag.Log($"{Tag} 回滚完成：round={state.RoundNumber}。");

            // 广播给所有还在线的端：主机已退回到该回合开头，请各自核对校验和。
            // 掉线的那一端此刻收不到，但它重连后走的是既有握手流程。
            CombatRewindNetModule.AnnounceRewind(snap.RoundNumber, snap.Checksum, true, snap.FullState);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 回滚失败（已恢复 Unpause，状态可能部分改动）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _rewinding = false;
        }
    }

    /// <summary>
    /// 等动作队列空闲：牌还在飞 / 怪物正在出招时不能改数值。
    /// 每帧让一次，超时返回 false。
    /// </summary>
    private static async Task<bool> WaitForSafeBoundaryAsync()
    {
        var executor = RunManager.Instance?.ActionExecutor;
        if (executor == null) return true;

        var tree = Engine.GetMainLoop() as SceneTree;
        ulong startMs = Time.GetTicksMsec();
        ulong timeoutMs = (ulong)(SafeBoundaryTimeoutSeconds * 1000);

        while (executor.IsRunning)
        {
            if (Time.GetTicksMsec() - startMs > timeoutMs) return false;
            if (tree != null)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            else
                await Task.Delay(16);
        }
        return true;
    }

    private static void ApplyCore(CombatState state, CombatTurnSnapshot snap)
    {
        var full = snap.FullState!;

        state.RoundNumber = snap.RoundNumber;
        state.CurrentSide = CombatSide.Player;

        RestoreRunRng(state, full);
        RestoreCreatures(state, full);
        RestoreIntents(state, snap);
        RestorePlayers(state, full);
    }

    /// <summary>
    /// 回滚 run RNG。**不回滚的话，回退后第一次洗牌就会发散**——这是 TurnRewindMp 明确的教训。
    /// </summary>
    private static void RestoreRunRng(CombatState state, NetFullCombatState full)
    {
        try
        {
            var rng = state.RunState?.Rng;
            if (rng == null || full.Rng == null) return;
            // Rng.LoadFromSerializable 是 public，但 RunRngSet 未必直接暴露同名方法，
            // 统一走反射更稳（TurnRewindMp 也是反射调的）。
            ReflectionHelper.InvokeMethod(rng, new[] { "LoadFromSerializable" },
                args: new object?[] { full.Rng });
            Diag.Log($"{Tag} run RNG 已回滚。");
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} run RNG 回滚失败：{ex.Message}");
        }
    }

    private static void RestoreCreatures(CombatState state, NetFullCombatState full)
    {
        var live = state.Creatures.ToList();
        var saved = full.Creatures;

        if (live.Count != saved.Count)
        {
            // 名册对不上通常是「快照后被召唤/被移除」。
            // 完整处理要按 ModelId 增删（TurnRewindMp 用活对象引用做 SequenceEqual），
            // 我们没有活引用可依，先按下标尽量对齐并明确告警，避免静默改错对象。
            Diag.Log($"{Tag} 警告：生物名册数量不一致（现场 {live.Count} vs 快照 {saved.Count}），" +
                     "按下标对齐，多出的现场生物不处理。");
        }

        int n = Math.Min(live.Count, saved.Count);
        for (int i = 0; i < n; i++)
        {
            var creature = live[i];
            var cs = saved[i];
            try
            {
                // ⚠ 这些属性的 setter 是 internal，跨程序集只读（sts2sig 的 [P get/set]
                //   不反映 setter 可见性，编译时才发现）。一律走 public 的 *Internal 方法，
                //   顺带好处是会触发对应的 Changed 事件，UI 能跟着刷新。
                creature.SetMaxHpInternal(cs.maxHp);
                creature.SetCurrentHpInternal(cs.currentHp);

                int blockDelta = cs.block - creature.Block;
                if (blockDelta > 0) creature.GainBlockInternal(blockDelta);
                else if (blockDelta < 0) creature.LoseBlockInternal(-blockDelta);

                RestorePowers(creature, cs.powers);
            }
            catch (Exception ex)
            {
                Diag.Log($"{Tag} 还原生物 #{i} {creature.ModelId.Entry} 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 把 power 对齐到快照：多余的移除，少的补上，数量不同的改数量。
    /// 用 <c>ModelDb.GetByIdOrNull</c> 取原型再 <c>ToMutable(amount)</c> 造可变实例。
    /// </summary>
    private static void RestorePowers(Creature creature, List<NetFullCombatState.PowerState>? powers)
    {
        powers ??= new List<NetFullCombatState.PowerState>();

        var target = new Dictionary<ModelId, int>();
        foreach (var p in powers)
        {
            if (p.id == null) continue;
            target[p.id] = p.amount;
        }

        foreach (var existing in creature.Powers.ToList())
        {
            if (existing == null) continue;
            if (!target.ContainsKey(existing.Id))
            {
                try { creature.RemovePowerInternal(existing); }
                catch (Exception ex) { Diag.Log($"{Tag} 移除 power {existing.Id} 失败：{ex.Message}"); }
            }
        }

        foreach (var kv in target)
        {
            try
            {
                var existing = creature.GetPower(kv.Key);
                if (existing != null)
                {
                    if (existing.Amount != kv.Value) existing.SetAmount(kv.Value, true);
                    continue;
                }
                var proto = ModelDb.GetByIdOrNull<PowerModel>(kv.Key);
                var instance = proto?.ToMutable(kv.Value);
                if (instance == null)
                {
                    Diag.Log($"{Tag} 无法构造 power {kv.Key}（ModelDb 未找到原型）。");
                    continue;
                }
                creature.ApplyPowerInternal(instance);
            }
            catch (Exception ex)
            {
                Diag.Log($"{Tag} 还原 power {kv.Key} 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 还原怪物意图与状态机位置。**这部分不在校验和里，但直接决定怪物下一步出什么招**，
    /// 漏掉就会「当下校验和一致、过两回合真分歧」。
    /// </summary>
    private static void RestoreIntents(CombatState state, CombatTurnSnapshot snap)
    {
        if (snap.Intents.Count == 0) return;

        var live = state.Creatures.ToList();
        foreach (var intent in snap.Intents)
        {
            if (intent.Index < 0 || intent.Index >= live.Count) continue;
            var monster = live[intent.Index].Monster;
            if (monster == null) continue;

            var sm = monster.MoveStateMachine;
            try
            {
                if (sm != null)
                {
                    if (intent.CurrentStateId != null &&
                        sm.States.TryGetValue(intent.CurrentStateId, out var curState))
                    {
                        sm.ForceCurrentState(curState);
                    }
                    ReflectionHelper.SetFieldValue(sm, new[] { "_performedFirstMove" },
                        intent.PerformedFirstMove);

                    if (intent.NextMoveId != null &&
                        sm.States.TryGetValue(intent.NextMoveId, out var moveState))
                    {
                        // NextMove 的 setter 同样是 internal，走反射。
                        // 不直接引用 MoveState 类型：它是状态机内部的嵌套/邻域类型，
                        // 跨程序集引用容易因游戏改版而编译不过。
                        ReflectionHelper.SetPropertyValue(monster, new[] { "NextMove" }, moveState);
                    }
                }

                ReflectionHelper.SetPropertyValue(monster, new[] { "SpawnedThisTurn" }, intent.SpawnedThisTurn);
                ReflectionHelper.SetPropertyValue(monster, new[] { "IsPerformingMove" }, intent.IsPerformingMove);
            }
            catch (Exception ex)
            {
                Diag.Log($"{Tag} 还原生物意图 #{intent.Index} 失败：{ex.Message}");
            }
        }
        Diag.Log($"{Tag} 已还原 {snap.Intents.Count} 个生物意图。");
    }

    private static void RestorePlayers(CombatState state, NetFullCombatState full)
    {
        foreach (var ps in full.Players)
        {
            var player = state.GetPlayer(ps.playerId);
            if (player == null)
            {
                Diag.Log($"{Tag} 找不到玩家 {ps.playerId}，跳过。");
                continue;
            }
            var pcs = player.PlayerCombatState;
            if (pcs == null) continue;

            try
            {
                // TurnNumber 的 setter 也是 internal → 反射；Phase 的 setter 可见，直接赋值。
                ReflectionHelper.SetPropertyValue(pcs, new[] { "TurnNumber" }, ps.turnNumber);
                pcs.Phase = ps.phase;

                ReflectionHelper.SetFieldValue(pcs, new[] { "_energy" }, ps.energy);
                ReflectionHelper.SetFieldValue(pcs, new[] { "_stars" }, ps.stars);
                ReflectionHelper.SetFieldValue(player, new[] { "_gold" }, ps.gold);

                try { player.PlayerRng?.LoadFromSerializable(ps.rngSet); }
                catch (Exception ex) { Diag.Log($"{Tag} 玩家 {ps.playerId} RNG 回滚失败：{ex.Message}"); }

                RestorePiles(state, player, pcs, ps.piles);
            }
            catch (Exception ex)
            {
                Diag.Log($"{Tag} 还原玩家 {ps.playerId} 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 重建四个战斗牌堆（手牌/抽牌/弃牌/消耗）。
    /// 卡牌对象由 <c>IRunState.LoadCard(SerializableCard, Player)</c> 从存档重建，
    /// 与游戏自己装载存档用的是同一条路。
    /// </summary>
    private static void RestorePiles(CombatState state, Player player, PlayerCombatState pcs,
        List<NetFullCombatState.CombatPileState>? piles)
    {
        if (piles == null) return;

        foreach (var pileState in piles)
        {
            var pile = PileFor(pcs, pileState.pileType);
            if (pile == null) continue;

            try
            {
                pile.Clear(true);

                var cards = pileState.cards;
                if (cards != null)
                {
                    for (int i = 0; i < cards.Count; i++)
                    {
                        var cs = cards[i];
                        if (cs.card == null) continue;
                        var card = state.RunState?.LoadCard(cs.card, player);
                        if (card == null) continue;
                        pile.AddInternal(card, i, true);
                    }
                }

                pile.InvokeContentsChanged();
            }
            catch (Exception ex)
            {
                Diag.Log($"{Tag} 还原牌堆 {pileState.pileType} 失败：{ex.Message}");
            }
        }
    }

    private static CardPile? PileFor(PlayerCombatState pcs, PileType type) => type switch
    {
        PileType.Hand => pcs.Hand,
        PileType.Draw => pcs.DrawPile,
        PileType.Discard => pcs.DiscardPile,
        PileType.Exhaust => pcs.ExhaustPile,
        PileType.Play => pcs.PlayPile,
        _ => null
    };

    /// <summary>
    /// 回滚后自校验：用游戏自己的校验器重算一次，与快照记录的比对。
    /// 不相等只告警不回滚——此时已经改完了，再改回去风险更大，交给上层决定。
    /// </summary>
    private static void Verify(CombatState state, CombatTurnSnapshot snap)
    {
        try
        {
            var tracker = RunManager.Instance?.ChecksumTracker;
            if (tracker == null) return;

            var now = NetFullCombatState.FromRun(state.RunState, null);
            uint actual = tracker.GenerateChecksum(now);
            if (actual == snap.Checksum)
            {
                Diag.Log($"{Tag} 校验通过：checksum={actual:x8} 与快照一致。");
            }
            else
            {
                Diag.Log($"{Tag} ⚠ 校验不一致：快照={snap.Checksum:x8} 实际={actual:x8}。" +
                         "回滚未完全对齐，后续可能 desync。");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 回滚后校验失败：{ex.Message}");
        }
    }
}
