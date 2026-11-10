using System.IO;
using System.Text.Json;

namespace Carp.Gui.Services;

/// <summary>
/// GUI 配置。数据目录默认就是 Carp 程序所在目录（便携式布局），
/// 配置与备份记录放在 %APPDATA%\Carp\gui.json；exe 目录不可写时可回退到 %LOCALAPPDATA%\Carp。
/// </summary>
public sealed class GuiConfig
{
    public string CorePath { get; set; } = "";
    public int Port { get; set; } = 50011;
    public string DataDir { get; set; } = "";
    public string Profile { get; set; } = "low";
    public string LogLevel { get; set; } = "info";
    public bool CookieSecure { get; set; }
    public bool AutoStart { get; set; }

    /// <summary>最近一次备份的输出目录（下次选择目录时的初始位置）。</summary>
    public string LastBackupDir { get; set; } = "";

    /// <summary>最近一次备份生成的 zip 完整路径。</summary>
    public string LastBackupPath { get; set; } = "";

    /// <summary>最近一次备份完成时间（yyyy-MM-dd HH:mm）。</summary>
    public string LastBackupTime { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Carp 程序（GUI exe）所在目录，同时是默认数据目录。</summary>
    public static string ExeDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>%APPDATA%\Carp，存放 gui.json。</summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Carp");

    /// <summary>%LOCALAPPDATA%\Carp，exe 目录不可写时的数据目录回退位置。</summary>
    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Carp");

    public static string ConfigPath => Path.Combine(UserRoot, "gui.json");

    public static string DefaultDataDir => ExeDir;

    public static string LocalDataDir => LocalRoot;

    /// <summary>
    /// 内嵌核心的释放目录：默认放在 exe 旁边的 core/，便携包把整个目录搬走即可。
    /// 如果 exe 目录不可写（例如解压到 Program Files），自动回退到用户目录。
    /// </summary>
    public static string LocalCoreDir
    {
        get
        {
            var portable = Path.Combine(ExeDir, "core");
            try
            {
                Directory.CreateDirectory(portable);
                return portable;
            }
            catch (UnauthorizedAccessException)
            {
                return Path.Combine(LocalRoot, "core");
            }
            catch (IOException)
            {
                return Path.Combine(LocalRoot, "core");
            }
        }
    }

    /// <summary>没有记住上次备份位置时的默认备份目录。</summary>
    public static string BackupFallbackDir => Path.Combine(LocalRoot, "backups");

    public static GuiConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<GuiConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值。
        }
        var fresh = new GuiConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DataDir))
        {
            DataDir = DefaultDataDir;
        }
        DataDir = DataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(DataDir))
        {
            DataDir = DefaultDataDir;
        }
        if (Port is < 1 or > 65535)
        {
            Port = 50011;
        }
        if (!string.Equals(Profile, "low", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Profile, "high", StringComparison.OrdinalIgnoreCase))
        {
            Profile = "low";
        }
        Profile = Profile.ToLowerInvariant();
        if (LogLevel is not ("debug" or "info" or "warn" or "error"))
        {
            LogLevel = "info";
        }
        if (string.IsNullOrWhiteSpace(LastBackupDir))
        {
            LastBackupDir = "";
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(UserRoot);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
