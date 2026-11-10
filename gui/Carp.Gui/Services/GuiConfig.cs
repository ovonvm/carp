using System.IO;
using System.Text.Json;

namespace Carp.Gui.Services;

/// <summary>
/// GUI 配置。便携式布局：Carp.exe 旁边固定三样东西——
/// <c>data\</c>（核心数据）、<c>conf\</c>（gui.json、日志、备份）、<c>core\</c>（释放的 carp-core.exe）。
/// 如果 exe 目录不可写（例如解压到 Program Files），整棵树回退到 %LOCALAPPDATA%\Carp。
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

    /// <summary>Carp 程序（GUI exe）所在目录；其下固定有 data\、conf\、core\。</summary>
    public static string ExeDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>%LOCALAPPDATA%\Carp，exe 目录不可写时的整棵树回退位置。</summary>
    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Carp");

    private static string? confDir;

    /// <summary>conf 目录：gui.json、日志、备份。exe 目录不可写时回退到 %LOCALAPPDATA%\Carp\conf。</summary>
    public static string ConfDir => confDir ??= ResolveWritableDir(
        Path.Combine(ExeDir, "conf"),
        Path.Combine(LocalRoot, "conf"));

    public static string ConfigPath => Path.Combine(ConfDir, "gui.json");

    /// <summary>默认数据目录：&lt;exe&gt;\data。</summary>
    public static string DefaultDataDir => Path.Combine(ExeDir, "data");

    /// <summary>回退数据目录：%LOCALAPPDATA%\Carp\data。</summary>
    public static string LocalDataDir => Path.Combine(LocalRoot, "data");

    /// <summary>
    /// 内嵌核心的释放目录：&lt;exe&gt;\core，便携包把整个目录搬走即可。
    /// exe 目录不可写时回退到用户目录。
    /// </summary>
    public static string LocalCoreDir => ResolveWritableDir(
        Path.Combine(ExeDir, "core"),
        Path.Combine(LocalRoot, "core"));

    /// <summary>没有记住上次备份位置时的默认备份目录。</summary>
    public static string BackupFallbackDir => Path.Combine(ConfDir, "backups");

    /// <summary>依次尝试创建并写入探测文件，返回第一个可写目录（都不可写时返回首选）。</summary>
    private static string ResolveWritableDir(string preferred, string fallback)
    {
        foreach (var candidate in new[] { preferred, fallback })
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".carp-write-test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return candidate;
            }
            catch (UnauthorizedAccessException)
            {
                // 尝试下一个位置。
            }
            catch (IOException)
            {
                // 尝试下一个位置。
            }
        }
        return preferred;
    }

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
        try
        {
            // 首次运行就把默认配置落盘：关于页里的 conf\gui.json 真实存在，用户也能直接改。
            fresh.Save();
        }
        catch
        {
            // 写不了配置不影响启动，下次保存设置时再写。
        }
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
        Directory.CreateDirectory(ConfDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
