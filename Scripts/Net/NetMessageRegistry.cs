using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace AutoReconnect.Scripts.Net;

/// <summary>
/// 需要把消息 handler 挂到当前 NetService 上的模块。
/// </summary>
public interface INetMessageModule
{
    /// <summary>
    /// 向给定的 NetService 注册本模块关心的全部消息 handler。
    /// 可能被反复调用（每次 NetService 实例变化都会重来一遍），实现必须幂等安全。
    /// </summary>
    void RegisterHandlers(INetGameService netService);
}

/// <summary>
/// 按 NetService 实例去重的多人消息注册器。
///
/// ## 它解决什么
///
/// <c>NetMessageBus._messageHandlers</c> 是**每个 NetService 实例一份**的字典。
/// 重连时我们会换掉 <c>RunManager.NetService</c>（见 <see cref="NetServiceSwapper"/>），
/// 新实例上没有任何 mod handler —— 换完不重新注册，消息就全部石沉大海。
/// 这正是 MpLib 的 <c>Mp.Core.NetBootstrap</c> 在补的洞，我们照它的思路自研一份，不引依赖。
///
/// ## 与 MpLib 共存为什么不冲突
///
/// 消息 ID **不归 mod 管**：<c>MessageTypes.Initialize()</c> 在游戏启动时把
/// 「内置消息 + <c>ReflectionHelper.GetSubtypesInMods<INetMessage>()</c> 扫到的全部 mod 消息」
/// 合在一起，交给 <c>ContentSorter</c> 按确定全序排序后按下标发号：
/// <c>affectsGameplay</c> 降序 → 类型短名 → mod id → FullName → Assembly。
/// 该全序与 mod 加载顺序无关，只要各端装的 mod 集合一致，ID 就一致。
/// 因此多个 mod 各自定义 INetMessage **天然不会撞车**，也不需要任何集中式 ID 分配。
///
/// handler 同样隔离：<c>_messageHandlers</c> 按消息 <c>Type</c> 分桶，
/// 两个 mod 注册的是各自的类型，互不干扰。
///
/// ## 相对 MpLib 的两点改进
///
/// 1. **弱引用持有已注册实例**：MpLib 用 <c>object? _registeredService</c> 强引用，
///    旧 NetService 会被一直钉住无法回收；这里用 <see cref="WeakReference"/>。
///    实例若已被 GC，下次 <see cref="EnsureRegistered"/> 会重新注册。
/// 2. **逐模块 try/catch**：一个模块挂了不影响其他模块，失败只记日志。
///
/// ## 最坏情况
///
/// 完全没注册上也不会崩：<c>NetMessageBus.SendMessageToAllHandlers</c> 找不到 handler 时
/// 只是 <c>Log.Error</c> 后丢弃消息。所以本文件出问题的最坏结果是「功能失效」，不是「游戏报错」。
/// </summary>
public static class NetMessageRegistry
{
    private const string Tag = "[MsgRegistry]";

    private static readonly List<INetMessageModule> _modules = new();
    private static readonly object _sync = new();

    /// <summary>上次注册到的 NetService（弱引用，避免钉住旧连接对象）。</summary>
    private static WeakReference? _registeredService;

    private static int _rounds;
    private static bool _coexistenceLogged;

    /// <summary>
    /// 登记一个模块。同类型重复登记会被忽略（幂等）。
    /// </summary>
    public static void Add(INetMessageModule module)
    {
        if (module == null) return;
        Type type = module.GetType();

        lock (_sync)
        {
            if (_modules.Any(m => m.GetType() == type)) return;
            _modules.Add(module);
        }

        Diag.Log($"{Tag} 已登记模块 {type.Name}。");
    }

    /// <summary>
    /// 确保当前 NetService 上挂着全部模块的 handler。幂等，可任意频繁调用。
    /// </summary>
    /// <returns>本次是否真的做了注册（false = 无模块 / 无 NetService / 已注册过同一实例）。</returns>
    public static bool EnsureRegistered()
    {
        INetMessageModule[] snapshot;
        lock (_sync)
        {
            if (_modules.Count == 0) return false;
            snapshot = _modules.ToArray();
        }

        INetGameService? svc;
        try
        {
            svc = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.NetService;
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 取 RunManager.NetService 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (svc == null) return false;

        // 已注册到同一实例且该实例还活着 → 什么都不做。
        // 注意 ReferenceEquals 对 null 也成立（两边都 null 时会走上面的 svc == null 返回）。
        if (ReferenceEquals(_registeredService?.Target, svc)) return false;

        int ok = 0, fail = 0;
        foreach (var module in snapshot)
        {
            try
            {
                module.RegisterHandlers(svc);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                Diag.Log($"{Tag} 模块 {module.GetType().Name} 注册失败（已跳过）：" +
                         $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        _registeredService = new WeakReference(svc);
        _rounds++;

        Diag.Log($"{Tag} 已向 {svc.Type}/{svc.GetType().Name} 注册 {ok} 个模块" +
                 (fail > 0 ? $"（失败 {fail} 个）" : "") + $"，累计 {_rounds} 轮。");

        LogCoexistenceOnce();
        return ok > 0;
    }

    /// <summary>
    /// 作废当前注册记录，强制下次 <see cref="EnsureRegistered"/> 重新注册。
    /// 在已知 NetService 被替换、但引用比对可能看不出变化时调用。
    /// </summary>
    public static void Invalidate()
    {
        _registeredService = null;
    }

    /// <summary>诊断用：模块数量、注册轮次、当前绑定实例。</summary>
    public static string Describe()
    {
        int count;
        lock (_sync) { count = _modules.Count; }

        var target = _registeredService?.Target;
        string bound = target == null ? "<无/已回收>" : target.GetType().Name;
        return $"模块 {count} 个，已注册 {_rounds} 轮，当前绑定 {bound}";
    }

    /// <summary>
    /// 首次成功注册时记一条共存诊断。
    /// 玩家反馈「两个 mod 一起装会不会炸」时，看这条日志就知道当时现场装了什么。
    /// </summary>
    private static void LogCoexistenceOnce()
    {
        if (_coexistenceLogged) return;
        _coexistenceLogged = true;

        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool mpLib = loaded.Contains("MpLib") || loaded.Contains("MpLib.loader");
            Diag.Log(mpLib
                ? $"{Tag} 检测到 MpLib 同时启用。两侧消息类型各自独立、ID 由游戏全局排序统一分配，互不影响。"
                : $"{Tag} 未检测到 MpLib（单独运行）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"{Tag} 共存自检失败（不影响功能）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
