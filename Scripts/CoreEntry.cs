using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using JmcModLib.Core;
using MegaCrit.Sts2.Core.Modding;

namespace AutoReconnect.Scripts;

/// <summary>
/// Core mod 入口——只负责重连相关的 Harmony 补丁挂载和 Core 配置注册。
/// 不含检查点/托管/战斗同步（那些由 AutoReconnectMin Main 负责）。
/// </summary>
[ModInitializer(nameof(Init))]
public class CoreEntry
{
    private static Harmony? _harmony;
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized)
        {
            Diag.Log("CoreEntry.Init() 重复调用，已忽略。");
            return;
        }
        _initialized = true;

        Diag.Init();
        Diag.Log("============================================");
        Diag.Log("AutoReconnectMinCore v0.9.37-core (Reconnect Only) CoreEntry.Init() 开始");
        Diag.Log("============================================");

        // 向 JML ModRegistry 注册 Core——触发 AttributeRouter 扫描本程序集，
        // 发现 [PauseMenuButton] 等 attribute 并注册到 PauseMenuRegistry。
        // 必须在 Harmony 挂载之前调用（AttributeRouter 扫描不依赖 Harmony）。
        try
        {
            ModRegistry.Register<CoreEntry>();
            Diag.Log("[Core] 已向 JML ModRegistry 注册（attribute 扫描已触发）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Core] JML ModRegistry 注册失败（暂停菜单按钮将不可用）：{ex.GetType().Name}: {ex.Message}");
        }

        HostInfoTracker.Reset();

        // 向 RitsuLib 注册 Core 设置界面（含持久化加载）
        try
        {
            CoreModConfigBridge.DeferredRegister();
        }
        catch (Exception ex)
        {
            Diag.Log($"Core RitsuLib 设置注册触发异常（已忽略）：{ex}");
        }

        // 挂载 Core 的 Harmony 补丁（重连 + 过渡守卫 + 宝藏房防护）
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            _harmony = new Harmony("sts2.autoreconnectmin.core");

            int ok = 0, failed = 0, methods = 0;
            foreach (var type in asm.GetTypes())
            {
                if (type.GetCustomAttribute<HarmonyPatch>() == null) continue;

                try
                {
                    var patched = _harmony.CreateClassProcessor(type).Patch();
                    if (patched is { Count: > 0 })
                    {
                        ok++;
                        methods += patched.Count;
                    }
                    else
                    {
                        Diag.Log($"  跳过补丁类 {type.Name}（无生效目标）");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Diag.Log($"  补丁类 {type.Name} 挂载失败：{ex.InnerException?.Message ?? ex.Message}");
                }
            }

            Diag.Log($"Core Harmony 挂载完成：成功 {ok} 类 / 失败 {failed} 类 / 共 {methods} 个目标方法");
        }
        catch (Exception ex)
        {
            Diag.Log($"Core Harmony 挂载整体失败：{ex}");
        }

        Diag.Log($"自动重连：AutoReconnectEnabled={ReconnectService.AutoReconnectEnabled}");
        Diag.Log("AutoReconnectMinCore v0.9.37-core (Reconnect Only) 初始化完成。");
    }
}
