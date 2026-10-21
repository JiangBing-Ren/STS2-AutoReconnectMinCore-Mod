# CombatSolver × ModConfig-StS2 技术分析报告

分析对象（只读）：
- `_modresearch/CombatSolver`（MIT，v0.48.0，战斗路线求解器，依赖 RitsuLib ≥0.6.0，游戏 0.111.0）
- `_modresearch/ModConfig-StS2`（MIT，v0.2.2，通用 mod 配置框架，"zero Harmony"）

下文路径均相对各仓库根目录；行号为提交时快照。

---

## 一、CombatSolver

### 1.1 整体架构

- 入口：`src/Runtime/Entry.cs:16`，`[ModInitializer(nameof(Initialize))]`；`Initialize()` 内完成：日志目录 `OS.GetUserDataDir()/logs/CombatSolver`（Entry.cs:43）、设置加载、GC 启动配置、生命周期订阅、批量注册约 40 个 patch。
- RitsuLib 集成方式（Entry.cs:57-109）：
  - 事件订阅：`RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(...)` / `<CombatEndedEvent>` / `<ModelRegistryInitializedEvent>`（Entry.cs:58-64），另有 `CombatManager.Instance.TurnStarted += OnTurnStarted`（Entry.cs:65）。
  - Patch：`RitsuLibFramework.CreatePatcher(ModId, "combat-solver", "战斗路线求解器")`（Entry.cs:67）→ `patcher.RegisterPatch<T>()` × N → `ApplyRequiredPatcher(patcher, DisableMod)`（Entry.cs:109）。patch 类实现 `IPatchMethod`，用 `GetTargets()` 声明 `ModPatchTarget`，方法带 `[HarmonyPriority(Priority.First)]`。
- 命名空间：`CombatSolver`（Runtime/Search/UI/Api/Diagnostics）、`CombatSolver.Engine.Common`、`CombatSolver.Engine.InCombat.Simulation`、`CombatSolver.Engine.InCombat.Mirrors`、`CombatSolver.Prediction`、`CombatSolver.Replay`、`CombatSolver.Strategy`、`CombatSolver.Testing`。
- 运行链（docs/ARCHITECTURE.md:7）：主线程捕获稳定战斗根 → 冻结请求政策 → 后台分叉搜索 → 主线程收结果 → 原版公开入口部署 → `ContinuationStamp` 核对或重新捕获。核心 Runtime 类：`SolverController`（编排）、`SolverControllerSessions`、`CombatRootSnapshot`（根捕获）、`ContinuationStamp`（跨回合一致）、`SearchGcPolicy`、`SearchMemoryPressureSignal`、`NativeChoiceRuntime`、`PlayerTurnSetupPatches`。

### 1.2 战斗模拟核心（不改游戏 RNG 的确定性模拟）

- **RNG**：`src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs:11`。从 `RunRngSet` 一次性 `CaptureState()` 捕获 9 条原生流（shuffle/cardGeneration/potionGeneration/cardSelection/energyCosts/targets/orbGeneration/monsterAi/niche，CombatPredictionRngSet.cs:73-84）；每条流是 `FrozenStream`（不可变快照）+ 懒物化 `Rng _mutable ??= State.ToRng()`（CombatPredictionRngSet.cs:25）——只有分支真正消费随机数时才在**私有 Rng 实例**上重放，游戏 RNG 永不前进。`Fork()` 对已物化流重新 `CaptureState()`（CombatPredictionRngSet.cs:31-33）。
- **牌堆**：`src/Engine/Common/SimCardPile.cs:6`，`List<PredictedCard>` + 三组双 u64 指纹缓存（有序/无序/循环形态，SimCardPile.cs:9-17），增删即失效（SimCardPile.cs:156）；`Fork(context)` 逐卡 fork 并在同一上下文登记（SimCardPile.cs:89-106）。
- **Fork/引用重映射**：`src/Engine/Common/PredictionForking.cs:398` `PredictionForkContext`。`ArrayPool` 租借 + 开放寻址哈希索引（PredictionForking.cs:530-555），`Register/RemapOrSelf/RequireRemap`（PredictionForking.cs:430-492）；同一对象被 fork 两次即抛错（PredictionForking.cs:440）；`[ThreadStatic] _entryCountHint` 用上次条目数预分配容量（PredictionForking.cs:409-425）。COW 卡牌：`src/Engine/Common/PredictedCard.cs:11` 用 `ConditionalWeakTable<CardModel, object>` 维持 Original/Preview 身份，写入必须取 `MutablePreview`。
- **Mirror（原版语义复刻）**：`src/Engine/InCombat/Mirrors/HookMirrors.cs:29` 是与原版 `Hook` 平行的 facade——调用方传普通 hook 参数，内部构造 mirror context、按 `MirroredHookMask` 位图过滤监听者并保持原生监听顺序（HookMirrors.cs:53-61）。`docs/ARCHITECTURE.md` 与 AGENTS.md §6 硬约束"同一战斗语义只能有一个权威实现"。
- **History**：`src/Engine/InCombat/Simulation/CombatPredictionHistory.cs:14`，只记模拟事件不碰 live history；分段链表 + 可变尾表 + 风险签名（CombatPredictionHistory.cs:64-79）。
- **状态等价/续用**：搜索状态键为双 u64 指纹（Entry.cs:116 启动日志 `search_state_key=dual_u64`）；跨回合复用不走哈希而走**逐字段文本全等**：`src/Runtime/ContinuationStamp.cs:21` `ContinuationStamp(string StateText)`，`DescribeDifferences` 输出首个差异字段用于诊断（ContinuationStamp.cs:26-56）。
- **根捕获**：`src/Runtime/CombatRootSnapshot.cs:23`，主线程一次性冻结玩家/敌人/药水/遗物/牌组/成长上界/hook 监听计数等；AGENTS.md §5 列举"必须随分支变化的值"清单（HP/格挡/能量/有序牌堆/Power 数量/怪物下一行动/九条 RNG 等）。

### 1.3 后台搜索

- **跑法**：`src/Runtime/SolverController.cs:1280` `Task.Run`（线程池），worker 内把线程优先级降为 `BelowNormal`（SolverController.cs:1292）；结果经 `SolverDispatcher.Post` 回主线程（SolverController.cs:1339-1342）。
- **并行**：`src/Search/CombatBeamSolver.ParallelExpansion.cs:131` `ParallelExpansionExecutor`，每个 lane 是**专用后台线程**（CombatBeamSolver.ParallelExpansion.cs:284 `new Thread`，`IsBackground=true`，`ThreadPriority.BelowNormal`，CombatBeamSolver.ParallelExpansion.cs:340），`AutoResetEvent` 派发作业；父节点 fork 加锁串行化，worker 只消费私有 fork（CombatBeamSolver.ParallelExpansion.cs:64-68）；**提交按父节点原序回到协调线程**（CombatBeamSolver.ParallelExpansion.cs:156-172），保证确定性。并行度 1–16，受逻辑核数与内存准入限制（README:132）。
- **让出 CPU**：`src/Search/SearchWorkPacer.cs:6`，每 16 次检查、连续占用满 4ms 即 `Thread.Yield()`（`SolverWeights.cs:135-136`：`BackgroundWorkSliceMilliseconds=4`、`BackgroundYieldCheckInterval=16`）；同时观测 GC 暂停（SearchWorkPacer.cs:57-72）。
- **帧压感知**：`src/Search/SearchFramePressureSignal.cs:3`，主线程用 31 帧空闲间隔中位数估基线，压力阈值 = max(33ms, 基线×1.5)（SearchFramePressureSignal.cs:5-9, 35-45）；worker 检测到压力纪元变化即 `SpinWait.SpinUntil` 等下一帧（上限 25ms）后重启时间片（SearchFramePressureSignal.cs:66-86, SearchWorkPacer.cs:28-33）。
- **预算**：`src/Search/CombatBeamSolver.Phases.cs`，总时间/节点预算按回合层切分：`turnLayerBudgetMs = Math.Max(250, remainingSearchMs / remainingReservedLayers)`（CombatBeamSolver.Phases.cs:1505）、回合层节点预算（CombatBeamSolver.Phases.cs:1520）； anytime 搜索持续更新当前最佳路线，满足战损/成长/用药条件可提前结束（README:119-120）。内存准入 `src/Search/SearchWaveMemoryPolicy.cs:44`（每并行度限制排队父节点数）。

### 1.4 自动执行

- **拦截回合准备**：patch 私有方法 `CombatManager.SetupPlayerTurn` 与 `RunAutoPrePlayPhase`（`src/Runtime/PlayerTurnSetupPatches.cs:20-95`，目标方法用反射 `MethodInfo` 预取，PlayerTurnSetupPatches.cs:164-180），`Prefix` 返回 false 并回填 `__result` Task，把原生流程替换为"搜索→执行"管线。
- **执行=调原生 API，不模拟输入**（`src/Runtime/SolverController.cs`）：
  - 打牌：`card.TryManualPlay(target)`，入队后捕获对应 `PlayCardAction` 并 await 完成（SolverController.cs:3005-3013）。
  - 药水：`potion.EnqueueManualUse(target)`，匹配 `UsePotionAction`（SolverController.cs:2970-2976）。
  - 结束回合：`CombatManager.Instance.OnEndedTurnLocally()` + `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn))`（SolverController.cs:3135-3136）。
  - 动作间 `await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame)` 让动画播放，间隔可配（SolverController.cs:3057-3065）。
- **漂移即重搜**：药水丢失/不符、卡牌不可打、计划耗尽均记 `DEPLOY_REPLAN` 并请求新搜索（SolverController.cs:2944-3003, 3070-3082）；结束回合前用实机状态做一次 end-turn 风险复核，劣化即停全自动（SolverController.cs:3089-3118）。选牌页面用 `NativeChoiceRuntime` 观察原生界面并按计划逐项匹配（`src/Runtime/NativeChoiceRuntime.cs:84`）。

### 1.5 性能与稳定

- **NoGC 区域**：`src/Runtime/SearchGcPolicy.cs:349` `EnterSearchScope(...)`；启用时走 `GC.TryStartNoGCRegion`，LOH 预算 = max(256MB, 总预算/6)（SearchGcPolicy.cs:379-381），默认独立 16GB 区域预算（README:128）；不支持/关闭时回退 CLR 常规分代 GC（SearchGcPolicy.cs:376）；预算变更触发区域重建（SearchGcPolicy.cs:433-444）；每次搜索串行化进程级区域，lane 间仍并行（SearchGcPolicy.cs:420-426 注释）。
- **MemoryCleaner.exe**：`tools/runtime/CombatSolver.MemoryCleaner/Program.cs`。启用 `SeProfileSingleProcessPrivilege`（Program.cs:27）后调用 `NtSetSystemInformation(SystemMemoryListInformation=80)`，依次执行 `MemoryEmptyWorkingSets(2)` 与 `MemoryPurgeStandbyList(4)`（Program.cs:30-33, 76-84）——即清空所有进程工作集+清空备用列表，缓解 NoGC 大预算下的系统换页。由 `src/Runtime/SystemMemoryReleaseService.cs:39-45` 以 `UseShellExecute + Verb="runas"` 提权启动，用户取消 UAC 转 `OperationCanceledException`（SystemMemoryReleaseService.cs:52-55）。
- **问题上报**：`src/Runtime/CombatBugReportUploader.cs:24`。HTTPS POST 固定端点，**证书 SHA-256 固定引脚 + FixedTimeEquals 校验**（CombatBugReportUploader.cs:39-48），禁代理/禁重定向；包 ≤128MB（CombatBugReportUploader.cs:26）；按战斗内容自动分类（`CombatBugReportUploadPolicy`/`CombatBugReportDescription`），也可导出桌面手动提交。另有 30s 一次的匿名在线统计（`src/Runtime/OnlinePresence.cs:17`，可关）。
- 社区回归设施：`tools/search/OfflineSearchHarness` 离线跑搜索只产指标；无人测试 `tools/testing/run-unattended-test.*` 起隔离 `--headless` 游戏实例（AGENTS.md §8）。

---

## 二、ModConfig-StS2

### 2.1 设置页注入（零 Harmony）

- 入口：`MainFile.cs:13` `Initialize()` → `I18n.Initialize / ModConfigManager.Initialize / SettingsTabInjector.Initialize`；日志明示 "zero Harmony, cross-platform"（MainFile.cs:19）。
- 注入手法（`Scripts/SettingsTabInjector.cs`）：
  1. 订阅 `SceneTree.NodeAdded`（SettingsTabInjector.cs:57-61），发现 `NSettingsTabManager` 后一次性连 `ready`（SettingsTabInjector.cs:63-74）。
  2. 反射私有字段 `_tabs`（`IDictionary`，NSettingsTab→NSettingsPanel）（SettingsTabInjector.cs:80-92）。
  3. **克隆**第一个 tab 与第一个 panel：`firstTab.Duplicate()` / `firstPanel.Duplicate()`（SettingsTabInjector.cs:110, 126）；panel 的子节点在 `AddChild` **之前**清理，避免游戏内部节点 `_Ready()` 引用原面板控件抛 `ObjectDisposedException`（SettingsTabInjector.cs:123-156 注释即此）。
  4. 登记进 `_tabs` 字典（SettingsTabInjector.cs:175）；点击回调用 `tabManager.Call("SwitchTabTo", modsTab)` 反射调用（SettingsTabInjector.cs:178-184）。
  5. 面板限高：覆盖 `RefreshSize` 自动膨胀——`viewport.SizeChanged` 里 `CallDeferred("set_size", ...)` 钉住高度，让 ScrollContainer 能滚（SettingsTabInjector.cs:194-213, 354-362）。
  6. 内容包一层 `ScrollContainer`（`FollowFocus=true`）（SettingsTabInjector.cs:529-534）。
  7. 焦点链重建：收集 `FocusMode=All` 控件，串 `FocusNeighborTop/Bottom/Left/Right`，并反射写 `NSettingsPanel._firstControl`（SettingsTabInjector.cs:419-463）——键盘/手柄导航靠这个。
- 多实例：主菜单与暂停菜单各一份，用 `WeakReference` 列表跟踪，语言切换/晚注册时 `RefreshUI()` 全量重建（SettingsTabInjector.cs:18-19, 272-293）。

### 2.2 零依赖注册与持久化

- 公开 API：`Scripts/ModConfigApi.cs:127` `ModConfigApi`：`Register(modId, displayName[, displayNames], ConfigEntry[])` 两个重载（ModConfigApi.cs:136, 150）、`GetValue<T>` / `SetValue`（ModConfigApi.cs:164, 172）；数据类 `ConfigEntry`（ModConfigApi.cs:22，Key/Label/Labels/Type/DefaultValue/Min/Max/Step/Format/Options/MaxLength/Validator/OnChanged…）与 `ConfigType` 枚举 9 种（ModConfigApi.cs:6-17）。
- 消费侧桥接（`examples/ModConfigBridge.cs`，drop-in 模板）：
  - `AppDomain.CurrentDomain.GetAssemblies()` 全程序集扫类型，按 FullName 找 `ModConfig.ModConfigApi/ConfigEntry/ConfigType`（ModConfigBridge.cs:57-68）；
  - `DeferredRegister()` 挂一帧 `ProcessFrame` 再探测注册，解决"我的 mod 先加载"（ModConfigBridge.cs:37-49）；
  - 按参数个数选 `Register` 重载（ModConfigBridge.cs:96-119）；
  - `GetValue` 全部 try/catch 回退 fallback，ModConfig 不在时 mod 照常运行（ModConfigBridge.cs:131-142）。
- 持久化（`Scripts/ModConfigManager.cs`）：`user://ModConfig/<modId>.json`（Godot `FileAccess`，ModConfigManager.cs:19, 235-251）；写盘**去抖**——脏 modId 收集后挂下一帧 `ProcessFrame` 一次性 flush，滑杆连续拖动只写一次（ModConfigManager.cs:162-186）；读盘按 `ConfigType` 分派 `JsonElement` 转换，失败回落默认值（ModConfigManager.cs:216-231）；UI 折叠状态单独存 `_ui_state.json`（ModConfigManager.cs:261-300）。

### 2.3 控件自动生成与输入支持

- 映射：`PopulateInto` 按 `entry.Type` switch 到 `AddToggle/AddSlider/AddDropdown/AddKeyBind/AddTextInput/AddButton/AddColorPicker`（SettingsTabInjector.cs:583-612），Header/Separator 纯视觉。
- 典型实现：Toggle=`CheckButton`（SettingsTabInjector.cs:842-890）；Slider=`HSlider`+数值标签，`Format`（"F0"/"P0"）控制显示（SettingsTabInjector.cs:894-966）；Dropdown 支持 `OptionsKeys` 逐项 i18n（SettingsTabInjector.cs:970+）。
- 程序化同步：每个控件注册 `LiveBinding`（`WeakReference` 持有控件，失效即移除），外部 `SetValue` 时经 `NotifyValueChanged` 回推 UI，并用 `UiUpdateGuard.Suppress` 防止回写环路（SettingsTabInjector.cs:33-46, 479-513）。
- KeyBind：值类型 `long`（Godot keycode 含修饰键），临时 `KeyCaptureNode` 挂到 `SceneTree.Root` 捕 `_UnhandledKeyInput`，`GetKeycodeWithModifiers()` 打包组合键，Esc/点击空白解绑（`Scripts/KeyCaptureNode.cs:25-60`，SettingsTabInjector.cs:1125-1148）。
- 字体/本地化：手动创建的 Label 复用原面板主题字体（修 Linux CJK 缺字，SettingsTabInjector.cs:364-404）；i18n 双通道（DLL 内嵌 JSON + PCK），订阅 `LocManager.SubscribeToLocaleChange` 热切换（`Scripts/I18n.cs:52-78`）。

---

## 三、与描述不符/需注意

1. CombatSolver 的"多人"已归档：`archive/multiplayer` 分支仅历史参考，当前主线只支持单人（AGENTS.md §1；README:142 明示多人不在兼容范围）——对我们的多人重连 mod 无直接代码可抄，但其主线程/后台线程分离方法论可迁移。
2. ModConfig 仓库结构：C# 源码在仓库根 `MainFile.cs` + `Scripts/`，`ModConfig/` 目录只放 localization/manifest/图片（还有 Godot 工程文件 `project.godot`、`export_presets.cfg`）；安装物是 `ModConfig.dll + ModConfig.pck`（README:24）。
3. 版本漂移：`mod_manifest.json` 为 0.2.2，而 `MainFile.cs:10` 常量仍是 `"0.2.1"`。
4. ModConfig 零 Harmony 的代价：依赖反射私有字段 `_tabs`、`_firstControl` 与 `Duplicate()` 行为，游戏 UI 重构即失效；CombatSolver 则相反——大量 Harmony patch，需跟进游戏版本（要求 0.111.0）。
5. CombatSolver 的 MemoryCleaner/深度内存释放仅 Windows；预战预报 API 同样只支持 Windows 单人（README:65）。

---

## 四、可借鉴清单（面向 AutoReconnectMinCore）

1. **主线程捕获根快照 + 后台只读分叉**（`CombatRootSnapshot`/`PredictionForkContext`）：重连时可在主线程冻结一份战斗状态做"预期推演"，后台线程只操作影子副本，绝不写 live——与我们打多人层补丁的风险控制同构。
2. **RNG 快照重放**（`CombatPredictionRngSet`）：`CaptureState()` + 懒物化，不推进游戏 RNG 即可做确定性模拟；若重连后需要校验/重放战斗随机序列，可直接套此模式。
3. **帧压感知的后台让步**（`SearchFramePressureSignal` + `SearchWorkPacer`）：空闲帧间隔中位数基线 → 压力阈值 1.5× → worker spin 等下一帧；后台任务（如同步、探测）避免拖垮游戏帧率的标准做法。
4. **专用 lane 线程 + BelowNormal + 确定性提交**（`ParallelExpansionExecutor`）：固定线程、事件派发、结果按序回协调线程提交；后台线程不做任何 Godot/游戏对象访问。
5. **执行一律走原生 API 而非模拟输入**（`TryManualPlay`/`ActionQueueSynchronizer.RequestEnqueue`）：重连 mod 若要代发操作，同样应入队原版 action，并监听实际 action 与计划不符即回退重算。
6. **零 Harmony UI 注入**（`SettingsTabInjector`）：`SceneTree.NodeAdded` + `Duplicate()` + 反射私有字典，不动函数即可给设置页加标签页——适合做重连状态面板/设置页且不与其他 patch 冲突。
7. **零依赖反射桥模板**（`examples/ModConfigBridge.cs`）：全程序集类型扫描 + 延迟一帧 + 按参数个数选重载 + 全链路 fallback；可作为我们对外提供"可选集成 API"的模板。
8. **去抖持久化**（`ModConfigManager.ScheduleSave`）：脏集合 + 下一帧 `ProcessFrame` flush；UI 高频改动合并一次落盘。
9. **WeakReference 活绑定 + 失效自清**（`LiveBinding`/`NotifyValueChanged`）：UI 重建后旧控件自动解绑，避免 Godot 对象已释放仍被回调。
10. **上报安全细节**（`CombatBugReportUploader`）：证书固定引脚 + `FixedTimeEquals` + 体积上限 + 自动分类；若要加诊断上传可直接复用。提权助手进程模式（`SystemMemoryReleaseService` + `runas` + UAC 取消处理）也值得留档。
