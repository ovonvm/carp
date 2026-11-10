using System.Text.Json.Serialization;

namespace Carp.Gui.Services;

/// <summary>服务运行状态（由核心 stdout 的 state 事件驱动）。</summary>
public enum AppState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

/// <summary>
/// 常驻核心 stdout 输出的单行 JSON 事件：
/// <c>{"event":"ready","version":"...","data_dir":"..."}</c>、
/// <c>{"event":"state","state":"running","addr":"127.0.0.1:50011"}</c>、
/// <c>{"event":"error","message":"..."}</c>、
/// <c>{"event":"log","level":"info","message":"..."}</c>。
/// </summary>
public sealed class CoreEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("addr")]
    public string? Addr { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("data_dir")]
    public string? DataDir { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }
}
