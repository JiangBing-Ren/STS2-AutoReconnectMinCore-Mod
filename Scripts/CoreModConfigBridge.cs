// =============================================================================
// CoreModConfigBridge.cs -- AutoReconnectMinCore 设置界面（基于 RitsuLib RegisterModSettings）
// =============================================================================
// 使用 RitsuLib 的 ModSettings API 注册 Core 的 3 个配置项：
//   - autoReconnectEnabled  (bool,  默认 true)
//   - showClientResultPopup (bool,  默认 true)
//   - showHostEventPopup    (bool,  默认 true)
//
// 持久化：使用 ModSettingsCallbackValueBinding 绑定到静态字段，
//         save 回调触发延迟 JSON 写入（user://ModConfig/AutoReconnectMinCore.json）。
//
// Main mod (AutoReconnectMin) 有自己的设置注册，两者注册在不同的 mod ID 下，互不干扰。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using FileAccess = Godot.FileAccess;

namespace AutoReconnect.Scripts;

internal static class CoreModConfigBridge
{
    private const string ModId = "AutoReconnectMinCore";
    private const string ConfigDir = "user://ModConfig/";
    private const string ConfigPath = "user://ModConfig/AutoReconnectMinCore.json";

    private static bool _registered;
    private static bool _saveScheduled;

    internal static void DeferredRegister()
    {
        try
        {
            // 先加载已持久化的设置
            TryLoadSettings();

            // 延迟到下一帧注册（RitsuLib 可能尚未完成初始化）
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.ProcessFrame += OnNextFrame;
            else
                Register();
        }
        catch (Exception ex)
        {
            Diag.Log($"[Core] RitsuLib 设置延迟注册失败：{ex}");
        }
    }

    private static void OnNextFrame()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.ProcessFrame -= OnNextFrame;
        }
        catch { }
        Register();
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            RitsuLibFramework.RegisterModSettings(ModId, page =>
            {
                page.WithTitle(ModSettingsText.Literal("自动重连(核心)"))
                    .WithModDisplayName(ModSettingsText.Literal("Auto Reconnect Min Core"))
                    .AddSection("reconnect", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("重连设置"));

                        // autoReconnectEnabled
                        section.AddToggle(
                            "autoReconnectEnabled",
                            ModSettingsText.Literal("自动重连"),
                            new DefaultModSettingsValueBinding<bool>(
                                new ModSettingsCallbackValueBinding<bool>(
                                    ModId, "autoReconnectEnabled", SaveScope.Global,
                                    () => ReconnectService.AutoReconnectEnabled,
                                    v => ReconnectService.AutoReconnectEnabled = v,
                                    ScheduleSave),
                                () => true),
                            ModSettingsText.Literal("掉线后是否自动尝试重连回同一大厅。关闭后掉线即回主菜单。"));

                        // showClientResultPopup
                        section.AddToggle(
                            "showClientResultPopup",
                            ModSettingsText.Literal("显示重连结果"),
                            new DefaultModSettingsValueBinding<bool>(
                                new ModSettingsCallbackValueBinding<bool>(
                                    ModId, "showClientResultPopup", SaveScope.Global,
                                    () => ReconnectDiagnostics.ShowClientPopup,
                                    v => ReconnectDiagnostics.ShowClientPopup = v,
                                    ScheduleSave),
                                () => true),
                            ModSettingsText.Literal("客机重连成功/失败时是否弹窗提示。"));

                        // showHostEventPopup
                        section.AddToggle(
                            "showHostEventPopup",
                            ModSettingsText.Literal("显示主机事件"),
                            new DefaultModSettingsValueBinding<bool>(
                                new ModSettingsCallbackValueBinding<bool>(
                                    ModId, "showHostEventPopup", SaveScope.Global,
                                    () => ReconnectDiagnostics.ShowHostPopup,
                                    v => ReconnectDiagnostics.ShowHostPopup = v,
                                    ScheduleSave),
                                () => true),
                            ModSettingsText.Literal("主机是否弹窗提示队友掉线/重连事件。"));
                    });
            });

            Diag.Log("[Core] 已向 RitsuLib 注册设置页（设置 -> Mods -> Auto Reconnect Min Core）。");
        }
        catch (Exception e)
        {
            Diag.Log($"[Core] RitsuLib 设置注册失败：{e}");
        }
    }

    // --- 持久化（JSON at user://ModConfig/AutoReconnectMinCore.json） ---

    private static void TryLoadSettings()
    {
        if (!FileAccess.FileExists(ConfigPath)) return;

        try
        {
            using var file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Read);
            if (file == null) return;

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(file.GetAsText());
            if (dict == null) return;

            if (dict.TryGetValue("autoReconnectEnabled", out var v))
                ReconnectService.AutoReconnectEnabled = v.GetBoolean();
            if (dict.TryGetValue("showClientResultPopup", out v))
                ReconnectDiagnostics.ShowClientPopup = v.GetBoolean();
            if (dict.TryGetValue("showHostEventPopup", out v))
                ReconnectDiagnostics.ShowHostPopup = v.GetBoolean();

            Diag.Log("[Core] 已从本地 JSON 加载设置。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Core] 设置 JSON 加载失败（已忽略）：{ex.Message}");
        }
    }

    private static void ScheduleSave()
    {
        if (_saveScheduled) return;
        _saveScheduled = true;

        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame += FlushSave;
        else
            FlushSave();
    }

    private static void FlushSave()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame -= FlushSave;
        _saveScheduled = false;

        try
        {
            if (!DirAccess.DirExistsAbsolute(ConfigDir))
                DirAccess.MakeDirRecursiveAbsolute(ConfigDir);

            var dict = new Dictionary<string, object>
            {
                ["autoReconnectEnabled"] = ReconnectService.AutoReconnectEnabled,
                ["showClientResultPopup"] = ReconnectDiagnostics.ShowClientPopup,
                ["showHostEventPopup"] = ReconnectDiagnostics.ShowHostPopup,
            };

            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });

            using var file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Write);
            file?.StoreString(json);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Core] 设置 JSON 保存失败（已忽略）：{ex.Message}");
        }
    }
}
