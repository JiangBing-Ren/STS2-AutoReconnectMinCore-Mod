using System;
using AutoReconnect.Scripts.Net;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Combat;

/// <summary>
/// 「战斗中回合回溯」协议的网络模块：把 <see cref="CombatRewindResultMessage"/> 接到当前 NetService 上。
///
/// ## 本版本只接 Result，不接 Request / Vote
///
/// 协议定义了三条消息（见 <c>CombatRewindMessages.cs</c>），但当前只有 Result 有真实用途：
///
///   - <b>Result（已接）</b>：主机在战斗中回滚成功后，把「我退回到了哪个回合、校验和是多少」
///     广播给所有还在线的端。收到的一端拿自己的回合快照核对，
///     对齐就安心继续，对不上就留下一条明确的分歧诊断——这正是排查 desync 最缺的证据。
///   - <b>Request / Vote（未接）</b>：用于「主机想回退、先征求各端同意」的场景。
///     当前主机是**先回滚再广播结果**（<see cref="CombatTurnRewind"/> 的既有行为），
///     没有征求环节，所以这两条现在没有人发。注册 handler 也只是永不触发的死路径，
///     等协议走到「表决后再回退」那一步时再一起接。
///
/// 未注册的后果仅限于「没人处理」：<c>NetMessageBus</c> 会 Log.Error 后丢弃，不会报错崩溃。
///
/// ## 为什么客机收到后不直接应用 FullState
///
/// Result 里带的 <see cref="NetFullCombatState"/> 是权威全量状态，理论上客机能直接套用。
/// 但「在客机端应用全量状态」与主机端 <see cref="CombatTurnRewind"/> 的还原逻辑是同一套代码，
/// 需要先把那部分抽成可跨端调用的公共还原器并单独验证。
/// 本版本刻意只做**只读校验**：零风险，且已经能产出最有价值的诊断信息。
/// </summary>
internal sealed class CombatRewindNetModule : INetMessageModule
{
    private const string Tag = CombatRewindProtocol.Tag;

    private static int _resultsReceived;
    private static int _resultsAligned;
    private static int _resultsDiverged;
    private static uint _tokenCounter;

    public void RegisterHandlers(INetGameService netService)
    {
        // 先自检：mod 自定义消息必须被游戏扫描进 MessageTypes 才能拿到网络 ID，
        // 拿不到号的消息根本发不出去（序列化时取不到 ID），且不会报错——最易静默失败的一环。
        VerifyMessageIds();

        netService.RegisterMessageHandler<CombatRewindResultMessage>(OnResult);
    }

    /// <summary>
    /// 确认三条消息都拿到了网络 ID。
    ///
    /// 正常路径下不需要我们做任何事：<c>MessageTypes.Initialize()</c> 在 mod 全部加载完之后运行，
    /// 会把「内置消息 + <c>GetSubtypesInMods</c> 扫到的全部 mod 消息」合在一起排序发号。
    /// 但如果本 mod 的程序集没能关联上 ModManager（比如被别的加载器以非常规方式载入），
    /// 扫描就会漏掉我们 —— 表现是消息永远发不出去且不报错，极难排查。
    /// 所以这里主动问一次 ID，问不到就把原因直接写进日志。
    /// </summary>
    private static void VerifyMessageIds()
    {
        Verify(default(CombatRewindRequestMessage), nameof(CombatRewindRequestMessage));
        Verify(default(CombatRewindVoteMessage), nameof(CombatRewindVoteMessage));
        Verify(default(CombatRewindResultMessage), nameof(CombatRewindResultMessage));
    }

    private static void Verify(INetMessage sample, string name)
    {
        try
        {
            int id = MessageTypes.ToId(sample);
            Diag.Log($"{Tag} 消息 ID 自检：{name} = {id}（当前共 {MessageTypes.Count} 种消息类型）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} ⚠ 消息 {name} 未获得网络 ID（{ex.GetType().Name}）：该消息无法跨端传输。" +
                     "请检查本 mod 的程序集是否被正确关联到 ModManager。");
        }
    }

    /// <summary>
    /// 主机端：回滚完成后广播结果，让所有还在线的端核对自己是否仍与本局对齐。
    /// </summary>
    /// <param name="roundNumber">回退到的回合号。</param>
    /// <param name="checksum">主机在该回合开头的校验和。</param>
    /// <param name="approved">回滚是否真的成功（失败时各端保持现状）。</param>
    /// <param name="fullState">主机在该回合开头抓的全量战斗状态；approved 为 false 时可为 null。</param>
    /// <returns>是否已发出。</returns>
    public static bool AnnounceRewind(int roundNumber, uint checksum, bool approved,
        NetFullCombatState? fullState)
    {
        try
        {
            var svc = RunManager.Instance?.NetService;
            if (svc == null)
            {
                Diag.Log($"{Tag} 广播回滚结果失败：RunManager.NetService 为空。");
                return false;
            }

            // 无参 SendMessage 的语义按端而异：主机 = 广播全体，客机 = 发给主机。
            // 这里只允许主机调用，免得客机误把「我是权威」的消息发给主机。
            if (svc.Type != NetGameType.Host)
            {
                Diag.Log($"{Tag} 本端不是主机（{svc.Type}），不广播回滚结果。");
                return false;
            }

            if (!svc.IsConnected)
            {
                Diag.Log($"{Tag} 广播回滚结果失败：连接已断开，无人可通知。");
                return false;
            }

            var msg = new CombatRewindResultMessage
            {
                Token = ++_tokenCounter,
                RoundNumber = roundNumber,
                Checksum = checksum,
                Approved = approved,
                FullState = approved ? fullState : null
            };

            svc.SendMessage(msg);   // 主机 → 全体
            Diag.Log($"{Tag} 已广播回滚结果：round={roundNumber}, checksum={checksum:x8}, " +
                     $"approved={approved}, token={msg.Token}。");
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 广播回滚结果异常（已忽略）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 客机端：收到主机的回滚结果，与本端快照做只读比对。
    /// 只记录不修状态——本端若已分歧，擅自改反而可能制造第二个分歧点。
    /// </summary>
    private static void OnResult(CombatRewindResultMessage msg, ulong senderId)
    {
        try
        {
            _resultsReceived++;

            if (!msg.Approved)
            {
                Diag.Log($"{Tag} 主机广播：round={msg.RoundNumber} 回滚未成功，各端保持现状。");
                return;
            }

            var mgr = CombatManager.Instance;
            if (mgr == null || !mgr.IsInProgress)
            {
                Diag.Log($"{Tag} 收到 round={msg.RoundNumber} 的回滚结果，但本端不在战斗中，忽略。");
                return;
            }

            var snap = CombatTurnSnapshots.Find(msg.RoundNumber);
            if (snap == null)
            {
                Diag.Log($"{Tag} 收到 round={msg.RoundNumber} 的回滚结果，但本端没有该回合快照" +
                         $"（现有 {CombatTurnSnapshots.Describe()}），无法核对。");
                return;
            }

            if (snap.Checksum == msg.Checksum)
            {
                _resultsAligned++;
                Diag.Log($"{Tag} 核对一致：round={msg.RoundNumber}, checksum={msg.Checksum:x8}，本端与主机对齐。");
            }
            else
            {
                _resultsDiverged++;
                Diag.Log($"{Tag} ⚠ 核对不一致：round={msg.RoundNumber}，" +
                         $"本端={snap.Checksum:x8} 主机={msg.Checksum:x8}。" +
                         "本端未与主机对齐，后续可能 desync（本端不做自动修正）。");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 处理回滚结果异常（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>诊断用：收发统计。</summary>
    public static string Describe()
        => $"收到 {_resultsReceived} 条（对齐 {_resultsAligned} / 分歧 {_resultsDiverged}）";
}
