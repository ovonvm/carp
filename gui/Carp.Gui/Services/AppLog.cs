using System.Globalization;
using System.IO;
using System.Text;

namespace Carp.Gui.Services;

/// <summary>
/// GUI 落盘日志：<c>&lt;exeDir&gt;\conf\logs\gui-yyyyMMdd.log</c>，按天切分，最多保留 7 天。
/// exe 目录不可写时自动回退到 %LOCALAPPDATA%\Carp\conf\logs。
/// </summary>
public static class AppLog
{
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static readonly string LogDirectory = ResolveDirectory();
    private static DateTime lastPruneDay = DateTime.MinValue;

    public static string Directory => LogDirectory;

    public static string CurrentFile => Path.Combine(LogDirectory, $"gui-{DateTime.Now:yyyyMMdd}.log");

    private static string ResolveDirectory()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(GuiConfig.ConfDir, "logs"),
                     Path.Combine(GuiConfig.LocalRoot, "conf", "logs"),
                 })
        {
            try
            {
                System.IO.Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".carp-log-test");
                File.AppendAllText(probe, "1");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // 尝试下一个位置。
            }
        }
        var fallback = Path.Combine(Path.GetTempPath(), "Carp", "logs");
        try
        {
            System.IO.Directory.CreateDirectory(fallback);
        }
        catch
        {
            // 忽略：日志不可用时不应影响 GUI。
        }
        return fallback;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        lock (Gate)
        {
            try
            {
                var now = DateTime.Now;
                System.IO.Directory.CreateDirectory(LogDirectory);
                var line = $"{now:HH:mm:ss} [{level}] {message.Replace("\r", " ").Replace("\n", " ")}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                if (lastPruneDay != now.Date)
                {
                    lastPruneDay = now.Date;
                    Prune(now);
                }
            }
            catch
            {
                // 日志失败不影响主流程。
            }
        }
    }

    private static void Prune(DateTime now)
    {
        try
        {
            // 核心（carp serve）也把日志写进同一目录，所以按修改时间清理所有
            // *.log，而不是只认 gui- 前缀，否则核心日志会一直堆积。
            foreach (var file in System.IO.Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                if ((now - File.GetLastWriteTime(file)).TotalDays >= KeepDays)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败忽略。
        }
    }
}
