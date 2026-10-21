# 断线重连 | AutoReconnectMinCore

**[杀戮尖塔 2](https://store.steampowered.com/app/2868840/)（Slay the Spire 2）联机断线自动重连 Mod —— 核心版（Reconnect Only）**

多人联机时一人掉线、全员被迫退出？本 mod 在客机掉线后**自动重连**回原对局：静默吞掉原版错误弹窗、保留内存活对局、按需选择零重建或整局重建路径，重连成功后等待对局状态就绪再收起浮层。房主还可通过暂停菜单的「邀请重连」按钮，把掉线队友重新拉回大厅。

本仓库是**可独立安装的重连核心**。在此之上的扩展版（检查点回退等）在另一个仓库 [STS2-AutoReconnect-Mod](https://github.com/JiangBing-Ren/STS2-AutoReconnect-Mod)。

---

## 功能特性

- **客机掉线自动重连**：瞬断（Timeout / StateDivergence 等非用户主动退出）自动触发重连流程，无需手动操作
- **双路径重锚**：
  - *零重建*——复用既有网络服务与内存活对局，秒级回到地图/战斗界面；
  - *整局重建*——必要时从 SerializableRun 完整重建并纠正落点（地图屏 / 保留当前房间）
- **错误弹窗守卫**（v0.9.37 起）：重连流程进行中抑制原版 `NErrorPopup` 错误弹窗，避免 vanilla 文案盖住重连进度、打断 JoinFlow 收尾；不在重连流程时原版行为一律放行
- **重连后状态就绪门闩**（v0.9.37 起）：重连成功后按 `主菜单已退出 → 对局节点就绪 → RunManager.IsInProgress →（战斗中）CombatManager/手牌` 顺序等待对局就绪，回执准确
- **重连浮层**：实时状态、尝试次数、手动重试、返回主菜单
- **主机侧能力**：掉线通知 + 暂停菜单「邀请重连」按钮（经 Steam 邀请面板把掉线队友拉回大厅）
- **跨游戏版本兼容**：同一 DLL 兼容 0.107.1（正式版）与 0.111.0（beta）网络层 API 差异（监听器经 DispatchProxy 按方法名分发）
- **诊断日志**：带时间戳的滚动归档，便于联机问题定位
- **最小侵入**：所有补丁仅在重连流程进行中改变行为；未安装本 mod 时游戏表现完全不变

## 依赖

| 依赖                                                                                 | Workshop ID | 说明               |
| ---------------------------------------------------------------------------------- | ----------- | ---------------- |
| [STS2-RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) | 3747602295  | 设置界面、补丁框架、生命周期事件 |
| [JmcModLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747526103)     | 3747526103  | 暂停菜单按钮注入         |

游戏版本要求：**Slay the Spire 2 ≥ 0.111.0**。

## 安装

**推荐（Steam 创意工坊）**：订阅 [断线重连 | AutoReconnectMinCore](https://steamcommunity.com/sharedfiles/filedetails/?id=3783056249)（物品 3783056249，公开）并订阅上述两个依赖，Steam 自动下载更新。

**手动安装**：将 `AutoReconnectMinCore.dll` 与 `AutoReconnectMinCore.json`（由 `mod_manifest.json` 改名）放入游戏目录 `mods/AutoReconnectMinCore/`，并自备上述两个依赖。

## 使用

安装后无需任何配置：

- 客机掉线 → 浮层自动出现并开始重连，成功后自动回到对局；
- 重连失败 → 浮层提供「重试重连」与「返回主菜单」；
- 房主 → 暂停菜单点「邀请重连」，经 Steam 邀请面板拉回掉线队友；
- 设置项经 RitsuLib 设置界面访问。

## 从源码构建

前置：.NET 9 SDK、已安装的游戏本体（编译需引用游戏 `sts2.dll` / `0Harmony.dll` / `Steamworks.NET.dll`）。

```bash
dotnet build -c Release
```

默认引用 `D:\Steam\steamapps\common\Slay the Spire 2`，可用环境变量覆盖：

```bash
STS2_GAME_DIR="D:/Steam/steamapps/common/Slay the Spire 2" dotnet build -c Release
```

产物 `AutoReconnectMinCore.dll` 会自动复制到项目根。

## 仓库结构

```
├── AutoReconnectCore.csproj      # Core mod 项目（Godot.NET.Sdk 4.5.1 / net9.0）
├── Scripts/                      # mod 源码
│   ├── CoreEntry.cs              #   入口（ModInitializer）
│   ├── CoreBootstrap.cs          #   [ModuleInitializer] 早期引导
│   ├── ReconnectPatcher.cs       #   掉线检测与补丁（客机/主机分流、原因分类）
│   ├── ReconnectRunner.cs        #   重连编排（快/慢双路径 + 就绪门闩）
│   ├── ReconnectService.cs       #   重连服务层
│   ├── MenuRejoin/               #   菜单重连流（零重建重锚、落点纠正、DispatchProxy 跨版本大厅监听器）
│   ├── Net/                      #   网络层补丁（错误弹窗守卫、抑制包装器等）
│   ├── Reload/                   #   场景重载守卫
│   ├── ReconnectOverlay.cs       #   重连浮层 UI
│   ├── ReconnectDiagnostics.cs   #   诊断弹窗与归因
│   ├── CoreModConfigBridge.cs    #   RitsuLib 设置桥接
│   ├── PauseMenuInviteButton.cs  #   暂停菜单「邀请重连」按钮（JmcModLib）
│   ├── ClientRunPreserver.cs     #   客机活对局保留
│   ├── ClientStateGuard.cs       #   客机状态守卫
│   ├── HostInfoTracker.cs        #   房主信息与重连状态跟踪
│   ├── QuestionRoomResolvePatch.cs    # 问号房间类型解析修正
│   ├── TreasureRoomHandNullGuard.cs   # 宝藏房间手牌空指防御
│   └── ReflectionHelper.cs / Diag.cs  # 反射工具 / 日志
├── deploy/AutoReconnectMinCore/  # 分发目录（latest + 各版本归档）
├── deploy_core.sh                # 构建 + 分发 + 归档脚本
├── lib/                          # 编译期第三方引用（STS2-RitsuLib.dll）
├── tools/
│   ├── sts2sig/                  # 自研 sts2.dll 元数据索引工具（见下）
│   ├── make_preview.py           # 工坊预览图生成脚本
│   └── workshop.json             # 工坊上传配置参考副本
├── docs/mod-ecosystem-research.md  # 社区 mod 生态研究知识库（12 款学习/借鉴汇总）
├── docs/research/                  # 四份详细研究报告（生态学习笔记）
└── mod_manifest.json             # mod 清单
```

## 开发工具：sts2sig

`tools/sts2sig/` 是本仓库自研的 sts2.dll 元数据索引工具（net9.0、零 NuGet 依赖、System.Reflection.Metadata 流式读元数据，秒级完成），用来解决 ilspycmd `-t` 必须猜精确全类型名的痛点：

```bash
tools/sts2sig/bin/Release/net9.0/sts2sig.exe index -o sts2_types.txt   # 全量类型索引（可 grep）
tools/sts2sig/bin/Release/net9.0/sts2sig.exe find LoadRunLobbyPlayer   # 类型名子串 → 完整成员签名
tools/sts2sig/bin/Release/net9.0/sts2sig.exe member Rejoin             # 跨全部类型的成员名搜索
```

API 探查三层工作流：grep `sts2.xml` → `sts2sig` → ilspycmd（需要方法体时）。

## 相关知识库

- [`docs/mod-ecosystem-research.md`](docs/mod-ecosystem-research.md)：对社区 mod 的实现学习借鉴汇总。
- 各 mod 按「仓库、Steam创意工坊、作者」标注来源。

## 致谢

- [Mega Crit](https://github.com/megacrit) 官方 mod 生态与 [ModUploader](https://github.com/megacrit/sts2-mod-uploader)
- [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)、[JmcModLib](https://github.com/JMC-Mods/SlayTheSpire2_JmcModLib)、[BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2)
- QuickLink / Rewind 等同业 mod 的设计思路（详见知识库文档）
