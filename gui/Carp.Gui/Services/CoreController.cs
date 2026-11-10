using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Carp.Gui.Services;

/// <summary>
/// 常驻 Carp 核心控制器：以 <c>serve -gui-control</c> 拉起子进程，通过 stdin 发送
/// <c>START / STOP / STATUS / SHUTDOWN</c>，通过 stdout 的 JSON 行更新 <see cref="State"/>。
/// </summary>
public sealed class CoreController : IDisposable
{
    public const int MaxLogLines = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>去掉单文件发布解压目录差异后的“程序所在目录”，其下有 data\、conf\、core\。</summary>
    public static string Root => AppContext.BaseDirectory;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly object logGate = new();
    private readonly List<string> logLines = new(MaxLogLines + 8);
    private readonly SemaphoreSlim stdinLock = new(1, 1);

    private Process? process;
    private bool ready;
    private bool disposed;
    private string addr = "";
    private string? lastError;
    private string? resolvedCoreExe;

    public CoreController(GuiConfig config)
    {
        Config = config;
    }

    public GuiConfig Config { get; }

    public string DataDir => Config.DataDir;

    /// <summary>核心可执行文件路径（懒解析，首次访问时决定）。</summary>
    public string CoreExe => resolvedCoreExe ??= ResolveCoreExe();

    public string Addr => string.IsNullOrWhiteSpace(addr) ? $"127.0.0.1:{Config.Port}" : addr;

    public string AppUrl => $"http://{Addr}/";

    public string BaseUrl => $"http://127.0.0.1:{Config.Port}/";

    /// <summary>核心控制进程是否还活着。</summary>
    public bool IsLaunched
    {
        get
        {
            var p = process;
            try
            {
                return p != null && !p.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsReady => ready;

    public AppState State { get; private set; } = AppState.Stopped;

    public string? LastError => lastError;

    public DateTime? RunningSince { get; private set; }

    public int LogCount
    {
        get
        {
            lock (logGate)
            {
                return logLines.Count;
            }
        }
    }

    public event Action? StateChanged;

    public event Action? LogChanged;

    /// <summary>核心通过 error 事件报告的错误（已格式化，可直接弹窗）。</summary>
    public event Action<string>? CoreError;

    // ---------------------------------------------------------------- 进程生命周期

    /// <summary>
    /// 确保核心可执行文件可用：优先使用内嵌 payload（单文件发布），
    /// 开发态回退到仓库 <c>dist/core/carp.exe</c>，最终统一释放到
    /// <c>%LOCALAPPDATA%\Carp\core\carp-core.exe</c>。
    /// </summary>
    public (bool Ok, string Error) EnsureCore()
    {
        try
        {
        if (!string.IsNullOrWhiteSpace(Config.CorePath))
        {
            var explicitCore = Path.IsPathRooted(Config.CorePath)
                ? Config.CorePath
                : Path.Combine(Root, Config.CorePath);
            if (File.Exists(explicitCore))
            {
                resolvedCoreExe = explicitCore;
                return (true, "");
            }
            return (false, "配置中指定的核心程序不存在：" + explicitCore);
        }

        var failures = new List<string>();
        var resource = typeof(CoreController).Assembly
            .GetManifestResourceStream("Carp.Gui.payload.carp-core.exe");
        if (resource != null)
        {
            byte[] payload;
            using (resource)
            using (var buffer = new MemoryStream())
            {
                resource.CopyTo(buffer);
                payload = buffer.ToArray();
            }
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            foreach (var directory in CoreTargetDirs())
            {
                var target = Path.Combine(directory, "carp-core.exe");
                var hashFile = target + ".sha256";
                try
                {
                    // 只比文件大小会漏掉“同尺寸重新编译”的核心（每次构建字节数几乎一样），
                    // 升级后会继续跑旧核心、GUI 误报“版本不一致”。改为比对内容哈希。
                    if (File.Exists(target) && File.Exists(hashFile) &&
                        string.Equals(File.ReadAllText(hashFile).Trim(), payloadHash, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedCoreExe = target;
                        return (true, "");
                    }
                    Directory.CreateDirectory(directory);
                    var tmp = target + ".tmp";
                    File.WriteAllBytes(tmp, payload);
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    File.Move(tmp, target);
                    File.WriteAllText(hashFile, payloadHash);
                    AppendLog("[GUI] 已释放内嵌核心：" + target);
                    resolvedCoreExe = target;
                    return (true, "");
                }
                catch (Exception ex)
                {
                    failures.Add(directory + "：" + ex.Message);
                }
            }
            return (false, "无法释放内嵌核心：" + string.Join("；", failures));
        }

        // 开发态：从仓库构建产物复制。
        foreach (var candidate in DevCoreCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            foreach (var directory in CoreTargetDirs())
            {
                var target = Path.Combine(directory, "carp-core.exe");
                try
                {
                    var needsCopy = !File.Exists(target) ||
                                    File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(target);
                    if (needsCopy)
                    {
                        Directory.CreateDirectory(directory);
                        File.Copy(candidate, target, true);
                        AppendLog("[GUI] 已复制开发态核心：" + candidate);
                    }
                    resolvedCoreExe = target;
                    return (true, "");
                }
                catch (Exception ex)
                {
                    failures.Add(directory + "：" + ex.Message);
                }
            }

            // 所有释放位置都不可写时，直接运行开发态核心。
            resolvedCoreExe = Path.GetFullPath(candidate);
            AppendLog("[GUI] 无法复制核心，直接使用：" + resolvedCoreExe);
            return (true, "");
        }

        return (false, "未找到核心程序 carp-core.exe。请使用完整安装包，或先运行 scripts/build-all.sh 生成 dist/core/carp.exe。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, $"准备核心程序失败：没有权限写入 {GuiConfig.LocalCoreDir}。请把 Carp 解压到可写目录（如桌面或 D 盘）后重试，或以管理员身份运行安装包。\n{ex.Message}");
        }
        catch (IOException ex)
        {
            return (false, $"准备核心程序失败：{ex.Message}\n请确认程序目录可写，或重新解压完整安装包。");
        }
        catch (Exception ex)
        {
            return (false, "准备核心程序失败：" + ex.Message);
        }
    }

    private static IEnumerable<string> DevCoreCandidates()
    {
        var root = Root;
        yield return Path.Combine(root, "..", "..", "..", "..", "..", "dist", "core", "carp.exe");
        yield return Path.Combine(root, "..", "..", "..", "..", "dist", "carp.exe");
        yield return Path.Combine(root, "..", "..", "..", "..", "..", "dist", "carp.exe");
        yield return Path.Combine(GuiConfig.LocalRoot, "carp.exe");
    }

    /// <summary>核心可执行文件的释放位置：用户目录优先，其次是程序目录与临时目录。</summary>
    private static IEnumerable<string> CoreTargetDirs()
    {
        yield return GuiConfig.LocalCoreDir;
        yield return Path.Combine(Root, "core");
        yield return Path.Combine(Path.GetTempPath(), "Carp", "core");
    }

    private string ResolveCoreExe()
    {
        if (!string.IsNullOrWhiteSpace(Config.CorePath))
        {
            return Path.IsPathRooted(Config.CorePath) ? Config.CorePath : Path.Combine(Root, Config.CorePath);
        }
        foreach (var directory in CoreTargetDirs())
        {
            var local = Path.Combine(directory, "carp-core.exe");
            if (File.Exists(local))
            {
                return local;
            }
        }
        foreach (var candidate in DevCoreCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return Path.Combine(GuiConfig.LocalCoreDir, "carp-core.exe");
    }

    /// <summary>启动常驻核心并等待 ready 事件（最长 25 秒）。</summary>
    public async Task<(bool Ok, string Message)> LaunchAsync()
    {
        if (IsLaunched)
        {
            return (true, "");
        }
        var exe = CoreExe;
        if (!File.Exists(exe))
        {
            return (false, "找不到核心程序：" + exe);
        }

        if (process != null)
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略。
            }
            process = null;
        }

        ready = false;
        lastError = null;
        addr = "";

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = BuildArguments(),
            WorkingDirectory = DataDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 核心的日志目录也指到 conf\logs，data\ 里只留数据。
        psi.Environment["CARP_LOG_DIR"] = Path.Combine(GuiConfig.ConfDir, "logs");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                HandleStdout(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                HandleStderr(e.Data);
            }
        };
        proc.Exited += (_, _) => HandleExited();

        try
        {
            if (!proc.Start())
            {
                return (false, "无法启动核心进程。");
            }
        }
        catch (Exception ex)
        {
            return (false, "无法启动核心进程：" + ex.Message);
        }

        process = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        AppendLog("[GUI] 核心已启动，等待就绪…");
        RaiseState();

        var ok = await WaitUntilAsync(() => ready || !IsLaunched, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
        if (ready)
        {
            AppendLog("[GUI] 核心就绪");
            return (true, "");
        }
        if (!IsLaunched)
        {
            return (false, string.IsNullOrWhiteSpace(lastError)
                ? "核心进程启动后立即退出，请查看运行日志。"
                : lastError!);
        }
        return (false, "核心启动超时（25 秒），请查看运行日志。");
    }

    private string BuildArguments()
    {
        var sb = new StringBuilder();
        sb.Append("serve -gui-control -data \"").Append(DataDir).Append('"');
        sb.Append(" -addr 127.0.0.1:").Append(Config.Port);
        sb.Append(" -profile ").Append(Config.Profile);
        sb.Append(" -log-level ").Append(Config.LogLevel);
        if (Config.CookieSecure)
        {
            sb.Append(" -cookie-secure always");
        }
        return sb.ToString();
    }

    /// <summary>发送 START，并等待 state=running（调用方给定超时，默认 25 秒）。</summary>
    public async Task<(bool Ok, string Message)> StartAsync(TimeSpan timeout)
    {
        if (!IsLaunched)
        {
            return (false, "核心进程未运行，无法启动服务。");
        }
        if (State == AppState.Running)
        {
            return (true, "");
        }

        lastError = null;
        SetState(AppState.Starting);
        if (!await SendAsync("START").ConfigureAwait(false))
        {
            SetState(AppState.Stopped);
            return (false, "无法向核心发送 START 命令。");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && State is AppState.Starting)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (State == AppState.Running)
        {
            return (true, "");
        }
        if (State == AppState.Error)
        {
            return (false, lastError ?? "核心报告启动失败。");
        }
        if (State == AppState.Stopped)
        {
            return (false, lastError ?? "服务未能启动，核心已回到停止状态。");
        }
        SetState(AppState.Stopped);
        return (false, $"启动超时（{timeout.TotalSeconds:0} 秒），请查看运行日志。");
    }

    /// <summary>发送 STOP，并等待 state=stopped（默认 15 秒）。</summary>
    public async Task<(bool Ok, string Message)> StopAsync(TimeSpan timeout)
    {
        if (!IsLaunched || State == AppState.Stopped)
        {
            SetState(AppState.Stopped);
            return (true, "");
        }

        SetState(AppState.Stopping);
        if (!await SendAsync("STOP").ConfigureAwait(false))
        {
            SetState(AppState.Stopped);
            return (false, "无法向核心发送 STOP 命令。");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && State is AppState.Stopping or AppState.Running)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (State == AppState.Stopped)
        {
            return (true, "");
        }
        if (State == AppState.Error)
        {
            return (false, lastError ?? "核心报告停止失败。");
        }
        SetState(AppState.Stopped);
        return (false, $"停止超时（{timeout.TotalSeconds:0} 秒），请重试或强制结束核心进程。");
    }

    /// <summary>发送 SHUTDOWN 并等待进程退出（默认 5 秒）；返回是否已退出。</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        var proc = process;
        if (proc == null)
        {
            SetState(AppState.Stopped);
            return true;
        }
        if (proc.HasExited)
        {
            SetState(AppState.Stopped);
            return true;
        }

        await SendAsync("SHUTDOWN").ConfigureAwait(false);
        var exited = await WaitUntilAsync(() =>
        {
            try
            {
                return proc.HasExited;
            }
            catch
            {
                return true;
            }
        }, timeout).ConfigureAwait(false);

        if (exited)
        {
            AppendLog("[GUI] 核心已退出");
            SetState(AppState.Stopped);
        }
        return exited;
    }

    /// <summary>强制结束核心进程（SHUTDOWN 超时后使用）。</summary>
    public void ForceKill()
    {
        var proc = process;
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                AppendLog("[GUI] 已强制结束核心进程");
            }
        }
        catch (Exception ex)
        {
            AppendLog("[GUI] 强制结束失败：" + ex.Message);
        }
        ready = false;
        SetState(AppState.Stopped);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略。
        }
        try
        {
            process?.Dispose();
        }
        catch
        {
            // 忽略。
        }
        http.Dispose();
        stdinLock.Dispose();
    }

    // ---------------------------------------------------------------- 事件处理

    private void HandleStdout(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        CoreEvent? ev = null;
        try
        {
            ev = JsonSerializer.Deserialize<CoreEvent>(line, JsonOptions);
        }
        catch
        {
            // 非 JSON：按普通日志处理。
        }

        if (ev == null || string.IsNullOrEmpty(ev.Event))
        {
            AppendLog(line);
            return;
        }

        switch (ev.Event)
        {
            case "ready":
                ready = true;
                AppendLog($"[核心] 就绪 version={ev.Version ?? "?"} data_dir={ev.DataDir ?? DataDir}");
                break;

            case "state":
                ApplyState(ev.State ?? "", ev.Addr);
                break;

            case "listening":
                ApplyState("running", ev.Addr);
                break;

            case "stopped":
                ApplyState("stopped", ev.Addr);
                break;

            case "error":
                lastError = string.IsNullOrWhiteSpace(ev.Message) ? "核心报告未知错误" : ev.Message!;
                AppendLog("[错误] " + lastError);
                RunningSince = null;
                State = AppState.Error;
                RaiseState();
                try
                {
                    CoreError?.Invoke(lastError);
                }
                catch
                {
                    // 忽略订阅方异常。
                }
                break;

            case "log":
                AppendLog($"[{ev.Level ?? "info"}] {ev.Message}");
                break;

            default:
                AppendLog(line);
                break;
        }
    }

    private void ApplyState(string state, string? newAddr)
    {
        if (!string.IsNullOrWhiteSpace(newAddr))
        {
            addr = newAddr!;
        }

        var next = state switch
        {
            "starting" => AppState.Starting,
            "running" => AppState.Running,
            "stopping" => AppState.Stopping,
            "stopped" => AppState.Stopped,
            _ => State,
        };

        if (next == AppState.Running && State != AppState.Running)
        {
            RunningSince = DateTime.Now;
        }
        else if (next != AppState.Running)
        {
            RunningSince = null;
        }

        State = next;
        AppendLog($"[状态] {state} {addr}".TrimEnd());
        RaiseState();
    }

    private void HandleStderr(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        AppendLog("[stderr] " + line);
        if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("bind", StringComparison.OrdinalIgnoreCase) && line.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            lastError = line;
        }
    }

    private void HandleExited()
    {
        ready = false;
        if (!disposed && State is not (AppState.Stopped or AppState.Stopping))
        {
            AppendLog("[核心] 进程已退出");
        }
        RunningSince = null;
        State = AppState.Stopped;
        RaiseState();
    }

    private void SetState(AppState state)
    {
        if (state == AppState.Running)
        {
            RunningSince ??= DateTime.Now;
        }
        else
        {
            RunningSince = null;
        }
        State = state;
        RaiseState();
    }

    private void RaiseState()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // 忽略 UI 异常。
        }
    }

    private async Task<bool> SendAsync(string command)
    {
        var proc = process;
        if (proc == null)
        {
            return false;
        }
        try
        {
            if (proc.HasExited)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        await stdinLock.WaitAsync().ConfigureAwait(false);
        try
        {
            AppendLog("[GUI] → " + command);
            await proc.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("[GUI] 写入命令失败：" + ex.Message);
            return false;
        }
        finally
        {
            stdinLock.Release();
        }
    }

    // ---------------------------------------------------------------- 命令行操作

    /// <summary>carp.exe version，返回原始文本。</summary>
    public async Task<string?> GetCoreVersionAsync()
    {
        var result = await CliRunner.RunAsync(CoreExe, "version", Root, timeoutSeconds: 15).ConfigureAwait(false);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Output))
        {
            AppLog.Warn($"[GUI] 读取 core version 失败（Ok={result.Ok}）：{result.Output}");
            return null;
        }
        return result.Output.Trim();
    }

    /// <summary>carp.exe backup -data ... -out ... -no-secret，返回 zip 路径。</summary>
    public async Task<(bool Ok, string Message)> BackupAsync(string outDir)
    {
        var args = $"backup -data \"{DataDir}\" -out \"{outDir}\" -no-secret";
        var result = await CliRunner.RunAsync(CoreExe, args, Root, timeoutSeconds: 600).ConfigureAwait(false);
        var text = result.Output.Trim();
        if (!result.Ok)
        {
            return (false, string.IsNullOrWhiteSpace(text) ? "备份失败（无输出）。" : text);
        }
        var zip = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return (true, zip ?? (string.IsNullOrWhiteSpace(text) ? outDir : text));
    }

    /// <summary>carp.exe reset-admin-password --root -data ... -password-stdin。</summary>
    public async Task<(bool Ok, string Message)> ResetRootPasswordAsync(string password)
    {
        var args = $"reset-admin-password --root -data \"{DataDir}\" -password-stdin";
        var result = await CliRunner.RunAsync(CoreExe, args, Root, stdin: password + "\n", timeoutSeconds: 120)
            .ConfigureAwait(false);
        var text = result.Output.Trim();
        return (result.Ok, text);
    }

    /// <summary>carp.exe doctor -data ...，返回原始输出。</summary>
    public async Task<(bool Ok, string Output)> RunDoctorAsync()
    {
        var result = await CliRunner.RunAsync(CoreExe, $"doctor -data \"{DataDir}\"", Root, timeoutSeconds: 120)
            .ConfigureAwait(false);
        return (result.Ok, result.Output);
    }

    /// <summary>GET /health，用于启动前探测已有核心。</summary>
    public async Task<bool> HealthAsync()
    {
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{Config.Port}/health").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 &lt;dataDir&gt;/carp.pid，不存在或非法时返回 0。</summary>
    public int ReadPidFile()
    {
        try
        {
            var path = Path.Combine(DataDir, "carp.pid");
            if (!File.Exists(path))
            {
                return 0;
            }
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) && pid > 0 ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- 日志

    private void AppendLog(string line)
    {
        lock (logGate)
        {
            logLines.Add(line);
            if (logLines.Count > MaxLogLines)
            {
                logLines.RemoveRange(0, logLines.Count - MaxLogLines);
            }
        }
        AppLog.Info(line);
        try
        {
            LogChanged?.Invoke();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>由界面写入一条日志（同时刷新面板与落盘）。</summary>
    public void AddLog(string message) => AppendLog(message);

    public string SnapshotLog()
    {
        lock (logGate)
        {
            return logLines.Count == 0 ? "" : string.Join(Environment.NewLine, logLines);
        }
    }

    public void ClearLog()
    {
        lock (logGate)
        {
            logLines.Clear();
        }
        AppendLog("[GUI] 日志已清空");
        try
        {
            LogChanged?.Invoke();
        }
        catch
        {
            // 忽略。
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return condition();
    }
}
