# QuickLink & Rewind 学习分析报告（多人联机重连/回退专题）

分析对象：`QuickLink.dll`（皮皮快连 v0.9.13，330KB）与 `Rewind.dll`（皮皮倒带 v0.26.19，531KB）。
两者为同一作者（`sts2.piyixiajiuhenfen.*`）的配套 mod：QuickLink 负责多人联机的「回退+重连」，Rewind 负责单机「回合倒带+地图检查点」。
方法：ilspycmd 11.1 整库反编译 + sts2sig 元数据核对。两 DLL 均做了字符串混淆（`SolverStub.CalcAutomatableEmitter` XOR 解密）与标识符混淆，但控制流未混淆；游戏侧 API 调用均已用 sts2sig 核实。

---

## 一、QuickLink（重点）

### 1.1 入口与架构

- 入口：`[ModInitializer("Initialize")]` → `MainFile.Initialize()`（`QuickLink/MainFile.cs:25`），使用游戏官方 Modding 框架（`MegaCrit.Sts2.Core.Modding`）+ **HarmonyLib `PatchAll`**。
- 同时向 Godot 根节点挂 5 个常驻 Node：`ToastOverlay`、`QuickLinkTransitionOverlay`（过场遮罩）、`QuickLinkSignalRouter`（网络消息路由）、`QuickLinkCombatButton`（战斗内「重置战斗」按钮）、`QuickLinkMapOverlay`（地图「节点回退」按钮）。
- 依赖：HarmonyLib、Godot、Steamworks（`SteamFriends.GetFriendGamePlayed`）、游戏多人层。
- 配置：`EngineService` 通过**反射扫描全 AppDomain 按 FullName 找 3 个外部类型**（疑似通用 ModConfig 框架），再反射注册选项项（数值/布尔/字符串/枚举/回调）。默认值（`UserPolicy` 静态构造）：checkpoint 上限 15、自动重连间隔 4f 等。未反编译确认外部配置框架的程序集名（字符串被混淆）。

### 1.2 Harmony patch 清单（32 个，全部为 Prefix/Postfix，无 Transpiler）

| 目标方法全名 | 类型 | 作用 |
|---|---|---|
| `RunSaveManager.SaveRun(AbstractRoom)` | Prefix | **checkpoint 捕获入口**：preFinishedRoom≠null → `TransformerAuthenticator.PostTransformer`（战斗节点：刷新该房间入口快照）；null → `FormatTransformer`（地图节点：新建快照）。快照来源 `RunManager.Instance.ToSave(...)`，仅存内存 |
| `RunManager.SetUpSavedMultiplayer(RunState, LoadRunLobby)` | Postfix | 动态 TargetMethod；记录存档化多人局建立事件（日志+状态） |
| `NGame.LoadRun(RunState, SerializableRoom)` | Prefix | 记录 LoadRun 调用（回退/重连落点标记） |
| `RunManager.LoadIntoLatestMapCoord(AbstractRoom)` | Prefix | 记录进入最新地图坐标 |
| `RunManager.EnterRoomInternal` / `EnterMapPointInternal` / `CreateRoom` / `ExitCurrentRooms` / `ClearScreens` | Prefix | 房间/地图流转打点，驱动 overlay 按钮显隐 |
| `CombatRoom.Enter(IRunState,bool)` 或 `CombatRoom.EnterInternal`（运行时二选一，`[HarmonyTargetMethod]`） | Prefix | 战斗进入时机打点 |
| `CombatRoom.StartCombat` | Prefix | 战斗开始打点 |
| `CombatManager.AfterCombatRoomLoaded` | Postfix | 战斗房间加载完成打点 |
| `CombatStateSynchronizer.StartSync` / `WaitForSync` / `CheckSyncCompleted` | Prefix/Postfix | 战斗同步状态观测（打点+记录同步器实例） |
| `NMainMenu._Ready` | Postfix | **房主回退流程触发点**：若状态机处于「待回退」→ 启动 `SpecMatcher.ProcessControllableChain`（重建房间） |
| `NMultiplayerLoadGameScreen.OnSubmenuOpened` | Postfix | **自动准备入口**：`ChooserExtractor` 开始轮询 `LoadRunLobby` 玩家到齐情况 |
| `NMultiplayerLoadGameScreen.BeginRun` / `StartRun` | Prefix | 开局打点 |
| `NCustomRunLoadScreen.OnSubmenuOpened` / `NDailyRunLoadScreen.OnSubmenuOpened` | Postfix | 同上（自定义/每日模式） |
| `NErrorPopup.Create(NetErrorInfo)` | Prefix（可取消） | **断线自动重连核心**：见 1.4 |
| `NRun._Ready` / `NRun.SetCurrentRoom` | Postfix | run 场景就绪打点 |
| `NCombatRoom._Ready` / `NCombatRoom.OnCombatSetUp` / `NCombatUi._Ready` | Postfix | 战斗 UI 就绪打点（供回房后状态探测） |
| `NTransition.FadeIn` / `FadeOut` | Prefix | 过场打点 |

### 1.3 消息协议：劫持游戏内置 `ReactionMessage`

QuickLink **没有自定义 INetMessage 类型**，而是劫持游戏的表情消息 `MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor.ReactionMessage`（字段：`ReactionType type` + `Godot.Vector2 normalizedPosition`，sts2sig 已核实）：

- 魔数：`type == (ReactionType)6` 且 `normalizedPosition.X == 2.75f`（±0.05）。
- 指令编码在 `normalizedPosition.Y`（6 个值，见 `AuditorProc.ProcessStaticFormatter`）：

| Y 值 | 枚举 | 方向/语义（据调用链推断） |
|---|---|---|
| 2.75 | m_IterableSingleton | 房主→全员：「开始执行回退，即将重连」 |
| -2.25 | _ParserSingleton | 回退流程第二阶段广播 |
| 1.75 | m_AdvancedSingleton | 房主→队友：「战斗重置请求」（求同意） |
| 0.75 | _SingletonDecryptor | 队友→房主：「同意」 |
| -0.25 | emitterDispatcher | 队友→房主：「拒绝/未就绪」 |
| -1.25 | scalableStub | 取消/中止当前流程 |

- 发送通道（`AuditorProc.ObserveStruct`/`DetachResolver`）：
  1. `INetGameService.SendMessage<ReactionMessage>(msg)` 广播（默认通道）；
  2. 反射取 `NetHostGameService._messageBus` / `NetClientGameService._messageBus`（私有字段），调 `NetMessageBus.SerializeMessage<ReactionMessage>(sender, msg, out msgType)` 拿到裸字节，再走 `NetHost.SendMessageToClient(peerId, bytes, msgType, NetTransferMode)` / `NetClient.SendMessageToHost(bytes, msgType, NetTransferMode)` 定向可靠发送（广播失败时每指令重试 3 次）。
- 接收：`QuickLinkSignalRouter._Process` 每帧检测 `RunManager.Instance.NetService` 变化，重新 `RegisterMessageHandler<ReactionMessage>`（`_ExitTree` 时反注册）；收到后进 `AuditorProc.ProcessVisualStruct` 分派。

### 1.4 断线自动重连（队友侧，ErrorPopupPatch → SpecMatcher.PerformVisibleProc）

1. `NErrorPopup.Create` Prefix 中 `IsReconnectableReason` 白名单判断（6 个 NetError 名，字符串混淆未能确认具体名，行为上覆盖「连接类失败」）；
2. 命中且处于多人局 → `__result = null; return false` **吞掉错误弹窗**，`TaskHelper.RunSafely(SpecMatcher.PerformVisibleProc())`；
3. `PerformVisibleProc`（`SpecMatcher.cs:521`）：
   - 从状态取房主 SteamID（`SymbolicTracker.RecordSymbolicStack`）；
   - `PerformSequentialInterpreter`：5 秒窗口内轮询 `SteamFriends.GetFriendGamePlayed(hostId)` 拿房主当前 lobby SteamID；
   - **重试循环 18 次**：`SteamClientConnectionInitializer.FromLobby(lobbyId)`（探测到）或 `.FromPlayer(hostSteamId)` → `new JoinFlow(...)`（反射构造）→ `joinFlow.Begin(initializer, sceneTree)` → `JoinResult`；
   - 成功判定 `sessionState == 2 && loadJoinResponse.HasValue` → `StartFinalizer`：按 `gameMode` 把 `NMultiplayerLoadGameScreen` / `NCustomRunLoadScreen` / `NDailyRunLoadScreen` 以 `InitializeAsClient(netService, ClientLoadJoinResponseMessage)` 压入 `NMainMenu.SubmenuStack`——即**走游戏原版的「作为客户端加入进行中局」路径**，不自己造 UI；
   - 失败也断开重来，18 次后 toast 引导手动加入。

### 1.5 房主回退 / 队友自动回房 / 自动准备

**房主发起**（地图 overlay 按钮 / 战斗内按钮 → `TransferableEvent.TriggerHiddenTransformer`）：
1. 取最新 checkpoint（`TransformerAuthenticator`，同 1.2 快照）；
2. 从 `RunManager.Instance.RunLobby` 反射取其他玩家 NetId 列表；
3. 只有房主 → 直接执行；有队友 → 发 1.75 指令（战斗重置需队友同意，弹 `QuickLinkStyledPopup`，`ExecutorCalculator.SelectHiddenDic` 三按钮：同意/总是同意/拒绝）；收齐 0.75 回执（`TriggerAutomatableDic` 计数，15 秒超时 `HandleGlobalCollector`）后执行。

**执行回退**（`SpecMatcher.ProcessConnectedManager` + `ProcessControllableChain`）：
1. 广播 2.75/-2.25 指令通知全员（队友 `ProcessScalableTransformer` 显示「即将自动重连」遮罩并记录房主 NetId/新 lobby 标识）；
2. `NGame.Instance.ReturnToMainMenuAfterRun()` 退回主菜单；
3. `MainMenuPatch` 检出「待回退」→ `ProcessControllableChain`：
   - checkpoint `SerializableRun` 经 `JsonSerializationUtility.ToJson/FromJson` **JSON 深拷贝**（`GuideProgram`），保留地图涂鸦（`SerializableMapDrawings`）；
   - `RunManager.CanonicalizeSave(copy, PlatformUtil.GetLocalPlayerId(...))`（sts2sig 已核实该静态方法存在）；
   - 反射 `new NetHostGameService(...)` → `await StartSteamHost(4)`（4 推断为 maxPlayers）**以 checkpoint 重新当房主**；
   - 按 GameMode `InitializeAsHost(netService, run)` 压入加载界面（`ProcessExtendedDriver`）。
4. 队友侧：房主退房导致连接断开 → 各自 ErrorPopupPatch 触发 1.4 自动重连流程加入新房 → 到齐后自动准备（下条）。

**自动准备**（`ChooserExtractor`，`MultiplayerLoadGameScreenPatch.OnSubmenuOpened` 启动）：
- 轮询（45s 上限）反射读取 `NMultiplayerLoadGameScreen` 等私有字段得到 `LoadRunLobby`，比较「已连接玩家数 vs `lobby.Run.Players.Count`」；
- 到齐 → 反射调用加载界面上的**私有无参方法**（`_FunctionDistributor` 等，推断为就绪/开始按钮回调，具体名被混淆未能确认）→ 相当于**模拟点击准备**，而非直接调 `LoadRunLobbyPlayer.SetReady`（全库未见 SetReady 调用）；
- 随后 `SpecMatcher.SolveSender` 轮询（180s 上限）等待：主菜单已清理、`NRun` 就绪、`RunManager.IsInProgress`、`ActionExecutor` 存在且未暂停、（战斗中时）`CombatManager.IsInProgress`、`NCombatRoom`/`NCombatUi`/手牌区/结束回合按钮就绪——**即「回房后状态就绪探测器」**，全部就绪才收遮罩。

### 1.6 checkpoint 机制（与 Rewind 的关键差异）

- 捕获：patch `RunSaveManager.SaveRun`，用 `RunManager.Instance.ToSave(room)` 生成 `SerializableRun`，按 (StartTime, ActIndex, MapCoord, 玩家 NetId 集合) 去重，存内存 `List<TrackerExtractor>`，上限 15（`UserPolicy.LeadWatcher`）；
- **不落盘**（重启即失）；「战斗前/战斗后」两态通过 `preFinishedRoom` 区分；
- 回退 = 整局重 hosting（`StartSteamHost` + `InitializeAsHost`），队友全部重连——**重连后状态完全由 checkpoint save 决定**。

### 1.7 Steam 集成

- 检测房主房间：`SteamFriends.GetFriendGamePlayed(new CSteamID(hostId), out FriendGameInfo_t)` → `m_steamIDLobby`；
- 连接：`SteamClientConnectionInitializer.FromLobby(lobbyId)` / `FromPlayer(steamId)`；
- 建房：`NetHostGameService.StartSteamHost(int)`；
- 无 Steam overlay/邀请发送代码（仅只读查询 + 连接）。

---

## 二、Rewind

### 2.1 入口与 patch 清单（33 个）

入口 `MainFile.Initialize()`：Harmony `PatchAll` + `I18n.Initialize` + `FairModeTracker.Initialize` + `TurnRewindManager.Initialize` + `RunStatsTracker.Initialize`；挂 3 个 Node：`InputHandler`（热键）、`RewindButton`（可拖动「倒带回合」按钮）、`MapCheckpointOverlay`（检查点历史 UI）。

| 目标方法全名 | 类型 | 作用 |
|---|---|---|
| `RunSaveManager.SaveRun(AbstractRoom)` | Prefix | **地图检查点落盘**：`StatelessRecord.RecordConcreteFactory` → `RunManager.ToSave(null)` 序列化为 JSON 写盘 + 更新索引（仅单机，见 2.6） |
| `CombatManager.LoseCombat` | Postfix | 战败前保存「应急后悔点」快照（`PreservePendingLossSnapshot`） |
| `RunManager.OnEnded(bool)` | Postfix | 结算统计快照 |
| `NGameOverScreen._Ready` / `OpenSummaryScreen` / `HideSummary` / `ShowLeaderboard` / `AnimateBadges` | Postfix | 结算界面注入「未使用倒带」徽章与倒带统计（`FilteredTransmitter` 画 NBadge/HoverTip） |
| `NGeneralStatsGrid.LoadStats` | Postfix | 统计网格注入倒带数据 |
| `NPauseMenu.OnSaveAndQuitButtonPressed`（动态解析，推断） | Prefix | **SL 透明化**：标记「合法退出」令牌 |
| `NMainMenu.RefreshButtons`（动态解析，读取 `_readRunSaveResult`，推断） | Prefix | 检测主菜单「继续游戏」→ 合法 continue 令牌 |
| `CardPileCmd.Draw(decimal, Player)` | Postfix | 公平模式：抽牌=新信息 |
| `CardCmd.Preview(...)` ×2 / `CardSelectCmd.FromSimpleGrid` / `CardPileCmd.Add(IEnumerable<CardModel>, CardPile, ...)`（动态重载解析） | Prefix/Postfix | 公平模式：查看牌库/选牌=泄露信息→锁定倒带 |
| `GameActionPlayerChoiceContext.SignalPlayerChoiceBegun/Ended` | Prefix | **回放中自动复现玩家选择** |
| `CardCmd.AutoPlay` / `CardPileCmd.AddDuringManualCardPlay` / `NCard.AnimMultiCardPlay` / `NCardTrail._Process` / `NCardTrailVfx.Create` / `Cmd.CustomScaledWait` / `PlayCardAction.ExecuteAction` / `CardModel.OnPlayWrapper` / `CardModel.PlayPowerCardFlyVfx` / `CardModel.SpendResources` / `PowerCmd.Apply`（动态解析）/ `NCard.OnReturnedFromPool` / `NCard.UpdateTypePlaqueSizeAndPosition` | Prefix/Postfix | **回放视觉抑制**：`TurnRewindManager.IsReplaying` 时跳过动画/拖尾/等待，实现 5-10 倍速快进 |
| `SavedSinglePlayerTrackingPatch`（`RunManager` 上 `(RunState, SerializableRun)` 方法，即 `SetUpSavedSingleplayer`，动态解析） | Prefix | 单机读档统计 |

### 2.2 战斗中倒带：复用游戏自带 `CombatReplay`（核心机制）

快照单位不是对象深拷贝，而是**序列化游戏的战斗回放流**：

- 快照捕获（`OnTurnStarted`，挂在 `CombatManager.TurnStarted`）：
  - 仅玩家回合（`state.CurrentSide == 1`）且非回放中时；
  - 取 `CombatReplayWriter` 的私有回放字段（反射 `ReplayField`），`SerializeReplay` → `byte[] _serializedReplay`；
  - 记录 `TurnCheckpoint(TurnNumber, EventIndex)` 进 `_history`；`CombatReplay` 内含完整 `serializableRun`（`replay.serializableRun`），即**快照自带一份全局存档**。
- 倒带执行（`ExecuteRewindAsync`，`TurnRewindManager.cs:2630`）：
  1. 反序列化快照 → `RunState.FromSerializable(replay.serializableRun)`；
  2. `RunManager.Instance.ActionQueueSet.Reset()` + `RunManager.Instance.CleanUp(true)`；
  3. **`NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService())`** —— 把当前会话替换为单机服务（联机下的关键脱钩点）；
  4. `NGame.Instance.LoadRun(runState, replay.serializableRun.PreFinishedRoom)` 重载战斗前状态；
  5. ID 快进：`ActionQueueSet.FastForwardNextActionId` / `ActionQueueSynchronizer.FastForwardHookId` / `PlayerChoiceSynchronizer.FastForwardChoiceIds`（max(当前, 回放) 合并，保证后续网络同步不断档）；
  6. 逐事件回放 `replay.events[0..targetEventIndex]`：重建 `GameAction`，`ActionQueueSet.EnqueueWithoutSynchronizing(action)`（**绕过同步器直接入队**）；`eventType==4`（玩家选择）事件进 `_pendingReplayChoices` 队列，由 `SignalPlayerChoiceBegun/Ended` patch 自动复现选择（含 NetCombatCardDb 的 id→card 映射反射修复，保证联机牌实例一致）；
  7. 回放完设 `_liveReplayMergeBaseEventCount`，之后每回合 `SerializeMergedReplay` 把新事件**增量合并**回快照（而非重新全量序列化）；
  8. `Engine.TimeScale = 10f` 快进 + 全屏黑幕过渡；失败有回滚（恢复源快照、`_rewindUnsafeAfterFailedRebuild` 熔断）。
- 回合边界：`TurnStarted` 时 `(RoundNumber, replay.events.Count)` 即一个 checkpoint；可倒到历史任意回合（右键按钮出历史列表）。

### 2.3 地图检查点（单机，磁盘）

- 位置：`StatelessRecord.RecordControllableSystem()` 目录（字符串混淆，未确认绝对路径，模式为 `user://` 下 mod 目录+序列号文件名，如 `..._0001.json`）；
- 内容：完整 `SerializableRun` JSON + `CheckpointIndex`（SchemaVersion/RunStartTime/NextSequence/Entries），Entry 含 ActIndex/MapCoord/FloorNumber/MapPointType/HP/Gold/IsActStart；战斗后状态另存 `ResolvedFileName`（`_resolved` 后缀）；
- 恢复（`OpenRecord`）：读 JSON → `RunState.FromSerializable` → `ActionQueueSet.Reset()` → `CleanUp(true)` → `InitializeNetworking(new NetSingleplayerGameService())` → `LoadRun(runState, PreFinishedRoom)` + FadeOut/FadeIn 过渡——**与战斗倒带同一套「单机化重载」骨架**；
- 支持「战斗前/战斗后」两种回退、撤销重做（redo）、一键重开（同角色进阶新种子）。

### 2.4 公平模式（FairModeTracker）

- 开关配置；`RewindAllowed = FairModeEnabled ? !InformationRevealed : true`；
- `InformationRevealed` 由抽牌（`CardPileCmd.Draw`）、查看牌库（`CardCmd.Preview`）、选牌（`CardSelectCmd.FromSimpleGrid`）等 patch 置位——**「已获取新信息则禁止倒带」**，防窥牌后悔；
- 战败后的「应急后悔」单独计数（`LastCombatEndedRewindAllowed`）。

### 2.5 SL 统计与透明化

- `RewindStats`（JSON 持久化）：胜场/连胜/最高连胜、纯净胜场（未倒带）、公平倒带胜场、读档次数、硬读档、疑似外部读档、累计倒带；
- 「合法 continue 令牌」机制：`NPauseMenu.OnSaveAndQuitButtonPressed` / `NMainMenu` 继续游戏时写 `LegitimateContinueToken`（含 run 标识哈希），读档时校验→区分「合法读档」与「硬读档」（外部 SL）；`RunStatsTracker` 在 `RunManager.OnEnded`、结算界面、统计界面持续快照。

### 2.6 联机限制（重要）

- `TransmitterCalc.ResetSchema()` = `NetService.Type == 1`（单机）才允许；`SubmitEditableTask()` 判断是否多人；
- 检查点恢复在联机下直接拒绝并提示「在线联机下已禁用 Rewind 功能」；**回合倒带同样仅单机**（`ExecuteRewindAsync` 强制 `NetSingleplayerGameService`，联机局用会把会话降级，作者选择禁用）。
- 游戏中无任何多人消息收发代码（`Rewind.Networking.FilteredTransmitter` 实为结算徽章绘制，与网络无关）。

---

## 三、可借鉴清单（对我们重连 mod）

1. **「错误弹窗拦截 + 自动重连」模式**（QuickLink ErrorPopupPatch）：Patch `NErrorPopup.Create(NetErrorInfo)` 返回 false 吞掉可重连错误，静默启动重连；白名单判断 `NetErrorInfo.GetReason()`。我们可直接复用该拦截点做「不断线重连提示」。
2. **`JoinFlow.Begin(IClientConnectionInitializer, SceneTree)` 是重连正路**：QuickLink 不自己拼协议，而是 `new JoinFlow()` + `SteamClientConnectionInitializer.FromPlayer/FromLobby` + 最多 18 次重试，成功判据 `sessionState==2 && loadJoinResponse.HasValue`，再走原版 `N*LoadScreen.InitializeAsClient`。我们的重连 mod 应照搬这条链路（含反射构造 JoinFlow 的兼容写法）。
3. **Steam 房主 lobby 探测**：`SteamFriends.GetFriendGamePlayed(new CSteamID(hostSteamId), out var info)` → `info.m_steamIDLobby.m_SteamID`，可在房主掉线后找到其新 lobby；配合 `FromLobby` 重连。
4. **劫持 `ReactionMessage` 做带内信令**：无需自定义 INetMessage，用 `type=(ReactionType)6` + `normalizedPosition=(2.75, Y)` 编码指令，广播 + `NetMessageBus.SerializeMessage<T>` + `NetHost.SendMessageToClient`/`NetClient.SendMessageToHost` 定向可靠发送。我们做「全员回退协调/同意收集」可整套借鉴（注意与 QuickLink 魔数撞车，需换 Y 基线）。
5. **重连后「状态就绪探测器」**（`SpecMatcher.SolveSender`）：按 `NGame.Instance.MainMenu==null → NRun.Instance → RunManager.IsInProgress → ActionExecutor 未暂停 → CombatManager/NCombatRoom/NCombatUi/Hand/EndTurnButton` 顺序轮询，全部就绪才认为回房成功。我们的重连后修复逻辑需要同款就绪门闩。
6. **自动准备 = 反射调用加载屏私有方法**：到齐判定用 `LoadRunLobby` 已连接玩家数 vs `lobby.Run.Players.Count`，然后反射调用 `NMultiplayerLoadGameScreen` 私有无参方法（就绪/开始）。我们不必直接动 `LoadRunLobbyPlayer.IsReady`，走 UI 同款方法更稳。
7. **「回退=重新 hosting checkpoint」模式**：`RunManager.CanonicalizeSave(deepCopy, localPlayerId)` + `new NetHostGameService().StartSteamHost(4)` + `InitializeAsHost`，队友全部重连新房。这是联机回退的完整可行路径（缺点是全员重连、慢）。
8. **`NetSingleplayerGameService` 会话替换技巧**（Rewind）：`NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService())` + `NGame.Instance.LoadRun(...)` 可在不重启游戏的情况下重载任意存档并脱离联机会话。若我们的重连方案需要「本地重建对局再拉人」，这是现成骨架（但联机下用需谨慎，Rewind 因此禁用了联机倒带）。
9. **同步器 ID 快进**（Rewind）：`ActionQueueSet.FastForwardNextActionId` / `ActionQueueSynchronizer.FastForwardHookId` / `PlayerChoiceSynchronizer.FastForwardChoiceIds`——任何「重放历史 action」的场景（含重连后补放）都必须处理 ID 合并，否则后续同步必崩。
10. **快照去重与增量合并**（两者）：QuickLink 按 (StartTime, Act, MapCoord, 玩家集合) 去重避免同节点重复快照；Rewind 的 `SerializeMergedReplay` 只追加新事件。我们做状态快照缓存时可直接借鉴，避免存档膨胀。

## 四、未能反编译确认事项

- QuickLink 的外部配置框架 3 个类型 FullName（字符串混淆）；`UserPolicy` 各选项中文名（ localization 仅 UI 文案）。
- QuickLink 加载屏「就绪」私有方法确切名（`_FunctionDistributor` 等被混淆）；`StartSteamHost(4)` 参数语义（推断 maxPlayers）。
- ErrorPopupPatch 中 6 个「可重连 NetError」确切枚举名。
- ReactionMessage 指令 6 个 Y 值的语义为据调用链推断（代码逻辑确定，命名不确定）。
- Rewind 检查点目录绝对路径、`NPauseMenu.OnSaveAndQuitButtonPressed`/`NMainMenu.RefreshButtons` 动态目标为按签名+用法推断。
- `ReactionType` 枚举值 6 在游戏内的名称（未查枚举定义，仅按值使用）。
