# STS2 Mod 生态研究知识库（12 款 mod 源码学习与借鉴汇总）

---

## 1. 总览：12 款 mod 的来源与形态

标注规范：**有仓库的标注源码仓库**，无仓库的标注创意工坊链接，两者皆无的标注作者。

| Mod             | 作者                    | 源码仓库                                                                                     | 创意工坊                                                                            | 版本     | 学习/借鉴                                                                                     |
| --------------- | --------------------- | ---------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- | ------ | ----------------------------------------------------------------------------------------- |
| STS2-RitsuLib   | BAKAOLC               | [BAKAOLC/STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)                        | [3747602295](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) | 0.6.9  | 设置系统接入：`CoreModConfigBridge` 经 `RegisterModSettings` 注册设置页                                |
| JmcModLib       | JMC-Mods              | [JMC-Mods/SlayTheSpire2\_JmcModLib](https://github.com/JMC-Mods/SlayTheSpire2_JmcModLib) | [3747526103](https://steamcommunity.com/sharedfiles/filedetails/?id=3747526103) | 1.9.0  | 暂停菜单按钮注入：`PauseMenuInviteButton` 经 `[PauseMenuButton]` 添加「邀请重连」                           |
| QuickLink（皮皮快连） | sts2.piyixiajiuhenfen | —（release 分发）                                                                            | [3747557834](https://steamcommunity.com/sharedfiles/filedetails/?id=3747557834) | 0.9.13 | 错误弹窗守卫（`ErrorPopupGuardPatch`）+ 重连后状态就绪门闩（`ReconnectRunner.WaitRunReadyAsync`），v0.9.37 落地 |

---

## 2. 前置库三库（mod 生态的框架层）

### 2.1 RitsuLib（本 mod 的依赖）

- 入口 `[ModInitializer]` + `CreatePatcher(modId, name)` 每逻辑区一个 Harmony 实例；补丁类实现 `IPatchMethod`（`GetTargets(): ModPatchTarget[]` 声明式）；`ApplyRequiredPatcher(patcher, DisableMod)` 关键失败自禁用不崩游戏。
- 生命周期事件订阅 `SubscribeLifecycle<TEvent>(...)`，**可回放事件**对迟订阅者补发当前状态（重连 mod 晚启动也能拿到 Run 状态）。
- Sidecar 副协议：**lobby 构造即拿 INetGameService**（`MethodType.Constructor` 同补 `StartRunLobby..ctor`×2 + `LoadRunLobby..ctor`×2，Postfix `ObserveNetService`）；**消息尾补丁**（给现有 INetMessage 追加 mod 字节，不占消息 ID）；加入诊断补丁（`JoinFlow.HandleInitialGameInfoMessage` 失败归因）。

### 2.2 JmcModLib（本 mod 的依赖）

- 下游一行 `ModRegistry.Register<MainFile>()`；`MethodAccessor`/`MemberAccessor` 三级缓存反射（DynamicMethod 快路 → 通用 invoker → MethodInfo.Invoke 降级）。
- `[Config]`/`[UIButton]` 零回调配置 UI（AttributeRouter 扫描分发）；设置页注入 = 复制 Input 页签塞 `NSettingsTabManager._tabs`（反射私有字段）。
- **OptionalNetworkFeature**：关闭联机功能的 mod 通过 `GetGameplayRelevantModNameList` 注入 `JML-ONF1...` 兼容 token 不进校验；`NErrorPopup.Create` Prefix 换自定义弹窗。
- **MultiplayerCompat**（`Lazy<MemberAccessor?> + FindReadableMember(新名, 旧名...)`）：跨版本改名解析模板（NetService/PlayerIds/ConnectedPeers/ConnectionExtraInfo），注释明确 0.107.1 须在 `JoinFlow.Begin()` 之后才拿得到 NetService。

---

## 3. 皮皮家族深度要点

### 3.1 QuickLink（与自家 mod 最同构：多人回退+重连，32 个 patch）

- **断线重连核心**（1.4）：`NErrorPopup.Create(NetErrorInfo)` Prefix 白名单判断可重连 reason → `__result=null; return false` 吞弹窗 → 静默重连：5 秒窗口 `SteamFriends.GetFriendGamePlayed(hostId)` 探测房主新 lobby → **18 次重试** `SteamClientConnectionInitializer.FromLobby/FromPlayer` + 反射构造 `JoinFlow` → `joinFlow.Begin(initializer, sceneTree)` → 成功判据 `sessionState==2 && loadJoinResponse.HasValue` → 按 gameMode 走原版 `N*LoadScreen.InitializeAsClient` 压入 SubmenuStack。**不自己造协议/UI**。
- **带内信令**（1.3）：无自定义 INetMessage，劫持 `ReactionMessage`（`type=(ReactionType)6` + `normalizedPosition.X=2.75f` 魔数，6 条指令编码在 Y）；定向发送 = 反射取 `_messageBus` → `NetMessageBus.SerializeMessage<T>` 裸字节 → `NetHost.SendMessageToClient/NetClient.SendMessageToHost(..., NetTransferMode)`。
- **回退 = 重新 hosting checkpoint**（1.5/1.6）：内存 `SerializableRun` 快照（patch `RunSaveManager.SaveRun`，15 上限，按 (StartTime, Act, MapCoord, 玩家集合) 去重）；`CanonicalizeSave(JSON 深拷贝, localPlayerId)` + `new NetHostGameService().StartSteamHost(4)` + `InitializeAsHost`；队友全靠重连回房。
- **自动准备**：轮询 `LoadRunLobby` 已连接玩家数 vs `lobby.Run.Players.Count`，到齐反射调加载屏**私有无参方法**（模拟点击准备，不直接动 `SetReady`）。
- **状态就绪探测器**（1.5）：`MainMenu==null → NRun 就绪 → RunManager.IsInProgress → ActionExecutor 未暂停 →（战斗中）CombatManager/NCombatRoom/NCombatUi/手牌/结束回合按钮` 顺序轮询，全就绪才收遮罩。

## 5. 已落地到 AutoReconnectMinCore 的借鉴

以下为**实际运用在本 mod 当前版本（v0.9.37-core）代码中**的生态借鉴，均有对应实现：

1. **原版错误弹窗守卫**（借鉴 QuickLink 的 `NErrorPopup.Create` 拦截，`Scripts/Net/ErrorPopupGuardPatch.cs`）：`[HarmonyTargetMethod]` 精确锁定 `NErrorPopup.Create(NetErrorInfo)` 重载；仅当重连流程进行中（`HostInfoTracker.IsReconnecting || MenuRejoinFlow.IsRejoining`）时 `__result = null; return false` 抑制原版弹窗，空闲时原版行为一律放行；守卫自身异常也放行，不成为崩溃源。
2. **重连后状态就绪门闩**（借鉴 QuickLink 的「状态就绪探测器」，`Scripts/ReconnectRunner.cs`）：重连成功后 `WaitRunReadyAsync` 以 250ms 轮询 × 20s 超时等待对局就绪，判定链 `NGame.Instance.MainMenu == null → CurrentRunNode != null → RunManager.IsInProgress →（战斗中）CombatManager.IsInProgress`；超时不阻断，仅记录诊断日志并按就绪与否给出不同回执（`ReconnectOverlay.SetStatus` 负责文案覆盖）。
3. **前置库能力运用**：RitsuLib 设置系统（`Scripts/CoreModConfigBridge.cs`，`RitsuLibFramework.RegisterModSettings` 注册配置项）与 JmcModLib 暂停菜单按钮（`Scripts/PauseMenuInviteButton.cs`，`[PauseMenuButton]` 特性添加「邀请重连」按钮）。

---

## 6. 工程纪律与踩坑（本次新增）

- **全局命名空间类型污染构建（2026-10-22 实锤）**：`tools/sts2sig/Program.cs` 的 `sealed class Engine` 在**全局命名空间**，经 Core csproj 默认 `**/*.cs` glob 混入 `AutoReconnectMinCore.dll`；全局命名空间类型在简单名查找中**击败** `using Godot` 导入的 `Godot.Engine`（命名空间链查找优先于 using 导入）→ 3 个文件 8 处 `Engine.GetMainLoop()` 报 CS0117，而 `typeof(Engine)` 能编译、全限定 `Godot.Engine.GetMainLoop()` 正常——极具迷惑性。**修复：csproj 加 `<Compile Remove="tools\**" />`**。教训：仓库内多项目平铺时，游戏 mod 项目必须显式收窄编译 glob 或排除 tools 目录。
- **字符串混淆 DLL 的学习方法**：标识符混淆但控制流清晰；关键靠 (a) 调用链上下文推断语义（如 QuickLink 6 个 ReactionMessage Y 值指令）；(b) sts2sig 核实游戏侧 API 全名与字段（`ReactionMessage`/`ReactionType`/`CanonicalizeSave` 均已核实）；(c) 混淆字符串可用 XOR 解密例程还原。
- 皮皮家族 UI 一律**纯代码构建 + 自建 CanvasLayer**（Layer 分层：102 菜单/100 仪表盘/64 弹幕），pck 仅承载 mod 元数据——版本兼容性最好的路线，我们 ReconnectOverlay 同款。

---

## 7. 材料索引

- 四份详细研究报告（本仓库 `docs/research/`）：`libs.md`（三大前置库）、`solver-modconfig.md`（CombatSolver × ModConfig）、`quicklink-rewind.md`（QuickLink × Rewind 联机专题）、`qol-danmaku.md`（SpeedX / DamageMeter / SkadaHelper / DanmakuSpire）
- API 探查三层工作流与 sts2sig 用法见 README「开发工具：sts2sig」一节
