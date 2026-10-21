# STS2 三大基础库技术分析（面向 AutoReconnectMinCore 联机重连 mod）

> 分析对象：STS2-RitsuLib v0.6.9（MIT，1344 个 .cs）、BaseLib-StS2 v3.4.7（has_pck+has_dll，min_game 0.107.1）、SlayTheSpire2_JmcModLib v1.9.0（LGPL）。游戏 .NET 9 Mono 后端。
> 引用格式为「仓库相对路径:行号」。本报告只摘关键签名，不贴大段源码。

---

## 1. RitsuLib（框架库）

### 1.1 mod 入口与加载机制
- 入口：`[ModInitializer(nameof(Initialize))] public static partial class RitsuLibFramework`（src/RitsuLibFramework.cs:69）。`Initialize()`（src/RitsuLibFramework.cs:411）在锁内做：LinuxHarmonyNativePreloader → 各 built-in 服务 → 注册并 `PatchAll` 各 FrameworkPatcherArea 补丁 → 发布生命周期事件；关键补丁失败则 `IsActive=false` 并不抛崩游戏。
- manifest：mod_manifest.json，`"has_dll": true, "dependencies": []`（自身即被依赖方）。下游 mod 声明 `{"dependencies":[{"id":"STS2-RitsuLib"}]}`（0.105.x+ 对象式；旧版用字符串式，见 docs/pages/guide/getting-started.md）。
- Harmony：不直接暴露。`ModPatcher` 构造函数内 `new Harmony(patcherId)`（src/Patching/Core/ModPatcher.cs:40），每个逻辑区域一个实例。

### 1.2 补丁/挂钩框架（Patcher 封装）
- 创建：`RitsuLibFramework.CreatePatcher(ownerModId, patcherName, patcherLabel?, logType)`（src/RitsuLibFramework.cs:1698）→ Harmony ID 为 `{ownerModId}.{patcherName}`，配专属 logger。
- 声明式补丁：补丁类实现 `IPatchMethod` 静态抽象接口（src/Patching/Models/IModPatches.cs）：`PatchId` / `IsCritical` / `Description` / `GetTargets(): ModPatchTarget[]`；postfix/prefix 直接写成静态方法（Harmony 原生 `__instance`/`__result` 约定）。示例：src/Lifecycle/Patches/NMainMenuReadyLifecyclePatch.cs:20-28。
- `ModPatchTarget(Type, methodName, parameterTypes?, ignoreIfMissing?, MethodType)`（src/Patching/Models/ModPatchTarget.cs:32）——支持 `MethodType.Constructor/Getter/Setter` 与重载消歧；工厂助手 `PatchTarget.Method<T>("OnEnter")` / `PatchTarget.Getter<Player>("Piles")` / `PatchTarget.OptionalGetter<T>`（src/Patching/Models/PatchTarget.cs）。
- 注册/应用/卸载：`RegisterPatch<T>()` / `RegisterPatches(...)` / `RegisterDynamicPatch(DynamicPatchInfo)`；`PatchAll()`（src/Patching/Core/ModPatcher.cs:266，幂等、按 IsCritical 记录成败）；`ApplyLateStaticPatches`、`UnpatchExternalPatches(patcherId,...)`、`UnpatchAll()`。重复 ID 跳过，PatchAll 后再注册抛异常。
- 关键失败自禁用：`ApplyRequiredPatcher(patcher, disableMod, failureMessage?)`（src/RitsuLibFramework.cs:2064）——官方推荐「每逻辑区域一个 patcher，必要区域用 ApplyRequiredPatcher」（docs/pages/guide/patching-guide.md）。
- 动态补丁：`DynamicPatchInfo(id, MethodBase, HarmonyMethod? prefix/postfix/transpiler/finalizer, isCritical, description)`（src/Patching/Models/DynamicPatchInfo.cs:47）+ `DynamicPatchBuilder` 流式累积（src/Patching/Builders/DynamicPatchBuilder.cs:14）；支持 lifetime lease（重连 mod 可做「会话期间动态挂载、断开卸载」）。
- 私有成员：`PrivateAccess.FieldRef<T,TField>(name)` / `DeclaredGetterDelegate<...>` / `DeclaredMethodDelegate<...>` 包装 AccessTools 并在初始化期做强校验（src/Patching/PrivateAccess.cs）。
- 诊断：`PatchLog.For<TPatch>()` 按补丁类型绑定 logger（src/Patching/Core/PatchLog.cs）；`RitsuLibStartupAudit.Measure(phase, action)` 分阶段计时并出报告（src/Diagnostics/RitsuLibStartupAudit.cs:33）；`HarmonyPatchDumpCoordinator` 可导出已生效补丁；`SelfCheckBundleCoordinator` 生成自检包。

### 1.3 生命周期钩子
- `SubscribeLifecycle(ILifecycleObserver, replayCurrentState=true)`（src/RitsuLibFramework.cs:169）；泛型版 `SubscribeLifecycle<TEvent>(Action<TEvent>, replay)`（:242）与带 subscription 自处置版（:317，handler 内可 `sub.Dispose()` 一次性订阅）；`SubscribeLifecycleOnce<T>`（src/RitsuLibFramework.LifecycleOnce.cs）。
- 事件契约：框架级 `FrameworkInitializing/InitializedEvent`、`ProfileServices*Event`（src/FrameworkLifecycleContracts.cs）；游戏级覆盖初始化、模型注册、`GameTreeEntered/GameReady/MainMenuReady`、`RunStarted/Loaded/Ended`、房间/战斗/卡牌/奖励（src/GameLifecycleContracts.cs:15 起全量 record struct）。
- 可回放事件（`IReplayableFrameworkLifecycleEvent`）会对迟订阅者立即补发——重连 mod 晚启动也能拿到当前 Run 状态。

### 1.4 多人/网络（Networking / Sidecar）
Sidecar 是一条**复用游戏自身消息总线**的副协议（src/Networking/Sidecar/，122 个文件）：
- 信封构建：`RitsuLibSidecar.CreateEnvelope(opcode, payload, extraFlags, gzipPayload, headerExtension)`（src/Networking/Sidecar/Core/Api/RitsuLibSidecar.cs:51）；opcode 由 `RitsuLibSidecarOpcodes.For` 或 `RitsuLibSidecarControlOpcodes` 分配。
- 分层：Protocol（Envelope/Wire/Compression/Chunk/Header）、Session（SessionManager/ConnectionExchange/ConnectionSession/NetworkingLifecycle）、Endpoints（Registry/BulkTransfer/TokenBucket 限流）、Messaging（TypedMessages/RequestReply/Dispatch）、Transport、Discovery。
- 挂载方式（重点借鉴）：`RitsuLibSidecarLobbyHelloPatch` 以 `MethodType.Constructor` 同时补 `StartRunLobby..ctor`（2 个重载）与 `LoadRunLobby..ctor`（2 个重载），Postfix 里 `SessionManager.ObserveNetService(lobby.NetService)` + `TrySendClientHelloIfReachable(...)`（src/Networking/Sidecar/Patches/RitsuLibSidecarLobbyHelloPatch.cs:30-76）——**正是重连 mod 需要的「lobby 构造即拿 INetGameService」手法**。
- 消息尾补丁：`RitsuNetMessageBusTailPatches.Register<TMessage>(...)` + `ApplySerializePatches(patcher)`（src/Networking/MessageExtensions/RitsuNetMessageBusTailPatches.cs:23-50）——在 NetMessageBus 序列化边界给**现有** INetMessage 类型（InitialGameInfoMessage / StateDivergenceMessage / LobbyBeginRunMessage）追加 mod 专属尾字节，不占用新消息 ID；配套 deserialize postfix（同文件:147）。
- 自定义网络动作：`RitsuLibManagedNetAction : INetMessage`（DescriptorOpcode+GameActionType+Payload，src/Networking/ManagedActions/RitsuLibManagedNetAction.cs:10）。
- 加入诊断：补 `JoinFlow.HandleInitialGameInfoMessage`（src/Networking/JoinDiagnostics/Patches/JoinFailureDiagnosticsPatches.cs:43）做加入失败归因。

---

## 2. BaseLib（内容/工具库）

### 2.1 入口与 Patches 手法
- 入口：`[ModInitializer(nameof(Initialize))] public static class BaseLibMain`（BaseLibMain.cs:16）。持有单例 `Harmony MainHarmony`（BaseLibMain.cs:25），入口里少数类手工 `Patch(MainHarmony)`，其余 `MainHarmony.TryPatchAll(assembly)`（BaseLibMain.cs:66）——**经典特性式，无自建 Patcher 抽象**。Linux 下 dlopen libgcc 修 Harmony（BaseLibMain.cs:78-101）。
- 版本兼容补丁写法：`Prepare()` + `TargetMethod()` 动态解析（如 `MessageTypes.Initialize` 不存在则退回静态构造函数，找不到就干净跳过）（Patches/Networking/CustomMessagePatches.cs:36-51）。
- 钩子接口分发：`IHealAmountModifier` 等接口 + `HookUtils.Dispatch<THook>(combatState, ...)` 遍历 `Hook.IterateCombatHookListeners`（Hooks/BaseLibHooks.cs:15，Utils/HookUtils.cs:12）。
- 多版本成员解析：`VariableReference<T>` / `VariableMethod`（Utils/BetaMainCompatibility.cs:318/388）——构造时按候选 `(Type,name)` / `(类型名,方法名,参数,参数索引)` 逐个 TryName，命中即用，全落空才抛异常。例：`LobbyLocalReady()` 同时兼容 `StartRunLobbyPlayer`/`LobbyPlayer` 改名（同文件:85-96）。

### 2.2 联机相关（BaseLib 的联机能力集中在自定义消息）
- `CustomMessageWrapper : INetMessage`（Abstracts/CustomMessage.cs:11）：**一个**包装消息类型承载所有 mod 自定义消息。内层消息按 `FullName.ComputeBasicHash()` 得 ID；类型经 `ReflectionHelper.GetSubtypesInMods<ICustomMessage>()` 自动发现（同文件:31）。
- 发送/接收：`CustomMessageWrapper.Send(msg, netService?)` → `(netService ?? RunManager.Instance.NetService).SendMessage(wrapper)`（Abstracts/CustomMessage.cs:94-96）；在 `RunManager.InitializeShared` Postfix 注册 handler、`CleanUp` 反注册（Patches/Networking/CustomMessagePatches.cs:12-26）。
- ID 占用手法：Postfix `MessageTypes.Initialize`（或静态构造）改写 `MessageTypes._cache`，把两个 wrapper 的 ID 抢到 128-254 高位，并 Postfix `MessageTypes.TryGetMessageType` 拦截映射（Patches/Networking/CustomMessagePatches.cs:53-108）。定向广播另有 `CustomTargetedMessageWrapper : IRunLocationTargetedMessage`（Abstracts/CustomTargetedMessage.cs:14）。
- 配置：`ModConfig` 抽象类 + `[ConfigSection]/[ConfigSlider]/[ConfigIgnore]/[ConfigHoverTip]` 特性反射生成 UI，JSON 持久化，`NGame.Quit` 时保存（Config/ModConfig.cs:26-38，Config/ConfigAttributes.cs）。
- 大厅相关仅零星引用（CustomCharacterModel.cs:342 反射 `StartRunLobby.BeginRunLocally`），无系统化重连/断线钩子。

---

## 3. JmcModLib（工具库）

### 3.1 入口与注册
- 入口：`[ModInitializer(nameof(Initialize))] public partial class MainFile : Node`（MainFile.cs:9）——自身是 Godot Node；`Initialize()` 里 `OptionalNetworkFeatureManager.Init()` → `ModRegistry.Register<MainFile>()` → `new Harmony(VersionInfo.Name).PatchAll(assembly)`（MainFile.cs:15-23）。
- 下游 mod 只需一行 `ModRegistry.Register<MainFile>()`，自动推断 Assembly/ModId/DisplayName/Version 并启用 Logger/Config/Persistence/AttributeRouter（docs/JML_QuickStart.md）。manifest 依赖 `{"id":"JmcModLib","min_version":"1.6.1"}`（docs/JML_OptionalNetworkFeatures.md）。

### 3.2 反射库（MethodAccessor / MemberAccessor）
- 缓存三级（Reflection/MethodAccessor.cs:20-26）：`_typeMethodGroups`（Type→方法名→List<MethodInfo>，只建一次）、`_nameFirstAccessor`（(Type,Name)→Accessor 快速路径）、`_signatureIndex`（Type→Name→ParamSignature→Accessor）。`ParamSignature`（:34）把泛型参数占位统一哈希为 0，使不同 T 落在同一缓存键。
- 调用三档：① `DynamicMethod` 生成的 fast invoker（0-3 参、无 ref/out/可选参，:767-863）；② `object?[]` 通用 invoker，支持 ref/out（IL 局部变量写回，:900-1014）；③ `CreateInvokerOrFallback` 失败降级 `MethodInfo.Invoke` 并用 `ExceptionDispatchInfo` 重抛内层异常（:1016-1057）。默认参数用 `[ThreadStatic]` buffer 补齐（:83）。
- `TypedDelegate`：Expression 树编译成 `Func/Action<>`（≤8 参，实例方法首参为声明类型）（:160-219），配 `Invoke<TTarget,TResult>(...)` 强类型语法糖；泛型方法先 `MakeGeneric(...)`（:333）。
- MemberAccessor（Reflection/MemberAccessor.cs）：字段/属性 getter/setter 委托、TypedGetter/TypedSetter、索引器、`(Type,string)` 查找缓存（:13）。两者共用 `ReflectionAccessorBase<TMember,TAccessor>` 的 GetOrCreate 缓存（Reflection/ReflectionAccessorBase.cs）。

### 3.3 [Config] / [UIButton] 零回调配置 UI
- 特性：`ConfigAttribute(displayName, onChanged?, group?)`（Config/ConfigAttribute.cs:10，还有 Key/Description/LocTable/Order/RestartRequired）；`UIButtonAttribute(description, buttonText?, group?)`（Config/UI/Attributes/ConfigUiAttribute.cs:11，含 UIButtonColor/HelpText/Order）。两者都带静态校验 `IsValidMethod`（回调必须 static 且参数签名匹配）。
- 机制：`AttributeRouter` 扫描已注册程序集的特性并按特性类型分发给 `IAttributeHandler`（Core/AttributeRouting/AttributeRouter.cs）；`ConfigAttributeHandler`/`UIButtonAttributeHandler` 把成员/方法转成 `ConfigEntry`/`ButtonEntry` 注册进 `ConfigManager`（Config/ConfigAttributeHandler.cs:11-56）——**mod 侧零回调代码**。
- UI 落点：Postfix `NSettingsScreen._Ready`/`OnSubmenuOpened` → `ModSettingsTabBridge.Install`：复制 "Input" 页签与 "GeneralSettings" 面板，塞进 `NSettingsTabManager._tabs`（反射私有字段）生成「Mod 设置」页（Config/UI/Bridge/ModSettingsTabBridge.cs:21-60、139-155）。
- 本地化键约定：`EXTENSION.JMCMODLIB.CONFIG.{modId}.{storageKey}.{NAME|DESCRIPTION|BUTTON|GROUP|OPTION.*}`（Config/ConfigLocalization.cs:73-80），找不到 key 回退特性里的硬编码文本。

### 3.4 本地化与日志
- L10n（Utils/L10n.cs）：STS2 从 PCK **自动发现** mod 本地化表，L10n 只拼 `res://{pckName}/localization/{lang}/{table}.json` 路径并用 `TableCache` 缓存读取（:24-45、:334-360）——注意 STS2 用 JSON 表而非 CSV；`EnumerateExistingModTablePaths` 自动追加 eng 回退（:53）。
- ModLogger（Utils/Logger/ModLogger.cs）：per-Assembly `Logger` + `AssemblyLogConfiguration`（LogType/PrefixFlags/ThrowOnFatal/IncludeExceptionDetails，:36-98）；日志前缀 TAG = `ModRegistry.GetTag(assembly)` → `[DisplayName v1.2.3]`（Core/Registry/ModRegistry.cs:306，用于 :417）。

### 3.5 OptionalNetworkFeature（可选网络功能，与重连最相关）
- 声明：`[OptionalNetworkFeature(id, messageMarkerType)]` 标在字段/属性上（Multiplayer/OptionalNetworkFeatureAttribute.cs:17）；用户开关→`EffectiveEnabled` 决定协议实际状态，状态机 Applied/PendingNetworkIdle/RestartRequired（docs/JML_OptionalNetworkFeatures.md）。
- 三个关键补丁（Multiplayer/Patches/OptionalNetworkFeaturePatches.cs）：
  1. Postfix `ReflectionHelper.GetSubtypesInMods<INetMessage>`，按 `ShouldIncludeMessage` 过滤掉已关闭功能的消息类型（:14-24）；
  2. Postfix `ModManager.GetGameplayRelevantModNameList`，注入 `JML-ONF1...` 兼容 token（:35-52）——**让关闭联机功能的 mod 不进入房客/房主兼容性校验**；
  3. Prefix `NErrorPopup.Create(NetErrorInfo)`，mod 不匹配时换自定义弹窗（:54 起）。
  另有 Prefix/Postfix `OneTimeInitialization.ExecuteEssential` 准备/标记协议（:26-33）。

### 3.6 MultiplayerCompat（多版本联机兼容层，重连 mod 直接可抄）
`Lazy<MemberAccessor?>` + `CompatMemberResolver.FindReadableMember(新名, 旧名...)` 候选解析（Compat/MultiplayerCompat.cs:14-96），对外 API：
- `TryGetConnectionExtraInfo(NetErrorInfo)`（:98，0.107 前读私有 `_connectionExtraInfo`，0.108+ 读属性 `ConnectionExtraInfo`）；
- `TryGetJoinFlowNetService(JoinFlow)`（:129，`NetService`/`_netService`，注释明确 0.107.1 须在 `JoinFlow.Begin()` 之后才拿得到）；
- `GetRunLobbyPlayerIds(RunLobby)` / `GetLoadRunLobbyPlayerIds(LoadRunLobby)`（:146/:161，`PlayerIds` vs 旧 `ConnectedPlayerIds`）；
- `GetConnectedHostPeerIds(INetHostGameService)`（:179，0.107.1-0.110.1 走 `ConnectedPeers`）。
- 不匹配信息读取：`ConnectionFailureExtraInfo` 0.110 前读 `missingModsOnHost/Local` 字段，0.110+ 改 `GetMissingModsOnLocal/Remote(bool)` 方法 + `localIsHost`（:38-77）。

---

## 4. 可借鉴清单（按性价比排序）

1. **lobby 构造即取 NetService**：抄 `RitsuLibSidecarLobbyHelloPatch`（src/Networking/Sidecar/Patches/RitsuLibSidecarLobbyHelloPatch.cs:30-60）——用 `ModPatchTarget(..., MethodType.Constructor)` 一组目标同时补 `StartRunLobby..ctor`×2 + `LoadRunLobby..ctor`×2，Postfix 里 `ObserveNetService` + 发握手。我们重连 mod 的 ReconnectService 可直接复用此模式拿 `INetGameService`。
2. **消息尾补丁承载私有协议**：抄 `RitsuNetMessageBusTailPatches`（src/Networking/MessageExtensions/RitsuNetMessageBusTailPatches.cs）——给现有 INetMessage 追加 mod 字节，**不占消息 ID、不动 MessageTypes 缓存**，比重连握手/状态校验自带消息更安全；BaseLib 的「高位 ID + `_cache` 改写 + `TryGetMessageType` 拦截」（Patches/Networking/CustomMessagePatches.cs:53-108）作为需要独立消息类型时的备选。
3. **多版本成员解析模板**：抄 JmcModLib `MultiplayerCompat` 的 `Lazy<MemberAccessor?> + FindReadableMember(新名, 旧名...)`（Compat/MultiplayerCompat.cs:14-96）处理 NetService/PlayerIds/ConnectedPeers/ConnectionExtraInfo 的跨版本改名；BaseLib `VariableReference<T>`（Utils/BetaMainCompatibility.cs:318）可作无 JmcModLib 依赖时的轻量替代。
4. **兼容性 token 注入 + 错误弹窗拦截**：抄 `OptionalNetworkCompatibilityPatch`/`OptionalNetworkMismatchPopupPatch`（Multiplayer/Patches/OptionalNetworkFeaturePatches.cs:35-54）——重连 mod 可让「未装 mod 的房客」得到友好提示而非裸错误，并可选择不进 `GetGameplayRelevantModNameList` 校验。
5. **生命周期驱动重连时机**：用 `RitsuLibFramework.SubscribeLifecycle<MainMenuReadyEvent/RunLoadedEvent/DeferredInitializationCompletedEvent>(...)`（docs/pages/guide/lifecycle-events.md）+ `replayCurrentState` 补发，避免为「回到主菜单再重连」自写补丁。
6. **补丁分区与失败自禁**：`CreatePatcher(modId, "lobby"/"join"/"menu")` 每区一个 + `ApplyRequiredPatcher(patcher, DisableMod)`（src/RitsuLibFramework.cs:2064）；重连功能可放非关键 patcher，失败只禁重连不崩游戏。
7. **动态补丁 + lifetime lease**：`DynamicPatchBuilder`/`DynamicPatchInfo`（src/Patching/Builders/DynamicPatchBuilder.cs）在会话建立时挂载、断开时 `UnpatchAll`——适合「仅联机中生效」的补丁。
8. **诊断体系**：`RitsuLibStartupAudit.Measure` 分阶段计时 + `SelfCheckBundleCoordinator` 自检包 + `HarmonyPatchDumpCoordinator` 补丁导出（src/Diagnostics/）；JmcModLib `ModLogger` 的 per-Assembly TAG（`[Name vVersion]`）日志规范。重连 mod 的 ReconnectDiagnostics 可对标产出同等可提交的排障材料。
9. **私有成员强校验读取**：`PrivateAccess.FieldRef/DeclaredGetterDelegate`（src/Patching/PrivateAccess.cs）在初始化期即暴露缺失，比重连 mod 里裸 `AccessTools` 更早失败。
10. **设置 UI 两条路线**：我们已用 RitsuLib `RegisterModSettings`（src/RitsuLibFramework.cs:1376）；JmcModLib 的「复制 Input 页签注入 NSettingsTabManager._tabs」（Config/UI/Bridge/ModSettingsTabBridge.cs:139-155）可作为不依赖 RitsuLib 设置页时的备选。
