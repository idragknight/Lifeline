using System.Text.Json.Serialization;

namespace Lifeline;

// Keep this schema byte-identical in:
//   src/RestartRequest.cs        (plugin — writer)
//   helper/RestartRequest.cs     (helper — reader)
internal sealed class RestartRequest
{
    [JsonPropertyName("v")]                     public int Version { get; set; } = 2;
    [JsonPropertyName("action")]                public string Action { get; set; } = "restart";
    [JsonPropertyName("pid")]                   public int Pid { get; set; }
    [JsonPropertyName("exe")]                   public string Exe { get; set; } = "";
    [JsonPropertyName("args")]                  public string[] Args { get; set; } = Array.Empty<string>();
    [JsonPropertyName("reason")]                public string Reason { get; set; } = "";
    [JsonPropertyName("writtenAt")]             public string WrittenAt { get; set; } = "";
    [JsonPropertyName("username")]              public string Username { get; set; } = "";

    // v1.0.5: script state captured BEFORE anything touches the manager. The helper
    // uses these to inject --run-script / --load-script / --script-options-storage
    // when the original launch didn't have them.
    [JsonPropertyName("loadedScript")]          public string? LoadedScript { get; set; }
    [JsonPropertyName("scriptWasRunning")]      public bool ScriptWasRunning { get; set; }
    [JsonPropertyName("scriptOptionsStorage")]  public string? ScriptOptionsStorage { get; set; }

    // v1.1.0: helper waits for network connectivity before relaunching, so a modal
    // triggered during an outage doesn't boot a new client into a dead link.
    [JsonPropertyName("pingTarget")]            public string? PingTarget { get; set; }
    [JsonPropertyName("pingWaitMaxSec")]        public int PingWaitMaxSec { get; set; }
    [JsonPropertyName("pingIntervalSec")]       public int PingIntervalSec { get; set; }
    [JsonPropertyName("pingTimeoutMs")]         public int PingTimeoutMs { get; set; }

    public string FileName => $"{Pid}.restart.json";
}

/// <summary>
/// Snapshot of the script manager's state at the moment recovery starts.
/// Captured before any recovery action so it reflects what the account was
/// actually doing when the blip hit.
/// </summary>
internal readonly record struct RestartContext(
    string? LoadedScript,
    bool ScriptWasRunning,
    string? ScriptOptionsStorage);
