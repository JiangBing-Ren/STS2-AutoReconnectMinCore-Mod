# STS2 已启用 Mod 生态研究知识库（12 mod 逆向/学习汇总）

> 2026-10-22 整理。范围：游戏当前启用的 12 个 mod。方法：**有仓库的浅克隆读源码**（5 个），**无仓库的对 release DLL 做 ilspycmd 逆向 + sts2sig 元数据核对**（7 个中的 6 个皮皮系 + CombatSolver 有仓库，实际无仓库仅 6 个皮皮 mod）。目标：借鉴改进自家 `AutoReconnectMinCore`（联机重连）。
> 四份详细报告在仓库外：`D:\1Mods\杀戮尖塔2\_modresearch\reports\{libs,solver-modconfig,quicklink-rewind,qol-danmaku}.md`。

---

## 1. 总览：12 个已启用 mod 形态分布

| Mod | Workshop ID | 版本 | 来源形态 | 分析方式 |
|---|---|---|---|---|
| STS2-RitsuLib | 3747602295 | 0.6.9 | 开源仓库 | 克隆读源码 |
| BaseLib-StS2 | 3737335127 | 3.4.7 | 开源仓库 | 克隆读源码 |
| JmcModLib | 3747526103 | 1.9.0 | 开源仓库 | 克隆读源码 |
| CombatSolver | 3790899961 | 0.48.0 | 开源仓库（MIT） | 克隆读源码 |
| ModConfig-STS2 | 3747557003 | 0.2.2 | 开源仓库（MIT） | 克隆读源码 |
| **AutoReconnectMinCore（自家）** | 3783056249 | 0.9.37-core | 本仓库 | — |
| QuickLink（皮皮快连） | 3747557834 | 0.9.13 | release DLL，混淆 | ilspycmd 逆向 |
| Rewind（皮皮倒带） | 3747557762 | 0.26.19 | release DLL，混淆 | ilspycmd 逆向 |
| SpeedX（皮皮极速） | 3747557357 | 0.11.12 | release DLL，混淆 | ilspycmd 逆向 |
| DamageMeter（皮皮统计） | 3747557283 | 1.14.7 | release DLL，未混淆 | ilspycmd 逆向 |
| SkadaHelper（皮皮军师） | 3763804482 | 0.22.2 | release DLL，混淆 | ilspycmd 逆向 |
| DanmakuSpire（弹幕尖塔） | 3779807977 | — | release DLL，未混淆 | ilspycmd 逆向 |

**皮皮家族**（`sts2.piyixiajiuhenfen.*`，同作者 6 个）仅 ModConfig 开源；其余 5 个走 release 分发。混淆特征：字符串 XOR 解密（`SolverStub.CalcAutomatableEmitter`）+ 标识符混淆，**控制流未混淆**，游戏侧 API 调用均可用 sts2sig 核实。

---

## 2. 前置库三库（mod 生态的框架层）

### 2.1 RitsuLib（自家 mod 的依赖，最熟）
- 入口 `[ModInitializer]` + `CreatePatcher(modId, name)` 每逻辑区一个 Harmony 实例；补丁类实现 `IPatchMethod`（`GetTargets(): ModPatchTarget[]` 声明式）；`ApplyRequiredPatcher(patcher, DisableMod)` 关键失败自禁用不崩游戏。
- 生命周期事件订阅 `SubscribeLifecycle<TEvent>(...)`，**可回放事件**对迟订阅者补发当前状态（重连 mod 晚启动也能拿到 Run 状态）。
- Sidecar 副协议：**lobby 构造即拿 INetGameService**（`MethodType.Constructor` 同补 `StartRunLobby..ctor`×2 + `LoadRunLobby..ctor`×2，Postfix `ObserveNetService`）；**消息尾补丁**（给现有 INetMessage 追加 mod 字节，不占消息 ID）；加入诊断补丁（`JoinFlow.HandleInitialGameInfoMessage` 失败归因）。

### 2.2 JmcModLib（自家 mod 的依赖）
- 下游一行 `ModRegistry.Register<MainFile>()`；`MethodAccessor`/`MemberAccessor` 三级缓存反射（DynamicMethod 快路 → 通用 invoker → MethodInfo.Invoke 降级）。
- `[Config]`/`[UIButton]` 零回调配置 UI（AttributeRouter 扫描分发）；设置页注入 = 复制 Input 页签塞 `NSettingsTabManager._tabs`（反射私有字段）。
- **OptionalNetworkFeature**：关闭联机功能的 mod 通过 `GetGameplayRelevantModNameList` 注入 `JML-ONF1...` 兼容 token 不进校验；`NErrorPopup.Create` Prefix 换自定义弹窗。
- **MultiplayerCompat**（`Lazy<MemberAccessor?> + FindReadableMember(新名, 旧名...)`）：跨版本改名解析模板（NetService/PlayerIds/ConnectedPeers/ConnectionExtraInfo），注释明确 0.107.1 须在 `JoinFlow.Begin()` 之后才拿得到 NetService。

### 2.3 BaseLib
- 经典特性式 `PatchAll`，无 Patcher 抽象；`Prepare()+TargetMethod()` 动态解析做版本兼容（找不到就干净跳过）。
- **CustomMessageWrapper**：一个包装消息承载所有 mod 自定义消息（内层按 FullName hash 得 ID，反射自动发现子类型）；**ID 占用手法**：Postfix `MessageTypes.Initialize` 改写 `_cache` 抢 128-254 高位 + 拦截 `TryGetMessageType`。
- `VariableReference<T>/VariableMethod` 构造时按候选 (Type,name) 逐个 TryName 的多版本成员解析。

---

## 3. 皮皮家族深度要点

### 3.1 QuickLink（与自家 mod 最同构：多人回退+重连，32 个 patch）
- **断线重连核心**（1.4）：`NErrorPopup.Create(NetErrorInfo)` Prefix 白名单判断可重连 reason → `__result=null; return false` 吞弹窗 → 静默重连：5 秒窗口 `SteamFriends.GetFriendGamePlayed(hostId)` 探测房主新 lobby → **18 次重试** `SteamClientConnectionInitializer.FromLobby/FromPlayer` + 反射构造 `JoinFlow` → `joinFlow.Begin(initializer, sceneTree)` → 成功判据 `sessionState==2 && loadJoinResponse.HasValue` → 按 gameMode 走原版 `N*LoadScreen.InitializeAsClient` 压入 SubmenuStack。**不自己造协议/UI**。
- **带内信令**（1.3）：无自定义 INetMessage，劫持 `ReactionMessage`（`type=(ReactionType)6` + `normalizedPosition.X=2.75f` 魔数，6 条指令编码在 Y）；定向发送 = 反射取 `_messageBus` → `NetMessageBus.SerializeMessage<T>` 裸字节 → `NetHost.SendMessageToClient/NetClient.SendMessageToHost(..., NetTransferMode)`。
- **回退 = 重新 hosting checkpoint**（1.5/1.6）：内存 `SerializableRun` 快照（patch `RunSaveManager.SaveRun`，15 上限，按 (StartTime, Act, MapCoord, 玩家集合) 去重）；`CanonicalizeSave(JSON 深拷贝, localPlayerId)` + `new NetHostGameService().StartSteamHost(4)` + `InitializeAsHost`；队友全靠重连回房。
- **自动准备**：轮询 `LoadRunLobby` 已连接玩家数 vs `lobby.Run.Players.Count`，到齐反射调加载屏**私有无参方法**（模拟点击准备，不直接动 `SetReady`）。
- **状态就绪探测器**（1.5）：`MainMenu==null → NRun 就绪 → RunManager.IsInProgress → ActionExecutor 未暂停 →（战斗中）CombatManager/NCombatRoom/NCombatUi/手牌/结束回合按钮` 顺序轮询，全就绪才收遮罩。

### 3.2 Rewind（单机倒带，33 个 patch）
- 战斗倒带复用游戏 `CombatReplay`：快照 = `SerializeReplay` 序列化字节（内含完整 serializableRun）；倒带 = `RunState.FromSerializable` + `ActionQueueSet.Reset` + `CleanUp(true)` + **`NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService())` 会话替换** + `LoadRun` 重载 + 逐事件回放（`EnqueueWithoutSynchronizing` 绕过同步器）。
- **同步器 ID 快进**：`ActionQueueSet.FastForwardNextActionId`/`ActionQueueSynchronizer.FastForwardHookId`/`PlayerChoiceSynchronizer.FastForwardChoiceIds` 取 max 合并——任何重放历史 action 的场景必须处理，否则后续同步必崩。
- 公平模式（抽牌/看库/选牌=泄露信息→禁倒带）；SL 合法 continue 令牌区分合法读档/硬读档；**联机下全部禁用**（作者选择不降级会话）。

### 3.3 SpeedX（倍速 + 自动化，混淆）
- **倍速单点**：patch `Cmd.Wait(float, CancellationToken, bool)` Prefix `ref float seconds → *= num`（游戏所有演出/逻辑延迟都走它）；Spine `SetTimeScale`、VFX `_Process ref double delta`、跳过顿帧 `NHitStop.DoHitStop`；**多人下强制倍率 1**（`NetGameTypeExtensions.IsMultiplayer`）。
- 跳过横幅三连：Prefix `return false` + `QueueFree()` + `__result = Task.CompletedTask`。
- **自动结束回合走 `ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(...))`**（唯一多人同步安全的代打通道，误触入队 `UndoEndPlayerTurnAction` 撤回）；自动前进用 `EmitSignal(Released)` 模拟点击（比反射调私有方法耐更新）。

### 3.4 DamageMeter（战斗统计，纯公开 API，全库仅 1 个 patch）
- **CombatHistory 尾随法**：订阅 `CombatHistory.Changed` + `_lastSeenIndex` 增量遍历 Entries，分流 11 种公开条目类型（DamageReceived/BlockGained/CardPlayFinished/…）——**零 patch 拿全部战斗事件**。
- rDPS = DamageDealt − DamageBoostedByOthers + AssistDamage；多窗口 CanvasLayer（Active=100/被遮挡 -10）。
- `ModManager.GetLoadedMods` 反射枚举其他 mod；`Type.GetType("Rewind.Scripts.TurnRewindManager, Rewind")` 跨 mod 能力探测。

### 3.5 SkadaHelper（牌组建议，16 个 UI postfix）
- 全部是原生界面 postfix 注入建议面板（牌组体检/选牌/商店/事件/地图/悬停）；私有字段 `AccessTools.Field` + 混淆名。
- 体积大头是硬编码数据表（IroncladCardCatalog 311KB 等 5 职业规则）；远程 BundleData JSON 按 GeneratedAt 合并；**UI 完全代码构建，pck 仅元数据**。

### 3.6 DanmakuSpire（弹幕，未混淆，工程范本）
- **GameFactBus 统一事实总线**：~33 个嵌套 patch 类只负责把游戏事件包装成 `IGameFact`（`Publish(new XxxFact(header,...))`），57 个规则消费侧订阅；按 FactType+EventId 去重、LifecyclePolicy 防跨回合过期、2000 条审计环、单 handler 异常不传染。
- **自定义消息协议范本**（`DanmakuNetworkForwarder : INetForwarder`）：5 个 `INetMessage, IPacketSerializable`；握手消息 `Mode=可靠+ShouldBuffer`（晚到者也能收到）、瞬时消息不可靠；`ReadSafeString` 限长 + `Corrupt` 标志 + 条数上限 + `CorrelationId` 去重（4096）+ 分类限速（5~40 条/s）；`RunStarted` EnsureAttached / `RunEnded` Detach。
- patch 安装不用 PatchAll：`HarmonyPatchInstaller.Install` 逐类 `CreateClassProcessor().Patch()` + ImpactCatalog 记录安装成败写兼容性报告。

---

## 4. CombatSolver × ModConfig

### 4.1 CombatSolver（战斗求解器，MIT）
- 运行链：主线程冻结战斗根快照（`CombatRootSnapshot`）→ 后台分叉搜索（`Task.Run` + 专用 lane 线程 BelowNormal，结果按父节点原序回协调线程保证确定性）→ 原版公开入口部署。
- **RNG 不推进**：`CombatPredictionRngSet.CaptureState()` 捕获 9 条原生流 + 懒物化（分支真正消费随机数时才在私有 Rng 实例重放）。
- 帧压感知：主线程 31 帧空闲间隔中位数估基线，压力阈值 = max(33ms, 基线×1.5)，worker spin 等下一帧。
- 执行一律走原生 API（`card.TryManualPlay`/`ActionQueueSynchronizer.RequestEnqueue`），漂移即重搜（`DEPLOY_REPLAN`）。
- 注意：多人已归档，主线仅单人；MemoryCleaner/预战预报仅 Windows。

### 4.2 ModConfig-STS2（通用配置框架，零 Harmony）
- 设置页注入：`SceneTree.NodeAdded` 发现 `NSettingsTabManager` → 反射私有 `_tabs`（IDictionary）→ **`Duplicate()` 克隆**首 tab/panel（AddChild 前先清子节点防 ObjectDisposedException）→ `CallDeferred("set_size")` 钉高度让 ScrollContainer 能滚 → 焦点链重建（反射写 `NSettingsPanel._firstControl`）。
- 消费侧零依赖桥模板（`examples/ModConfigBridge.cs`）：全 AppDomain 扫类型按 FullName 找 API → 延迟一帧探测（解决"我的 mod 先加载"）→ 按参数个数选 Register 重载 → 全链路 try/catch 回退。
- 持久化去抖：脏 modId 集合 + 下一帧 ProcessFrame 一次性 flush；`LiveBinding`（WeakReference）失效自清。

---

## 5. 可借鉴清单（面向 AutoReconnectMinCore，按性价比）

**已在 v0.9.37-core 落地**：
1. ✅ **错误弹窗守卫**（抄 QuickLink 1.4）：`Scripts/Net/ErrorPopupGuardPatch.cs` —— `[HarmonyTargetMethod]` 精确锁 `NErrorPopup.Create(NetErrorInfo)`；仅当 `HostInfoTracker.IsReconnecting || MenuRejoinFlow.IsRejoining` 时 `__result=null; return false`，空闲一律放行；异常放行不崩。
2. ✅ **重连后状态就绪门闩**（抄 QuickLink 1.5 SolveSender）：`ReconnectRunner.WaitRunReadyAsync`（250ms 轮询 × 20s 超时）+ `IsRunReady` 判定链 `MainMenu==null → CurrentRunNode != null → RunManager.IsInProgress →（战斗中）CombatManager.IsInProgress`；`ReconnectOverlay.SetStatus` 提供文案覆盖。
3. ✅ 构建卫生：csproj 排除 `tools/**`（见 §7）。

**下一轮候选（按优先级）**：
4. **自动准备改走加载屏私有方法**（QuickLink 1.5）：目前我们直接动 `LoadRunLobbyPlayer.IsReady`；改为到齐后反射调 `NMultiplayerLoadGameScreen` 私有无参方法（模拟点击）更耐更新。
5. **CombatHistory 尾随做重连后战斗校验**（DamageMeter）：零 patch 增量拿全部战斗事件，用于重连后比对/补算战斗状态。
6. **`ActionQueueSynchronizer.RequestEnqueue` 代打通道**（SpeedX）：任何"代发操作"（如自动结束回合等待）优先入队原版 action，天然多人同步安全。
7. **消息尾补丁承载私有协议**（RitsuLib Sidecar）：若要做"回退协调/同意收集"信令，给现有 INetMessage 追加字节比抢消息 ID 安全；备选 BaseLib 高位 ID 手法。
8. **MultiplayerCompat 式跨版本成员解析**（JmcModLib）：`Lazy<MemberAccessor?> + FindReadableMember(新名, 旧名...)` 处理网络层改名（我们已有 DispatchProxy 处理监听器，可扩展到 NetService 等）。
9. **`Cmd.Wait` 单点倍速**（SpeedX）：重连后快进掉线期间演出（注意多人强制 1 的保护）。
10. **同步器 ID 快进**（Rewind）：若做重连后补放历史 action 必须处理，否则同步崩。
11. **HarmonyPatchInstaller 逐类安装 + ImpactCatalog**（DanmakuSpire）：我们 patch 已 20+，值得加安装成败记录与兼容报告。
12. **`NetSingleplayerGameService` 会话替换**（Rewind）：本地重建对局再拉人的现成骨架（联机慎用）。

---

## 6. 工程纪律与踩坑（本次新增）

- **全局命名空间类型污染构建（2026-10-22 实锤）**：`tools/sts2sig/Program.cs` 的 `sealed class Engine` 在**全局命名空间**，经 Core csproj 默认 `**/*.cs` glob 混入 `AutoReconnectMinCore.dll`；全局命名空间类型在简单名查找中**击败** `using Godot` 导入的 `Godot.Engine`（命名空间链查找优先于 using 导入）→ 3 个文件 8 处 `Engine.GetMainLoop()` 报 CS0117，而 `typeof(Engine)` 能编译、全限定 `Godot.Engine.GetMainLoop()` 正常——极具迷惑性。**修复：csproj 加 `<Compile Remove="tools\**" />`**。教训：仓库内多项目平铺时，游戏 mod 项目必须显式收窄编译 glob 或排除 tools 目录。
- **字符串混淆 DLL 的逆向方法**：标识符混淆但控制流清晰；关键靠 (a) 调用链上下文推断语义（如 QuickLink 6 个 ReactionMessage Y 值指令）；(b) sts2sig 核实游戏侧 API 全名与字段（`ReactionMessage`/`ReactionType`/`CanonicalizeSave` 均已核实）；(c) 混淆字符串可用 XOR 解密例程还原。
- 皮皮家族 UI 一律**纯代码构建 + 自建 CanvasLayer**（Layer 分层：102 菜单/100 仪表盘/64 弹幕），pck 仅承载 mod 元数据——版本兼容性最好的路线，我们 ReconnectOverlay 同款。

---

## 7. 材料索引

- 详细报告（仓库外 `_modresearch/reports/`）：`libs.md`（三库）/ `solver-modconfig.md`（CombatSolver+ModConfig）/ `quicklink-rewind.md`（联机专题，最高价值）/ `qol-danmaku.md`（SpeedX/DamageMeter/SkadaHelper/DanmakuSpire）
- 克隆仓库：`D:\1Mods\杀戮尖塔2\_modresearch\{BaseLib-StS2, SlayTheSpire2_JmcModLib, STS2-RitsuLib, CombatSolver, ModConfig-StS2}\`
- 待逆向 DLL 副本：`_modresearch/dlls\`（7 个）；反编译产物：`_modresearch/decompiled\{QuickLink 61 个 .cs, Rewind 71 个 .cs\}`
- API 探查三层工作流与 sts2sig 用法见 `.workbuddy/memory/MEMORY.md`。
