# STS2 QOL/弹幕类 Mod 技术学习分析（SpeedX / DamageMeter / SkadaHelper / DanmakuSpire）

分析对象：`_modresearch/dlls/` 下 4 个 DLL，工具 ilspycmd 11.1.0（DanmakuSpire 有 PDB 但 ilspycmd 11.1 无 `--usepdb`，未使用；4 个 DLL 中 SpeedX/SkadaHelper 有字符串混淆，类名保留）。
目标读者：联机重连 mod 开发。所有结论均来自反编译实证；未能确认处已标注。

---

## 一、SpeedX（皮皮极速 v0.11.12，275KB，Harmony + 混淆）

入口 `SpeedX.MainFile.Initialize()`：`[ModInitializer("Initialize")]` → `new Harmony(id).PatchAll(Assembly)` → `Engine.GetMainLoop().Root.CallDeferred(AddChild, …)` 挂 3 个自研 UI 节点（CenterToastOverlay/TurboOverlay/TurboMenu）。

### 1. 倍速实现（不碰 Engine.time_scale，纯参数缩放）
默认 `SpeedMultiplier=0.1`（10 倍）。三层缩放：
- **逻辑等待**：patch `Cmd.Wait(float,CancellationToken,bool)` Prefix `ref float seconds` → `seconds *= num`；非战斗中 `num=Max(num,0.5)` 保底。这是主倍速来源——游戏所有演出/逻辑延迟都走 `Cmd.Wait`。
- **Spine 动画**：patch `MegaAnimationState.SetTimeScale` 与 `MegaTrackEntry.SetTimeScale` Prefix `ref float scale` → `scale /= multiplier`。
- **VFX 帧推进**：patch `NDamageNumVfx._Process`/`NHealNumVfx._Process`/`NBezierTrail._Process`/`NCardTrail._Process` Prefix `ref double delta` → `delta /= multiplier`。
- **卡牌飞行动画**：`NCardFlyVfx._Ready`/`NCardFlyShuffleVfx._Ready` postfix 用反射写私有 duration 字段；`NCardFlyPowerVfx.GetDurationInternal` postfix 改返回值。
- **顿帧**：`NHitStop.DoHitStop` Prefix 直接 `return false` 跳过。
- 多人保护：`ModularWatcher.CalcProc()` 检查 `NetGameTypeExtensions.IsMultiplayer(RunManager.Instance.NetService.Type)`，多人下倍率强制 1（除非用户开启 UnsafeMultiplayerSpeedEnabled 并打警告日志）。

### 2. 跳过横幅/动画
统一模式：Prefix 返回 `false` + `QueueFree()` 节点 + `__result = Task.CompletedTask`。目标：`NEnemyTurnBanner.Display`、`NPlayerTurnBanner.Display`、`NCombatStartBanner.AnimateVfx`、`NActBanner.AnimateVfx`。

### 3. 自动操作（三种触发范式）
- **自动结束回合（事件订阅 + 动作队列，网络合法）**：`ConfigurablePolicy` 订阅 `CombatManager.Instance.CombatSetUp/TurnStarted/CombatEnded`、`RunManager.Instance.RunStarted`；每帧 `ImplementScheduledPolicy(delta)` 做安全判定（`PlayerCombatState.HasCardsToPlay()`、费用增量、HP margin、药水检查、连斩计数），满足则 `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, …))`；误触时入队 `UndoEndPlayerTurnAction` 撤回。**关键：走游戏官方 ActionQueueSynchronizer，天然适配多人同步。**
- **自动拾取（反射调私有 UI）**：`NRewardsScreen` 奖励初始化方法 postfix → 0.15s `SceneTreeTimer` → 反射取私有按钮列表 → 对 `GoldReward`/有空位的 `PotionReward` 的 `NRewardButton` 用 `GodotObject.Call(方法名)` 触发。
- **自动前进（信号模拟 + 直接调静态方法）**：`NProceedButton.OnEnable` postfix：经 `Traverse` 读 RunManager 当前屏幕上下文，排除奖励屏/特定事件类型后，延迟一帧对按钮 `EmitSignal(Released)`（模拟点击而非调内部方法，兼容性好）；`NRewardsScreen.TryEnableProceedButton` 同理；`NEventRoom.SetOptions` postfix 直接 `TaskHelper.RunSafely(NEventRoom.Proceed())`；另有 RestSiteAutoProceedPatch。
- **自动选目标**：`NTargetManager.StartTargeting` postfix，若全场仅 1 个合法目标，反射写私有 `_hoveredNode` 属性并 `Invoke` 私有 `FinishTargeting(bool)`。
- 另有 QuickPlay 系列 9 个 patch（`NMouseCardPlay.Start`、`NCardPlay.TryPlayCard/CancelPlayCard`、`NPlayerHand._UnhandledInput` 等）实现空格快速出牌。

### 4. F9 配置面板
`TurboMenu : CanvasLayer`（`Layer=102`、`ProcessMode=Always`），`BuildUI()` 纯代码搭建（Panel/VBox/HSeparator/CheckBox/Slider/Button）；`TurboOverlay._Process` 中 `Input.IsKeyPressed(MenuKey)`（默认 `Key.F9=4194340`，`SpeedDirectKey/HitStopDirectKey/AutoEndDirectKey` 可配；空格=QuickPlay）→ `Toggle()`；`_Input` 处理 Escape(4194305) 关闭并 `SetInputAsHandled()`。配置经 `ReadableAuditor`/`CustomizableManager` 以 `System.Text.Json` 序列化、Godot `FileAccess` 持久化；内置 I18n（zhs/en JSON）。

---

## 二、DamageMeter（皮皮统计 Skada v1.14.7，501KB，纯公开 API，未混淆）

入口日志自述 "pure public API, cross-platform"——**全库只有 1 个 Harmony patch**。

### 1. 伤害/格挡数据捕获：CombatHistory 尾随（零 hook）
核心类 `HistoryTailer`：`PublicApiLifecycle.OnCombatSetUp` 时 `_tailer.Attach(CombatManager.Instance.History, state)`：
- 订阅 `CombatHistory.Changed` 事件 + 对每个敌人订阅 `Creature.Died`；
- `Drain()` 按 `_lastSeenIndex` 增量遍历 `_history.Entries`，分流 11 种公开条目类型：`DamageReceivedEntry`（Dealer/Receiver/DamageResult/CardSource）、`BlockGainedEntry`、`CardPlayFinishedEntry`、`CardDrawnEntry`、`CardDiscardedEntry`、`CardExhaustedEntry`、`OrbChanneledEntry`、`PotionUsedEntry`、`PowerReceivedEntry`（Owner 为空时延迟一轮）、`StarsModifiedEntry`、`SummonedEntry`；
- 归入 `CombatDataCollector.PlayerStats`（DamageDealt/DamageTaken/BlockedByTarget/OverkillDealt/HitCount/DamagePerTurn/DamageByCard/DamageBySource/TotalBlockGained/EnergySpent…/AssistDamage+AssistDamageByPower/DamagePrevented/DamageBoostedByOthers+DamageBoostedByPower 等约 40 字段）。
- `CombatDataCollector.RecordDamage(CombatState, dealer, receiver, DamageResult, CardModel?)` 直接读 `DamageResult.UnblockedDamage/BlockedDamage/OverkillDamage/WasTargetKilled`。
- `DamageReceivedEntry.Dealer==null` 时用 `GameCompat.GetHistoryEntryCurrentSide` +  PoisonPower/StranglePower/HauntPower 推断来源（DoT/附伤归属）。

### 2. rDPS 归因与仪表盘
`RdpsCategory`（实现 `IStatCategory.GetPlayerBars/GetDetailBars`）：
`rDPS = DamageDealt − DamageBoostedByOthers + AssistDamage`，`rDPS/t = rDPS / CurrentTurn`，占比 = 个人/全队。共 19 个统计类别（Dpt/Overkill/DamagePrevented/CardEfficiency…）同插件式接口。
UI：`DamageMeterUI.CreateAndAttach()`（战斗建立时）为每个窗口建独立 `CanvasLayer`（Active=100 / 被原生对话框遮挡时 Blocked=-10 并 0.4 透明度），窗口可拖拽/缩放/多开/compact；`InputHandler` Node 挂 root，`_Process` 轮询 `Input.IsPhysicalKeyPressed(ToggleKey)`（默认 `Key.F7=4194338`）；tooltip 用 `TooltipPanel`。
复盘：`CombatSegment` 分段存档（`ArchiveAndStartNew`），`SaveCombatState/RestoreCombatState` 支持 SL 回档恢复（比对 `GetRunSeedNumeric`）；`RunDataCollector`+`AnalyticsSyncer` 上传战斗摘要到 `http://124.223.63.165`；`IntegrityDetector` 检测 continue/hard reload。

### 3. 唯一的 patch 与跨 mod 探测（对我们有用）
- `NGameOverScreen._Ready` postfix：向死亡界面注入 `RichTextLabel` 分析链接（`GetNodeOrNull("%MainMenuButton")` 定位锚点 + 复用其 Theme）。
- 枚举其他 mod：反射 `ModManager.GetLoadedMods()`（Static|Public）→ 读每项 `manifest.id/version`（`RunDataCollector.EnumerateLoadedMods`）。
- 跨 mod 能力探测：`Type.GetType("Rewind.Scripts.TurnRewindManager, Rewind")` + 静态属性 `HasGameOverRewind`。

---

## 三、SkadaHelper（皮皮军师 v0.22.2，6.6MB，Harmony + 混淆）

### 1. 与 DamageMeter 的分工
无代码互依赖（同作者、同 ModId 前缀 `sts2.piyixiajiuhenfen.*`）。DamageMeter 管战斗数据记录；SkadaHelper 管**牌组/路线建议**：16 个 patch 全部是 UI postfix，给原生界面注入建议面板——`NDeckViewScreen._Ready/_ExitTree/OnPileContentsChanged`（牌组体检）、`NCardRewardSelectionScreen.RefreshOptions`/`NChooseACardSelectionScreen._Ready`/`NDeckCardSelectScreen._Ready`/`NDeckUpgradeSelectScreen._Ready`（选牌/删牌/升级）、`NMerchantInventory.Open/OnPurchaseCompleted`/`NMerchantCard.UpdateVisual`（商店）、`NEventLayout.AddOptions`（事件）、`NMapScreen.Open`（路线）、`NCombatUi.Activate`+`NHoverTipSet.CreateAndShow`+`NGridCardHolder.OnFreedToPool`（抽牌堆预览/悬停）。私有字段一律 `AccessTools.Field` + 混淆名字符串取。

### 2. 体积大头
PE 的 `.text` 节 6.65MB（≈整个 DLL）。反编译后体积集中在**硬编码数据表**：`IroncladCardCatalog.cs` 311KB（约 97 条卡牌规则记录，每条带评分 vars）、`DeckAdviceEngine.cs` 54KB、`EventStatistics.cs` 30KB、`PairAssociationHints.cs` 43KB 等（5 职业 × 进阶路线 + 事件选项通关统计）；另有混淆器注入的大量基础设施类（ErrorHandling/Management/Collections/Wrapping/Sorting/Chains/Specifications/Threading）。数据更新：`DataProvider` 读 `ReleaseInfoManager.Instance.ReleaseInfo.Version` 识别游戏版本，本地缓存与 `HttpClient`（GZip/Deflate、20s 超时、`async void`）拉取的远程 `BundleData` JSON 按 GeneratedAt 合并。

### 3. pck 与 UI 技术
`SkadaHelper.pck`（285KB）仅含 `SkadaHelper/mod_image.png`（mod 预览图）+ `godot/global_script_class_cache.cfg`。结论：**UI 完全代码构建**（C# Godot Node/Control，`[ScriptPath]` 脚本类），无场景/主题资源；pck 只是 mod 元数据载体。与 SpeedX 同一技术路线。

---

## 四、DanmakuSpire（弹幕尖塔，13MB，820 类型，未混淆）

### 1. 57 条行为规则的监听机制：统一事实总线（非逐点裸逻辑）
三层架构：
- **事实生产（patch 层）**：~33 个嵌套 patch 类只做一件事——把游戏事件包装成 `IGameFact` 记录 `ModRunState.Publish(new XxxFact(new GameFactHeader(EventId, GameFlowState, EmissionScope, RoomSequence, FactBoundary), …))`。目标含逻辑层（`CombatHistory.CardPlayStarted/CreatureAttacked/MonsterPerformedMove/CardDrawn/PotionUsed/DamageReceived`、`CardPile.AddInternal/RemoveInternal`、`Player.AddRelicInternal`、`CardCmd.Enchant/Upgrade`、`Creature.InvokeDiedEvent`、`Hook.AfterAutoPrePlayPhaseEntered`、`RunManager.InitializeSavedRun/OnEnded/CleanUp`）与 UI 层（`NCardRewardSelectionScreen.AfterOverlayOpened/OptionSelected/AfterOverlayClosed`、`NMerchantInventory.Open/Close`、`MerchantEntry.OnTryPurchaseWrapper`、`NEventRoom.SetupLayout`、`EventOption.Chosen`、`RestSiteSynchronizer.ChooseLocalOption`、`NCharacterSelectScreen.*`）。
- **总线**：`GameFactBus`（`Subscribe<TFact>(Action<TFact>)` / `Publish<TFact>(fact, lifecycle)`）：按 FactType+EventId 去重、`LifecyclePolicy` 防跨回合过期、2000 条审计环、单 handler 异常不传染。
- **规则消费**：`RuleFactRegistry.Build()` 是「事实↔规则↔阶段↔上下文」绑定表（57 个 `DanmakuTrigger`，含 Meta/Retired/Ready/Unimplemented 状态机）；`RuleConsumerBootstrap` 用泛型 `RuleFactConsumer<TFact>(bus, trigger, phase, handler, requireLocalPlayer)` 批量订阅，`RuleConsumerGroup` 统一 Dispose；`PhaseRouter/PoolManager.IsPhaseActive(TimingPhase)` 做阶段门控。
- patch 安装不用 PatchAll：`HarmonyPatchInstaller.Install` 逐类 `CreateClassProcessor().Patch()`，支持 `TargetMethods()` 多目标，并用 `HarmonyPatchImpactCatalog`（patch→产出事实→影响规则 ID）记录安装成败、写兼容性报告。

### 2. 多人弹幕互通协议（复用游戏网络通道）
`DanmakuNetworkForwarder : INetForwarder`，单例，经 `RunManager.Instance.NetService`（`INetGameService`）：
- 收发：`service.RegisterMessageHandler<T>(MessageHandlerDelegate<T>)` / `UnregisterMessageHandler<T>` / `SendMessage<T>`；5 个消息类均实现 `INetMessage, IPacketSerializable`：`ZzzDanmakuNetMessage`（规则触发，Trigger/EntryIndex/Variant/Scope/ContextRefs≤16）、`ZzzDanmakuHandshakeMessage`（ModVersion 协商）、`ZzzDanmakuChatMessage`、`ZzzDanmakuTextMessage`、`ZzzDanmakuUgcMessage`。
- 传输语义：`ShouldBroadcast=true`；`NetTransferMode Mode`：规则/聊天/文本=1（不可靠即时、`ShouldBuffer=false`），握手=2（可靠、`ShouldBuffer=true`，晚到者也能收到）。
- 序列化：`PacketWriter.WriteInt(bits)/WriteString`、`PacketReader` 对称读；防御性设计——`ReadSafeString` 限 512、`Corrupt` 标志、条数上限校验、`NetworkContextRefs.TryNormalize`。
- 对端管理：`DanmakuMultiplayerState`（RecordPeer/IsCompatiblePeer/ResolvePlayerName/LocalPlayerNetId）；无兼容对端时 `RetryHandshakeIfNeeded`（5s 节拍重发握手）。
- 健壮性：限速（单发送者 40 条/s 全局 + 分类 5/10/20/30）+ `CorrelationId` 去重（4096 容量的有序集合）；`RunStarted` 时 EnsureAttached、`RunEnded` 时 Detach 并 ResetPeers。

### 3. 弹幕渲染（Godot 节点注入 + 自建 CanvasLayer）
`RollingDanmakuScheduler : Node`（`[ScriptPath]`），`EnsureCreated()` 后 `Root.CallDeferred(AddChild, …)` 挂场景树；自建 `CanvasLayer{ Layer=64, Name="RollingDanmakuCanvas" }` 作 overlay；`RollingDanmakuSpawner.Spawn` 在 overlay 下创建 `Control`（RollingVisual，Label 系）。`_Process` 驱动滚动：16 车道、待发 256、每帧最多生成 2 条、最小水平间距 82px、顶部固定 4.6s；支持悬停暂停/全局暂停/点击交互（点赞）。弹幕文本来自 `DanmakuLibrary`（`rules.json`）+ 模板变量解析（`entry.ResolveAll(context)`）+ 强度/数量配置。

### 4. 内容与其他 mod 扫描机制
- **不反射遍历其他 mod 程序集**：内容是 JSON 数据文件三层加载——Baseline（mod 目录 `content/catalogs/{characters,aliases,names-zh,collectible-sets}.json` + `content/danmaku/rules.json`）→ Ugc（远程 `https://dl.danmakuspire.top/version.json` + 分片，`UgcCacheStore` 缓存 + 可信 manifest 校验，ZIP 导入上限 16MB）→ Overlay（`user://DanmakuSpire/content/` 用户层）。
- **扫描游戏对象而非 mod**：`CardScanner` 反射 `CardModel.HasEnergyCostX`（NonPublic 属性）+ `ObjectScanner.ComputeFingerprint()` 指纹缓存，对游戏卡牌 DB 分类（攻击/格挡/抽牌/AOE/多段/X 费）。
- 身份：`OS.GetUniqueId()` + `user://DanmakuSpire/owner-secret-v2` 持久化密钥（DeviceIdentity）。
- 对照：DamageMeter 的 `ModManager.GetLoadedMods` 反射才是「枚举其他 mod」的正确姿势。

---

## 五、Patch/监听点总表

| 目标全名 | mod | 作用 |
|---|---|---|
| `MegaCrit.Sts2.Core.Commands.Cmd.Wait(float,CancellationToken,bool)` | SpeedX | 倍速主入口：缩放所有逻辑等待时长 |
| `MegaAnimationState.SetTimeScale` / `MegaTrackEntry.SetTimeScale` | SpeedX | Spine 动画倍速 |
| `NDamageNumVfx._Process`/`NHealNumVfx._Process`/`NBezierTrail._Process`/`NCardTrail._Process` | SpeedX | VFX delta 缩放 |
| `NHitStop.DoHitStop` | SpeedX | 跳过顿帧 |
| `NCardFlyVfx._Ready`/`NCardFlyShuffleVfx._Ready`/`NCardFlyPowerVfx.GetDurationInternal` | SpeedX | 卡牌飞行/洗牌动画加速（反射写私有字段） |
| `NEnemyTurnBanner.Display`/`NPlayerTurnBanner.Display` | SpeedX | 跳过回合横幅（返回 false+QueueFree+CompletedTask） |
| `NCombatStartBanner.AnimateVfx`/`NActBanner.AnimateVfx` | SpeedX | 跳过战斗开始/章节横幅 |
| `NProceedButton.OnEnable`/`NRewardsScreen.TryEnableProceedButton` | SpeedX | 自动前进（延迟 EmitSignal(Released)） |
| `NEventRoom.SetOptions`/`RestSite*` | SpeedX | 事件/休息处自动离开 |
| `NRewardsScreen.<Init>(IEnumerable<Reward>)` | SpeedX | 自动拾取（反射按钮列表+Call） |
| `NTargetManager.StartTargeting` | SpeedX | 单选目标自动确认（反射 FinishTargeting） |
| `NMouseCardPlay.Start`/`NCardPlay.TryPlayCard`/`NPlayerHand._UnhandledInput` 等 9 处 | SpeedX | 空格快速出牌 |
| `CombatHistory.Changed`（事件订阅，非 patch） | DamageMeter | 战斗数据唯一来源：增量尾随 11 种 entry |
| `CombatManager.CombatSetUp/TurnStarted/CombatEnded`（事件订阅） | DamageMeter/SpeedX | 生命周期挂载（UI 建立/数据起止） |
| `NGameOverScreen._Ready` | DamageMeter | 死亡界面注入分析链接（全库唯一 patch） |
| `NDeckViewScreen._Ready`/`OnPileContentsChanged`/`_ExitTree` 等 16 处 | SkadaHelper | 牌组/选牌/商店/事件/地图建议面板注入 |
| `CombatHistory.DamageReceived/CardPlayStarted/CreatureAttacked/MonsterPerformedMove/CardDrawn/PotionUsed` | DanmakuSpire | 产出战斗类 Fact |
| `CardPile.AddInternal/RemoveInternal`、`Player.AddRelicInternal`、`CardCmd.Enchant/Upgrade` | DanmakuSpire | 产出收集/附魔 Fact |
| `Creature.InvokeDiedEvent`、`Hook.AfterAutoPrePlayPhaseEntered` | DanmakuSpire | 死亡确认/抽牌质量 Fact |
| `RunManager.InitializeSavedRun/OnEnded/CleanUp` | DanmakuSpire | 跑局生命周期 Fact（含读档） |
| `NCardRewardSelectionScreen.*`、`NMerchantInventory.Open/Close`、`MerchantEntry.OnTryPurchaseWrapper`、`NEventRoom.SetupLayout`、`RestSiteSynchronizer.ChooseLocalOption`、`NCharacterSelectScreen.*` | DanmakuSpire | 非战斗场景 Fact |

---

## 六、可借鉴清单（面向联机重连 mod）

1. **`Cmd.Wait` 单点倍速**（SpeedX）：改一个 `ref float seconds` 即全局变速，无需逐对象 speed_scale。适合重连后「快进掉线期间演出」。
2. **横幅/弹窗「Prefix 返 false + QueueFree + Task.CompletedTask」三连**（SpeedX）：对任何返回 Task 的 UI 展示方法通用，重连时可批量清场。
3. **`ActionQueueSynchronizer.RequestEnqueue(GameAction)` 驱动自动操作**（SpeedX 自动结束回合）：这是唯一对多人同步安全的「代打」通道；我们的自动重连/恢复逻辑应优先入队而非直接调业务方法。`EndPlayerTurnAction`/`UndoEndPlayerTurnAction` 可直接参考。
4. **CombatHistory 尾随法**（DamageMeter）：订阅 `CombatHistory.Changed` + 索引增量，零 patch 拿到全部战斗事件——重连后校验/补算战斗状态可用，且不与其他 mod 的 patch 冲突。
5. **IStatCategory 插件式统计 + `GameFactBus` 类型化总线**（DamageMeter/DanmakuSpire）：`Subscribe<TFact>/Publish` + EventId 去重 + 生命周期防过期，是「断线期间事件补偿」的天然骨架（重连后按 EventId 续订）。
6. **INetMessage/IPacketSerializable 自定义消息协议**（DanmakuSpire）：经 `INetGameService.RegisterMessageHandler/SendMessage` 收发；握手消息用 `Mode=可靠+ShouldBuffer`、瞬时消息用不可靠；`Corrupt` 标志 + 长度上限 + CorrelationId 去重 + 分类限速——重连握手机制可直接照搬。
7. **`HarmonyPatchInstaller` 逐类安装 + ImpactCatalog 元数据 + 兼容报告**（DanmakuSpire）：游戏版本升级导致 target 缺失时不崩溃、可诊断；我们 patch 多时值得复刻。
8. **自建 CanvasLayer overlay + 纯代码 UI**（SpeedX/SkadaHelper/DanmakuSpire）：`Engine.GetMainLoop().Root.CallDeferred(AddChild, node)` + `CanvasLayer.Layer` 分层（102 菜单/64 弹幕/100 仪表盘），`Input.IsKeyPressed`/`_Process` 轮询热键；不依赖游戏场景文件，版本兼容性最好。
9. **`ModManager.GetLoadedMods` 反射枚举 + `Type.GetType("Ns.Type, Assembly")` 能力探测**（DamageMeter）：检测冲突 mod（如 Rewind）是否存在，供重连 mod 做兼容判断。
10. **EmitSignal 模拟点击代替直接调私有方法**（SpeedX 自动前进）：对 `NProceedButton` 这类 Control，`EmitSignal(Released)` 比反射调方法更耐更新。

---

## 七、未能确认事项
- SpeedX/TimerSpeedPatch.cs 无 HarmonyPatch 特性且 Prefix 为空，疑为遗留死代码（未确认其意图）。
- SkadaHelper 远程 BundleData 的确切 URL：字符串被混淆，仅确认 `HttpClient`+GZip/Deflate+20s 超时，**URL 未能反编译确认**。
- DanmakuSpire 的 PDB 未使用（ilspycmd 11.1 不支持 `--usepdb`），局部变量名不可用；不影响结构性结论。
- DamageMeter 统计上传端点 `http://124.223.63.165` 为硬编码明文（GameOverScreenPatch 内常量），仅作文档记录。
