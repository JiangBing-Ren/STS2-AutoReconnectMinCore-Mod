using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using AutoReconnect.Scripts.MenuRejoin;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.9.31 — 重连主控制器（主线程 Godot Node）。
///
/// 每次重连流程（仿 Game Lobby 的 ManagedJoinFlow rejoin 分支）：
///   Card 3（v0.9.29 默认、不回退、零重建）：
///     1. 复用已断开的 RunManager.NetService 旧实例（其 _messageBus 上仍挂着全部战斗同步 Handler，
///        Disconnect() 只拆传输层不卸载 Handler——见 NetClientGameService.Disconnect）。
///     2. SteamClientConnectionInitializer.Connect(oldSvc) 重新拨号传输层（该 API 本就设计用于复用已断开实例）。
///     3. 仅清空/重注册“握手类”Handler（InitialGameInfoMessage / ClientRejoinResponseMessage），
///        战斗同步 Handler 原样保留 → 状态一致时零重建、对玩家完全无感。
///     4. 等待 InitialGameInfoMessage / ClientRejoinResponseMessage 完成握手。
///   降级（仅当状态不一致：ForceFullRebuild / NumReloads 不同）：
///     断开旧实例、走“新建 NetService + 整局重建”（与 v0.9.28 完全一致，已实战验证）。
///
/// NetService.Update() 由本节点的 _Process 每帧驱动；游戏自身也驱动 RunManager.Instance.NetService.Update()，
/// 双重 Update 安全（NetClient.Update 每次清空接收缓冲，不会重复派发）。
/// 消息等待使用 Godot 帧轮询（await ToSignal(ProcessFrame)），确保全程在主线程，
/// 满足 Godot/Steamworks 的线程安全要求。
/// </summary>
public partial class ReconnectRunner : Node
{
    /// <summary>
    /// v0.9.15: 指数退避重试（借鉴 Rewind 重放健壮性）。
    /// 第1次失败等 5s，第2次等 10s，第3次等 20s，上限 30s。
    /// 网络抖动通常几秒就恢复，但如果主机在做回退/重建，
    /// 越来越长的间隔能给主机足够时间完成操作。
    /// </summary>
    private const int MaxRetries = 3;
    private const float BaseRetryDelaySeconds = 5.0f;
    private const float MaxRetryDelaySeconds = 30.0f;
    private const float HandshakeTimeoutSeconds = 20.0f;

    /// <summary>v0.9.15: 指数退避——计算当前 attempt 的等间隔（秒）。</summary>
    private static float RetryDelayForAttempt(int attempt)
        => Math.Min(BaseRetryDelaySeconds * (float)Math.Pow(2, attempt - 1), MaxRetryDelaySeconds);

    private int _attempt;
    private bool _isRunning;
    private ReconnectOverlay? _overlay;
    private NetClientGameService? _netService;
    private CancellationTokenSource? _cts;

    /// <summary>v0.7.1 — 最近一次尝试的失败详情，用于失败弹窗展示「卡在哪一步 + 为什么」。</summary>
    private ReconnectFailure? _lastFailure;

    /// <summary>v0.7.1 — 最近一次使用的连接方式描述（FromLobby / FromPlayer），用于弹窗。</summary>
    private string _connLabel = "未知";

    /// <summary>
    /// v0.7.1 — <see cref="AwaitWithTimeoutAndDisconnect{T}"/> 的失败原因传出通道。
    /// C# 不允许 async 方法带 out 参数，因此用字段承载。
    /// </summary>
    private string _lastWaitFailReason = "未知原因";

    /// <summary>主机 Steam ID（断线瞬间由触发方捕获并写入），用于 FromPlayer 重建 Steam lobby 连接。0 表示未知。</summary>
    public ulong HostSteamId { get; set; }

    /// <summary>
    /// v0.9.7 —— 强制走「整局重建」而不是「只换连接」的快路径。
    ///
    /// 由 <c>LocalPlayerDisconnectedPatch</c> 在断开原因是
    /// <see cref="NetError.StateDivergence"/> 时置 true。理由：
    ///   · 主机检查点回退 → 主机 NumReloads 会 +1，本来也会判定成「主机状态已变」；
    ///   · 但真·战斗中校验和分歧时 NumReloads 两边是**一样**的，
    ///     此时若走快路径就等于带着一份已经错了的内存态继续跑，下一帧照样再分歧一次。
    /// 既然对方已经明确告诉我们“你的状态和我对不上”，那就只有整份重建这一条正确路径。
    /// </summary>
    public bool ForceFullRebuild { get; set; }

    private TaskCompletionSource<InitialGameInfoMessage>? _connectCompletion;
    private TaskCompletionSource<ClientRejoinResponseMessage>? _rejoinCompletion;
    private TaskCompletionSource<NetErrorInfo>? _disconnectCompletion;

    /// <summary>Card 3 复用路径的结果：成功零重建 / 降级整局重建 / 复用失败。</summary>
    private enum ReuseOutcome { Success, Fallback, Failed }

    /// <summary>复用路径失败时的原因（由 <see cref="PerformReuseReconnectAsync"/> 写入，供外层返回）。</summary>
    private ReconnectFailure? _reuseFailure;

    public override void _Ready()
    {
        _attempt = 0;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        ShowOverlay();
        Diag.Log("ReconnectRunner._Ready: starting async reconnect (v0.6.5)");
        _ = RunReconnectAsync();
    }

    public override void _Process(double delta)
    {
        if (_netService != null)
        {
            try
            {
                _netService.Update();
            }
            catch (Exception ex)
            {
                Diag.Log($"ReconnectRunner._Process: NetService.Update threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ShowOverlay()
    {
        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay))
            _overlay = ReconnectOverlay.Create(this);
    }

    private async Task RunReconnectAsync()
    {
        while (_isRunning && _attempt < MaxRetries)
        {
            _attempt++;
            if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
                _overlay.SetAttempt(_attempt, MaxRetries);
            Diag.Log($"ReconnectRunner: attempt {_attempt}/{MaxRetries}");

            var failure = await PerformReconnectAsync(_cts!.Token);
            _lastFailure = failure;
            if (failure == null)
            {
                OnReconnectSuccess();
                return;
            }

            if (_isRunning && _attempt < MaxRetries && !_cts.IsCancellationRequested)
            {
                var delay = RetryDelayForAttempt(_attempt);
                Diag.Log($"ReconnectRunner: attempt {_attempt} failed ({failure}), retry in {delay:F0}s (exponential backoff: {RetryDelayForAttempt(1):F0}→{RetryDelayForAttempt(2):F0}→{RetryDelayForAttempt(3):F0})");
                var timer = GetTree().CreateTimer(delay);
                await ToSignal(timer, Godot.Timer.SignalName.Timeout);
            }
            else
            {
                break;
            }
        }

        if (_isRunning)
            OnReconnectFailed();
    }

    /// <summary>
    /// 执行一次完整重连。返回 <c>null</c> 表示成功；返回 <see cref="ReconnectFailure"/> 时
    /// 携带「卡在哪一步 + 为什么」，供失败弹窗展示（v0.7.1 之前只返回 bool，原因全丢了）。
    /// </summary>
    private async Task<ReconnectFailure?> PerformReconnectAsync(CancellationToken token)
    {
        var stage = ReconnectStage.ResolvingTarget;
        try
        {
            // ───────────────────────────────────────────────────────────────────
            // Card 3（v0.9.29，默认、不回退、零重建）：优先复用已断开的旧 NetService
            // ───────────────────────────────────────────────────────────────────
            // 旧实例 _messageBus 上仍挂着全部战斗同步 Handler（PeerInputMessage /
            // SyncRngMessage / ChecksumDataMessage / StateDivergenceMessage…），
            // Disconnect() 只拆传输层、不卸载 Handler（见 NetClientGameService.Disconnect）。
            // 因此只需重新拨号传输层（SteamClientConnectionInitializer.Connect 本就设计用来
            // 复用已断开实例：要求 IsConnected==false），即可“零重建”无缝接回——
            // 抖动场景下对玩家完全无感。
            // 仅当状态不一致（ForceFullRebuild / NumReloads 不同）才降级到“新建 NetService
            // 整局重建”（与 v0.9.28 完全一致，已实战验证）。
            var oldSvc = RunManager.Instance?.NetService as NetClientGameService;
            bool canReuse = oldSvc != null && !oldSvc.IsConnected && !ForceFullRebuild;
            if (canReuse)
            {
                var reuse = await PerformReuseReconnectAsync(oldSvc!, token);
                if (reuse == ReuseOutcome.Success)
                    return null; // 快路径成功：零重建，oldSvc 已是 RunManager.NetService，保留让 _Process / 游戏继续驱动
                if (reuse == ReuseOutcome.Failed)
                    return _reuseFailure; // PerformReuseReconnectAsync 已 Cleanup 并记录失败原因
                // ReuseOutcome.Fallback：oldSvc 已断开并摘除本 Runner 处理器，落到下方“新建 NetService”标准流程
                Diag.Log("PerformReconnect: 复用路径降级为「新建 NetService」标准流程（整局重建）。");
            }

            // ───────────────────────────────────────────────────────────────────
            // 标准流程（新建 NetService）：与 v0.9.28 完全一致
            // ───────────────────────────────────────────────────────────────────
            // 1. 新建 NetService（v0.1.8 复用已死实例是错的；但 v0.9.29 已改为“优先复用”，见上）
            _netService = new NetClientGameService(PeerVersionInfo.LocalDefault());
            _connectCompletion = new TaskCompletionSource<InitialGameInfoMessage>();
            _rejoinCompletion = new TaskCompletionSource<ClientRejoinResponseMessage>();
            _disconnectCompletion = new TaskCompletionSource<NetErrorInfo>();

            _netService.RegisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
            _netService.RegisterMessageHandler<ClientRejoinResponseMessage>(HandleRejoinResponse);
            _netService.Disconnected += HandleDisconnected;

            Diag.Log("PerformReconnect: NetService created, resolving connection target...");

            // 2. 解析连接方式。统一走 ReconnectService.CreateInitializer：
            //    优先 FromLobby(捕获到的大厅 ID)，回退 FromPlayer(主机 Steam ID)。
            //    （长断线后 GetFriendGamePlayed 会失效，FromPlayer 直接 InvalidJoin。）
            var initializer = ReconnectService.CreateInitializer(HostSteamId, out _connLabel, out var resolveFail);
            if (initializer == null)
            {
                CleanupNetService();
                return new ReconnectFailure(ReconnectStage.ResolvingTarget, resolveFail);
            }

            stage = ReconnectStage.Connecting;
            Diag.Log($"PerformReconnect: connecting via {_connLabel} ...");
            NetErrorInfo? connectError = await initializer.Connect(_netService, token);
            if (connectError.HasValue)
            {
                var why = ReconnectDiagnostics.DescribeNetErrorInfo(connectError.Value);
                Diag.Log($"PerformReconnect: connect FAILED: {why}");
                CleanupNetService();
                return new ReconnectFailure(stage, $"{why}｜连接方式：{_connLabel}");
            }
            Diag.Log("PerformReconnect: connected, awaiting InitialGameInfoMessage...");

            // 3. 等 InitialGameInfoMessage
            stage = ReconnectStage.AwaitingGameInfo;
            var initial = await AwaitWithTimeoutAndDisconnect(
                _connectCompletion.Task, "InitialGameInfoMessage");
            if (initial == null)
            {
                CleanupNetService();
                return new ReconnectFailure(stage, _lastWaitFailReason);
            }

            Diag.Log($"PerformReconnect: sessionState={initial.Value.sessionState}");

            // 4. 仅 Running 状态可重连
            stage = ReconnectStage.CheckingSession;
            if (initial.Value.sessionState != RunSessionState.Running)
            {
                Diag.Log($"PerformReconnect: sessionState={initial.Value.sessionState} != Running, cannot rejoin");
                CleanupNetService();
                return new ReconnectFailure(stage,
                    $"主机当前会话状态是 {initial.Value.sessionState}，不是「对局进行中(Running)」");
            }

            // 5. 发 ClientRejoinRequestMessage，等 ClientRejoinResponseMessage
            //    （v0.111.0 起 ClientRejoinRequestMessage 不再含 versionInfo 字段，版本由游戏内部填充）
            stage = ReconnectStage.Handshaking;
            Diag.Log("PerformReconnect: sending ClientRejoinRequestMessage...");
            _netService.SendMessage(new ClientRejoinRequestMessage());

            var rejoin = await AwaitWithTimeoutAndDisconnect(
                _rejoinCompletion.Task, "ClientRejoinResponseMessage");
            if (rejoin == null)
            {
                CleanupNetService();
                return new ReconnectFailure(stage, _lastWaitFailReason);
            }
            Diag.Log("PerformReconnect: rejoin response received");

            // 6. 决定用哪条恢复路径。
            //    v0.9.0 新增：主机可能在我们掉线期间执行了**检查点回退**，run 被换成了更早的快照。
            //    这种情况下沿用「只换 NetService」的快路径 = 带着过期状态继续跑 → 必然 StateDivergence。
            //    判据用基游戏自带的 NumReloads：主机每次 SetUpSaved* 都会 +1，普通抖动重连则两边一致。
            stage = ReconnectStage.RestoringRun;
            int localReloads = MenuRejoinFlow.GetLocalNumReloads();
            int hostReloads = rejoin.Value.serializableRun?.NumReloads ?? -1;
            bool reloadsDiffer = localReloads >= 0 && hostReloads >= 0 && hostReloads != localReloads;
            // [v0.9.31] 同复用路径：战斗中主机推进但 NumReloads 不变 → 比对战斗态，
            // 不一致则整局重建（从主机当前 serializableRun，怪正确）。
            bool combatMismatch = HostCombatStateMismatch(rejoin);
            bool hostStateChanged = ForceFullRebuild || reloadsDiffer || combatMismatch;

            Diag.Log($"PerformReconnect: NumReloads 本地={localReloads} 主机={hostReloads}" +
                     $"，战斗态{(combatMismatch ? "不一致" : "一致")}" +
                     (ForceFullRebuild ? "，断开原因=StateDivergence（强制整局重建）" : "") + " → " +
                     (hostStateChanged ? "走整局重建" : "状态一致，走快路径（只换连接）"));

            if (hostStateChanged)
            {
                try
                {
                    // 整局重建期间不能再让 _Process 驱动 _netService：
                    // EnterRunFromRejoin 会自己挂 NetUpdatePump，LoadRun 之后由 NRun/RunManager 接手，
                    // 双重 Update 会重复派发消息。同时摘掉本 Runner 的处理器——本节点马上就要 QueueFree，
                    // 留着回调会在节点释放后被触发，指向已析构的 Godot 对象。
                    var svc = _netService;
                    _netService = null;
                    DetachHandlers(svc!);
                    await MenuRejoinFlow.RebuildRunInPlace(svc!, rejoin.Value);
                    Diag.Log("PerformReconnect: 整局重建完成（已同步到主机当前存档），reconnect SUCCESS");
                    return null;
                }
                catch (Exception rebuildEx)
                {
                    Diag.Log($"PerformReconnect: 整局重建失败 - {rebuildEx.GetType().Name}: {rebuildEx.Message}");
                    CleanupNetService();
                    return new ReconnectFailure(stage,
                        $"主机已回退到更早的检查点，按新存档重建对局时失败：{rebuildEx.Message}");
                }
            }

            // 快路径：状态一致，只把新连接接回 RunManager 即可。
            if (!ReconnectService.AssignNetServiceToRunManager(_netService))
            {
                Diag.Log("PerformReconnect: FAILED to assign NetService to RunManager");
                CleanupNetService();
                return new ReconnectFailure(stage, "无法把新连接接回 RunManager（反射未找到可写的 NetService 成员）");
            }
            Diag.Log("PerformReconnect: NetService assigned to RunManager, reconnect SUCCESS");

            // 保留 _netService（不 cleanup），让 _Process 继续驱动它
            return null;
        }
        catch (OperationCanceledException)
        {
            Diag.Log("PerformReconnect: cancelled");
            CleanupNetService();
            return new ReconnectFailure(stage, "重连被取消");
        }
        catch (Exception ex)
        {
            Diag.Log($"PerformReconnect: EXCEPTION - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            CleanupNetService();
            return new ReconnectFailure(stage, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Card 3 复用路径：在已断开的旧 NetService 上重新拨号传输层并完成握手。
    /// 返回 <see cref="ReuseOutcome.Success"/>=零重建快路径成功；
    /// <see cref="ReuseOutcome.Fallback"/>=状态不一致需降级整局重建（已断开并摘除本 Runner 处理器）；
    /// <see cref="ReuseOutcome.Failed"/>=复用失败（已将 <see cref="_reuseFailure"/> 写入原因）。
    /// </summary>
    private async Task<ReuseOutcome> PerformReuseReconnectAsync(NetClientGameService oldSvc, CancellationToken token)
    {
        var stage = ReconnectStage.ResolvingTarget;
        try
        {
            _reuseFailure = null;

            // 准备旧实例：清空“握手类”Handler 列表 + 注册本 Runner 的握手 Handler + 订阅断线
            if (!PrepareOldServiceForReuse(oldSvc))
            {
                _reuseFailure = new ReconnectFailure(ReconnectStage.ResolvingTarget,
                    "复用旧 NetService 的准备失败（反射清理/注册握手 Handler 异常）");
                return ReuseOutcome.Failed;
            }

            // 完成信号槽先建好（HandleInitialGameInfo 等可能在 Connect 后立刻收到 InitialGameInfoMessage）
            _connectCompletion = new TaskCompletionSource<InitialGameInfoMessage>();
            _rejoinCompletion = new TaskCompletionSource<ClientRejoinResponseMessage>();
            _disconnectCompletion = new TaskCompletionSource<NetErrorInfo>();
            _netService = oldSvc; // 让 _Process 在本 Runner 生命周期内驱动它（与游戏自身驱动并存，双重 Update 安全）

            // 解析连接方式
            var initializer = ReconnectService.CreateInitializer(HostSteamId, out _connLabel, out var resolveFail);
            if (initializer == null)
            {
                DetachReuseHandlers(oldSvc);
                _netService = null;
                _reuseFailure = new ReconnectFailure(ReconnectStage.ResolvingTarget, resolveFail);
                return ReuseOutcome.Failed;
            }

            stage = ReconnectStage.Connecting;
            Diag.Log($"PerformReuseReconnect: 复用旧 NetService，via {_connLabel} 重新拨号传输层...");
            NetErrorInfo? connectError = await initializer.Connect(oldSvc, token);
            if (connectError.HasValue)
            {
                var why = ReconnectDiagnostics.DescribeNetErrorInfo(connectError.Value);
                Diag.Log($"PerformReuseReconnect: 重新拨号失败：{why}");
                DetachReuseHandlers(oldSvc);
                _netService = null;
                _reuseFailure = new ReconnectFailure(stage, $"{why}｜连接方式：{_connLabel}");
                return ReuseOutcome.Failed;
            }
            Diag.Log("PerformReuseReconnect: 传输层已重连，等待 InitialGameInfoMessage...");

            // 等 InitialGameInfoMessage
            stage = ReconnectStage.AwaitingGameInfo;
            var initial = await AwaitWithTimeoutAndDisconnect(_connectCompletion.Task, "InitialGameInfoMessage");
            if (initial == null)
            {
                DetachReuseHandlers(oldSvc);
                _netService = null;
                _reuseFailure = new ReconnectFailure(stage, _lastWaitFailReason);
                return ReuseOutcome.Failed;
            }
            Diag.Log($"PerformReuseReconnect: sessionState={initial.Value.sessionState}");

            stage = ReconnectStage.CheckingSession;
            if (initial.Value.sessionState != RunSessionState.Running)
            {
                Diag.Log($"PerformReuseReconnect: sessionState={initial.Value.sessionState} != Running，降级重建。");
                DetachReuseHandlers(oldSvc);
                _netService = null;
                return ReuseOutcome.Fallback;
            }

            // 发 ClientRejoinRequestMessage，等响应
            stage = ReconnectStage.Handshaking;
            Diag.Log("PerformReuseReconnect: 发送 ClientRejoinRequestMessage...");
            oldSvc.SendMessage(new ClientRejoinRequestMessage());

            var rejoin = await AwaitWithTimeoutAndDisconnect(_rejoinCompletion.Task, "ClientRejoinResponseMessage");
            if (rejoin == null)
            {
                DetachReuseHandlers(oldSvc);
                _netService = null;
                _reuseFailure = new ReconnectFailure(stage, _lastWaitFailReason);
                return ReuseOutcome.Failed;
            }
            Diag.Log("PerformReuseReconnect: 收到 rejoin 响应");

            // 路径决策
            int localReloads = MenuRejoinFlow.GetLocalNumReloads();
            int hostReloads = rejoin.Value.serializableRun?.NumReloads ?? -1;
            bool reloadsDiffer = localReloads >= 0 && hostReloads >= 0 && hostReloads != localReloads;
            // [v0.9.31] 战斗态比对：战斗中主机持续推进但 NumReloads 不变，原判定漏掉这类推进
            // → 客机零重建接回过期战斗态（怪不同）。必须直接比对主机/客机战斗态。
            bool combatMismatch = HostCombatStateMismatch(rejoin);
            bool hostStateChanged = ForceFullRebuild || reloadsDiffer || combatMismatch;
            Diag.Log($"PerformReuseReconnect: NumReloads 本地={localReloads} 主机={hostReloads}" +
                     $"，战斗态{(combatMismatch ? "不一致" : "一致")} → " +
                     (hostStateChanged ? "状态不一致，降级整局重建" : "状态一致，零重建快路径"));

            if (hostStateChanged)
            {
                // 降级：断开旧实例、摘除本 Runner 处理器，落回“新建 NetService”整局重建
                try { oldSvc.Disconnect(NetError.Quit); } catch { }
                DetachReuseHandlers(oldSvc);
                _netService = null;
                return ReuseOutcome.Fallback;
            }

            // 快路径成功：保留 oldSvc（已是 RunManager.NetService），零重建
            Diag.Log("PerformReuseReconnect: 状态一致，零重建接回成功。");
            return ReuseOutcome.Success;
        }
        catch (Exception ex)
        {
            Diag.Log($"PerformReuseReconnect: EXCEPTION - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            try { DetachReuseHandlers(oldSvc); } catch { }
            _netService = null;
            _reuseFailure = new ReconnectFailure(stage, $"{ex.GetType().Name}: {ex.Message}");
            return ReuseOutcome.Failed;
        }
    }

    /// <summary>
    /// 准备复用旧实例：先摘本 Runner 可能残留的订阅，再清空“握手类”消息的既有 Handler
    /// （避免与基游戏原连接流程注册的 Handler 重复触发），最后只注册本 Runner 的握手 Handler。
    /// 战斗同步 Handler（PeerInputMessage 等其它类型）原样保留。
    /// </summary>
    private bool PrepareOldServiceForReuse(NetClientGameService oldSvc)
    {
        try
        {
            oldSvc.Disconnected -= HandleDisconnected; // 先摘，避免重复订阅
            ClearMessageHandlersOfType(oldSvc, typeof(InitialGameInfoMessage));
            ClearMessageHandlersOfType(oldSvc, typeof(ClientRejoinResponseMessage));
            oldSvc.RegisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
            oldSvc.RegisterMessageHandler<ClientRejoinResponseMessage>(HandleRejoinResponse);
            oldSvc.Disconnected += HandleDisconnected;
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"PrepareOldServiceForReuse: EXCEPTION - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>摘除本 Runner 在复用实例上注册的握手 Handler 与断线订阅（幂等）。</summary>
    private void DetachReuseHandlers(NetClientGameService svc)
    {
        try { svc.Disconnected -= HandleDisconnected; } catch { }
        try { svc.UnregisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo); } catch { }
        try { svc.UnregisterMessageHandler<ClientRejoinResponseMessage>(HandleRejoinResponse); } catch { }
    }

    /// <summary>
    /// 通过反射清空 NetClientGameService._messageBus._messageHandlers 中指定消息类型的全部 Handler。
    /// 用于在复用旧实例前移除基游戏原连接流程可能残留的握手 Handler，避免重复触发。
    /// </summary>
    private void ClearMessageHandlersOfType(NetClientGameService svc, Type msgType)
    {
        try
        {
            var svcType = svc.GetType();
            var busField = svcType.GetField("_messageBus", BindingFlags.NonPublic | BindingFlags.Instance);
            var bus = busField?.GetValue(svc);
            if (bus == null) return;
            var handlersField = bus.GetType().GetField("_messageHandlers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (handlersField == null) return;
            if (handlersField.GetValue(bus) is System.Collections.IDictionary dict)
                dict.Remove(msgType);
        }
        catch (Exception ex)
        {
            Diag.Log($"ClearMessageHandlersOfType: EXCEPTION - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 判定「主机当前战斗态」与「客机本地战斗态」是否不一致。
    /// 这是修复「战斗内掉线 → 零重建接回 → 怪不同」的核心判据：
    /// 战斗中主机持续模拟（怪物行动 / 回合推进），但 NumReloads 不变，
    /// 原判定只比 NumReloads 会漏掉这类推进，导致客机带着过期战斗态继续跑。
    ///
    /// 判据（复用游戏自带 ChecksumTracker.GenerateChecksum，与官方分歧检测同源）：
    ///   - 双方都不在战斗 → 一致（false）
    ///   - 一方在战斗一方不在 → 主机推进过（true）
    ///   - 双方都在战斗 → 比对战斗态 XxHash32 校验和，相等才一致（false）
    /// 取不到校验器 / 抛异常时保守返回 true（降级整局重建，保证怪正确）。
    /// </summary>
    private bool HostCombatStateMismatch(ClientRejoinResponseMessage? rejoin)
    {
        try
        {
            var localRun = RunManager.Instance?.DebugOnlyGetState();
            if (localRun == null) return true; // 无本地 run，保守重建

            var hostCombat = rejoin?.combatState; // NetFullCombatState?
            bool hostInCombat = MenuRejoinFlow.HostIsInCombat(hostCombat);
            bool clientInCombat = MenuRejoinFlow.HostIsInCombat(
                NetFullCombatState.FromRun(localRun, null));

            if (hostInCombat != clientInCombat)
                return true; // 一方在战斗一方不在 → 主机推进过
            if (!hostInCombat)
                return false; // 双方都不在战斗 → 一致

            // 双方都在战斗：用游戏自带校验器精确比对（与官方 StateDivergence 检测同源）
            var tracker = RunManager.Instance?.ChecksumTracker;
            if (tracker == null) return true; // 取不到校验器，保守重建
            if (hostCombat == null) return true; // 双都在战斗却不应为空，保守重建
            uint hostCs = tracker.GenerateChecksum(hostCombat);
            uint clientCs = tracker.GenerateChecksum(NetFullCombatState.FromRun(localRun, null));
            return hostCs != clientCs;
        }
        catch (Exception ex)
        {
            Diag.Log($"HostCombatStateMismatch: 异常（保守视为不一致）→ {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 主线程帧轮询等待：每帧 await ProcessFrame，检查 completion / 断线 / 超时。
    /// 避免使用 Task.Delay/Task.Run，确保 NetService 操作始终在主线程。
    ///
    /// 返回 null 时，失败的具体原因写在 <see cref="_lastWaitFailReason"/>
    /// （async 方法不能带 out 参数，故用字段传出），
    /// 让上层弹窗能区分「主机把我踢了(1016)」和「主机没理我(超时)」。
    /// </summary>
    private async Task<T?> AwaitWithTimeoutAndDisconnect<T>(Task<T> task, string label) where T : struct
    {
        _lastWaitFailReason = "未知原因";
        ulong startMs = Time.GetTicksMsec();
        ulong timeoutMs = (ulong)(HandshakeTimeoutSeconds * 1000);
        Diag.Log($"Await({label}): 开始等待，超时设置 {HandshakeTimeoutSeconds:F0}s（{timeoutMs}ms）");
        while (_isRunning && !_cts!.IsCancellationRequested)
        {
            if (task.IsCompleted)
                return await task;
            if (_disconnectCompletion!.Task.IsCompleted)
            {
                var info = await _disconnectCompletion.Task;
                var why = ReconnectDiagnostics.DescribeNetErrorInfo(info);
                Diag.Log($"Await({label}): disconnected during wait — {why}");
                _lastWaitFailReason = $"等待「{label}」期间被主机断开：{why}";
                return null;
            }
            if (Time.GetTicksMsec() - startMs > timeoutMs)
            {
                Diag.Log($"Await({label}): timeout after {HandshakeTimeoutSeconds}s");
                _lastWaitFailReason = $"等待「{label}」超过 {HandshakeTimeoutSeconds:F0} 秒仍无响应";
                return null;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _lastWaitFailReason = "重连流程已被取消";
        return null;
    }

    private void HandleInitialGameInfo(InitialGameInfoMessage message, ulong _)
    {
        Diag.Log("HandleInitialGameInfo: received");
        _connectCompletion?.TrySetResult(message);
    }

    private void HandleRejoinResponse(ClientRejoinResponseMessage message, ulong _)
    {
        Diag.Log("HandleRejoinResponse: received");
        _rejoinCompletion?.TrySetResult(message);
    }

    private void HandleDisconnected(NetErrorInfo info)
    {
        var reason = ReconnectDiagnostics.TryGetReason(info);
        Diag.Log($"HandleDisconnected: reason={(reason.HasValue ? reason.Value.ToString() : "(reason unavailable)")}");
        _disconnectCompletion?.TrySetResult(info);
    }

    /// <summary>
    /// 摘掉本 Runner 注册在指定连接上的全部回调。
    /// 用于「把连接移交给 MenuRejoinFlow 整局重建」之前——本节点随后会 QueueFree，
    /// 若回调还挂着，之后任何一条消息都会打到已释放的 Godot 节点上。
    /// </summary>
    private void DetachHandlers(NetClientGameService svc)
    {
        try { svc.Disconnected -= HandleDisconnected; } catch { }
        try { svc.UnregisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo); } catch { }
        try { svc.UnregisterMessageHandler<ClientRejoinResponseMessage>(HandleRejoinResponse); } catch { }
    }

    private void CleanupNetService()
    {
        try
        {
            if (_netService != null)
            {
                _netService.Disconnected -= HandleDisconnected;
                _netService = null;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"CleanupNetService: {ex.Message}");
        }
    }

    private void OnReconnectSuccess()
    {
        Diag.Log("OnReconnectSuccess: cleaning up overlay and runner");
        _isRunning = false;
        ReconnectService.OnReconnectFinished();

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();

        // v0.7.1 —— 成功也要给玩家一个明确回执（此前成功是完全静默的，
        // 玩家分不清「真的连回来了」还是「界面卡住了」）。
        ReconnectDiagnostics.ReportClientSuccess(_connLabel, _attempt, MaxRetries,
            "对局已恢复同步，可以继续游戏。");

        // _netService 保留，让 _Process 继续驱动新连接
        QueueFree();
    }

    private void OnReconnectFailed()
    {
        var failure = _lastFailure ?? new ReconnectFailure(ReconnectStage.Idle, "未知原因");
        Diag.Log($"OnReconnectFailed: 重连放弃 — {failure}");
        _isRunning = false;
        ReconnectService.OnReconnectFinished();
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.ShowManualButton();

        // v0.7.1 —— 不再统一弹「等本场战斗结束后再重连」（那只是众多失败原因里的一种，
        // 直接对所有失败都这么说会误导玩家）。改为展示真实的阶段 + 原因，并保留「重试」按钮。
        ReconnectDiagnostics.ReportClientFailure(
            failure.Stage,
            failure.Reason,
            _attempt,
            MaxRetries,
            withRetryButton: true,
            hintOverride: $"连接方式：{_connLabel}。\n{ReconnectDiagnostics.StageHint(failure.Stage)}");
    }

    public void OnManualRetry()
    {
        Diag.Log("OnManualRetry: resetting and retrying");
        _attempt = 0;
        _isRunning = true;
        HostInfoTracker.IsReconnecting = true;

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.HideManualButton();

        _ = RunReconnectAsync();
    }

    public void OnCancel()
    {
        Diag.Log("OnCancel: user cancelled reconnection");
        _cts?.Cancel();
        _isRunning = false;
        HostInfoTracker.IsReconnecting = false;
        CleanupNetService();

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();

        QueueFree();
        HostInfoTracker.Reset();

        var rm = RunManager.Instance;
        var method = rm?.GetType().GetMethod("ReturnToMainMenuWithError",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method?.Invoke(rm, new object?[] { null });
    }
}
