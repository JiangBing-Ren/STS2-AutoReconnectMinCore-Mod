using System;
using System.IO;
using System.Linq;

namespace AutoReconnect.Scripts;

/// <summary>
/// File-based diagnostic logger, self-initializing via ModuleInitializer.
/// Logs to: %APPDATA%/SlayTheSpire2/mods/{LogModName}/logs/{logfile}.log
/// 文件名由 LogModName 派生（ToLowerInvariant）：
///   - Core 默认 LogModName="AutoReconnectMinCore" → 日志 autoreconnectmincore.log
///   - Main 覆盖 LogModName="AutoReconnectMin"    → 日志 autoreconnectmin.log
/// 这样两个 mod 的日志文件名天然不同，互不混淆。
/// 滚动与上限：仿官方 godot.log——新会话把旧日志改名带时间戳留存，目录内最多保留 5 份。
/// </summary>
public static class Diag
{
    private static string? _logPath;
    private static readonly object _lock = new();

    /// <summary>
    /// 日志目录名（默认 "AutoReconnectMinCore"），同时决定日志文件名：
    /// 文件名 = LogModName.ToLowerInvariant() + ".log"。
    /// Main 模组在 Bootstrap 里覆盖为 "AutoReconnectMin" 并调 Reinit() 切换日志位置与文件名。
    /// </summary>
    public static string LogModName { get; set; } = "AutoReconnectMinCore";

    /// <summary>
    /// 重置日志路径并重新初始化（Main 模组覆盖 LogModName 后调用以切换日志目录）。
    /// </summary>
    public static void Reinit()
    {
        _logPath = null;
        Init();
    }

    public static void Init()
    {
        if (_logPath != null) return;

        try
        {
            // Path convention matching CardDisableControl:
            // %APPDATA%/Roaming/SlayTheSpire2/mods/{ModName}/logs/{file}.log
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "mods", LogModName, "logs");

            Directory.CreateDirectory(baseDir);

            var baseName = LogModName.ToLowerInvariant();
            var currentPath = Path.Combine(baseDir, baseName + ".log");

            // 滚动归档（与官方 godot.log 行为一致）：每次新启动，先把上一会话的
            // {baseName}.log 重命名为带时间戳的文件，历史全部保留、绝不覆盖。
            // 时间戳取上一会话日志的最后写入时间（即该会话结束时刻），格式 yyyy-MM-ddTHH.mm.ss
            // （Windows 文件名禁用冒号，时间用点分隔，对齐官方 godot<timestamp>.log）。
            if (File.Exists(currentPath))
            {
                try
                {
                    var stamp = File.GetLastWriteTime(currentPath)
                        .ToString("yyyy-MM-ddTHH.mm.ss");
                    var archived = Path.Combine(baseDir, $"{baseName}{stamp}.log");
                    if (File.Exists(archived))
                        archived = Path.Combine(baseDir,
                            $"{baseName}{stamp}_{Guid.NewGuid():N}.log");
                    File.Move(currentPath, archived);
                }
                catch (Exception ex)
                {
                    // 注意：此处用裸 Console 兜底，避免 Log()→Init() 重入导致死循环。
                    try { Console.Error.WriteLine($"[Diag] 日志滚动归档失败（忽略）：{ex.Message}"); }
                    catch { }
                }
            }

            // 仿官方 godot.log：日志目录内最多只保留 5 份（当前 + 历史），超出删除最旧的。
            try
            {
                EnforceLogRetention(baseDir, baseName);
            }
            catch { }

            _logPath = currentPath;
            Log("Diag init OK");
        }
        catch (Exception ex)
        {
            // Last resort: try writing next to the game exe
            try
            {
                var fallback = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "autoreconnect.log");
                _logPath = fallback;
                Log($"Diag init fallback: {fallback} (primary failed: {ex.Message})");
            }
            catch
            {
                _logPath = null;
            }
        }
    }

    /// <summary>
    /// 日志目录内最多保留 MaxLogs 份日志（当前 + 历史），超出删除最旧的。
    /// 仅匹配 baseDir 下以 baseName 开头的 *.log（本 mod 专属，绝不会误删其他 mod 的日志）。
    /// </summary>
    private static void EnforceLogRetention(string baseDir, string baseName)
    {
        const int MaxLogs = 5;
        var files = Directory.GetFiles(baseDir, baseName + "*.log")
            .OrderBy(p => File.GetLastWriteTimeUtc(p))
            .ToList();
        // 升序：最旧在前、当前（刚创建，最新）在后 → 删前面的旧文件不会误删当前。
        while (files.Count > MaxLogs)
        {
            var oldest = files[0];
            files.RemoveAt(0);
            try { File.Delete(oldest); }
            catch { /* 忽略删除失败，下次启动再尝试 */ }
        }
    }

    public static void Log(string message)
    {
        try
        {
            if (_logPath == null)
            {
                Init();
                if (_logPath == null) return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            lock (_lock)
            {
                File.AppendAllText(_logPath, $"[{timestamp}] {message}\n");
            }
        }
        catch { }
    }
}
