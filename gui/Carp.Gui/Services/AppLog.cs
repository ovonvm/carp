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
            foreach (var file in System.IO.Directory.EnumerateFiles(LogDirectory, "gui-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != 12 ||
                    !DateTime.TryParseExact(name[4..], "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day))
                {
                    continue;
                }
                if ((now.Date - day.Date).TotalDays >= KeepDays)
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
