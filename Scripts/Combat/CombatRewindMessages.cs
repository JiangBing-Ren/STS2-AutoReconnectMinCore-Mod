using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace AutoReconnect.Scripts.Combat;

/// <summary>
/// 「战斗中回合回溯」的自定义网络消息（三条，走游戏自带的消息总线）。
///
/// ## 为什么需要自定义消息
///
/// 掉线客机从主菜单重连时，主机回传的 <c>SerializableRun</c> **不含战斗现场**
/// （<c>SerializableRoom</c> 只有房间指针，<c>SerializablePlayer</c> 只有卡组/遗物/药水等局外数据）。
/// 战斗现场只存在于 <c>NetFullCombatState</c>（<c>ClientRejoinResponseMessage.combatState</c>）里，
/// 而基游戏对它**只有 FromRun、没有任何 Apply** —— 它是校验和比对用的只读快照。
/// 所以「让所有人回到同一个回合开头」这件事，必须自己带状态过去。
///
/// ## 为什么 Result 里带全量状态，而不是让各端应用自己的快照
///
/// 双端各自在回合开头抓快照看似更省流量，但有两个致命问题：
///   1. 掉线客机**没有**自己那份（进程重启，内存全丢）；
///   2. 各端应用"自己的"快照后无法交叉验证，一旦某端抓歪了就会静默分歧。
/// 因此改成：**主机是唯一权威**，Result 携带主机那份全量状态，所有端都应用同一个来源。
/// 各端自己抓的快照只用于 Vote —— 比对校验和，作为「我确实也在这个回合开头」的证据。
///
/// ## 握手
///
/// <code>
///   主机 ──Request(round, checksum)──▶ 全体
///   客机 ──Vote(round, checksum, agree)──▶ 主机
///   主机 ──Result(round, approved, fullState)──▶ 全体
/// </code>
///
/// Token 是一次共识轮次的标识：重连可能反复触发，晚到的旧消息必须能被认出来丢掉。
/// </summary>
internal static class CombatRewindProtocol
{
    /// <summary>协议标识，写进日志方便与游戏自身消息区分。</summary>
    public const string Tag = "[CombatRewind]";
}

/// <summary>
/// 主机 → 全体：提议把战斗回退到 <see cref="RoundNumber"/> 的开头。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CombatRewindRequestMessage : INetMessage, IPacketSerializable
{
    /// <summary>一次共识轮次的唯一标识，用于丢弃过期消息。</summary>
    public uint Token;

    /// <summary>目标回合号（<c>CombatState.RoundNumber</c>）。</summary>
    public int RoundNumber;

    /// <summary>主机在目标回合开头抓到的校验和，供各端与自己的快照比对。</summary>
    public uint Checksum;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;

    /// <summary>
    /// 必须为 true：客机可能正在加载（重连重建中），
    /// 消息需要排队等它就绪，否则回溯指令会丢失。
    /// </summary>
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(Token);
        writer.WriteInt(RoundNumber);
        writer.WriteUInt(Checksum);
    }

    public void Deserialize(PacketReader reader)
    {
        Token = reader.ReadUInt();
        RoundNumber = reader.ReadInt();
        Checksum = reader.ReadUInt();
    }
}

/// <summary>
/// 客机 → 主机：对 <see cref="CombatRewindRequestMessage"/> 的表态。
///
/// <see cref="Agree"/> 为 false 表示该端不同意（手里没有这个回合的快照 / 校验和对不上 / 不在战斗中），
/// 主机收到任一反对就放弃本次回溯，保持现状 —— 宁可不回退，也不能制造分歧。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CombatRewindVoteMessage : INetMessage, IPacketSerializable
{
    public uint Token;
    public int RoundNumber;

    /// <summary>本端在目标回合开头抓到的校验和（没有快照时为 0）。</summary>
    public uint Checksum;

    public bool Agree;

    /// <summary>本端当前是否在战斗中。不在战斗中（已离开战斗）也视为不同意。</summary>
    public bool InCombat;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(Token);
        writer.WriteInt(RoundNumber);
        writer.WriteUInt(Checksum);
        writer.WriteBool(Agree);
        writer.WriteBool(InCombat);
    }

    public void Deserialize(PacketReader reader)
    {
        Token = reader.ReadUInt();
        RoundNumber = reader.ReadInt();
        Checksum = reader.ReadUInt();
        Agree = reader.ReadBool();
        InCombat = reader.ReadBool();
    }
}

/// <summary>
/// 主机 → 全体：回溯表决的最终结果，并携带权威的全量战斗状态。
///
/// <see cref="Approved"/> 为 true 时 <see cref="FullState"/> 一定有值，所有端应用它；
/// 为 false 时表示有端反对或主机拿不到快照，各方保持现状（由上层走原来的兜底流程）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CombatRewindResultMessage : INetMessage, IPacketSerializable
{
    public uint Token;
    public int RoundNumber;
    public uint Checksum;
    public bool Approved;

    /// <summary>
    /// 权威的全量战斗状态（主机在目标回合开头抓的那一份）。
    /// 用 class 承载，序列化前写一个 bool 标记是否有值。
    /// </summary>
    public NetFullCombatState? FullState;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(Token);
        writer.WriteInt(RoundNumber);
        writer.WriteUInt(Checksum);
        writer.WriteBool(Approved);

        // NetFullCombatState 是 class，先写存在标记；
        // 直接调它的 Serialize（它本身实现了 IPacketSerializable），不套 Write<T> 泛型。
        bool hasState = Approved && FullState != null;
        writer.WriteBool(hasState);
        if (hasState)
            FullState!.Serialize(writer);
    }

    public void Deserialize(PacketReader reader)
    {
        Token = reader.ReadUInt();
        RoundNumber = reader.ReadInt();
        Checksum = reader.ReadUInt();
        Approved = reader.ReadBool();

        FullState = null;
        if (reader.ReadBool())
        {
            var state = new NetFullCombatState();
            state.Deserialize(reader);
            FullState = state;
        }
    }
}
