using System.Reflection;
using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Lifeline;

internal enum WriteOutcome { Restart, GiveUp, Failed }

/// <summary>
/// Writes a JSON restart request that LifelineHelper picks up and acts on.
/// Rate-limits itself so a genuinely broken Flash install cannot loop forever.
/// Refuses to run in the tab host.
/// </summary>
internal sealed class RestartRequestWriter
{
    private readonly IScriptInterface _bot;
    private readonly Action<string> _log;
    private readonly Func<int> _maxPerHour;
    private readonly Func<string> _pingTarget;
    private readonly Func<int> _pingTimeoutMs;

    public RestartRequestWriter(
        IScriptInterface bot,
        Action<string> log,
        Func<int> maxPerHour,
        Func<string> pingTarget,
        Func<int> pingTimeoutMs)
    {
        _bot = bot;
        _log = log;
        _maxPerHour = maxPerHour;
        _pingTarget = pingTarget;
        _pingTimeoutMs = pingTimeoutMs;
    }

    private static string RequestsDir => Path.Combine(ClientFileSources.SkuaDIR, "lifeline", "requests");
    private static string StateDir    => Path.Combine(ClientFileSources.SkuaDIR, "lifeline", "state");

    public RestartContext CaptureCurrent()
    {
        try
        {
            string? loaded = null;
            bool running = false;
            try
            {
                loaded = _bot.Manager.LoadedScript;
                running = _bot.Manager.ScriptRunning;
            }
            catch { }

            string? storage = null;
            try
            {
                PropertyInfo? overrideProp = _bot.Manager.GetType()
                    .GetProperty("OverrideStorage", BindingFlags.Public | BindingFlags.Instance);
                if (overrideProp?.GetValue(_bot.Manager) is string s && !string.IsNullOrEmpty(s))
                    storage = s;
            }
            catch { }

            try
            {
                if (string.IsNullOrEmpty(storage))
                    storage = _bot.Manager.Config?.Storage;
            }
            catch { }

            return new RestartContext(loaded, running, storage);
        }
        catch
        {
            return new RestartContext(null, false, null);
        }
    }

    public WriteOutcome Write(string reason, RestartContext? overrideContext = null)
    {
        if (ProcessKind.IsHost)
        {
            _log("refusing to write restart request: this is the tab host, not a client");
            return WriteOutcome.Failed;
        }

        try
        {
            string username = _bot.Player.Username ?? "unknown";

            List<DateTime> history = ReadHistory(username);
            DateTime cutoff2h = DateTime.UtcNow - TimeSpan.FromHours(2);
            history = history.Where(t => t > cutoff2h).ToList();

            int max = Math.Max(1, _maxPerHour());
            DateTime cutoff1h = DateTime.UtcNow - TimeSpan.FromHours(1);
            int recent = history.Count(t => t > cutoff1h);

            if (recent >= max)
            {
                _log($"GIVE-UP: {recent} restarts in the last hour for {username} (limit {max}).");
                WriteJson(new RestartRequest
                {
                    Action = "give-up",
                    Pid = Environment.ProcessId,
                    Reason = reason,
                    WrittenAt = DateTime.UtcNow.ToString("O"),
                    Username = username,
                });
                return WriteOutcome.GiveUp;
            }

            history.Add(DateTime.UtcNow);
            WriteHistory(username, history);

            string exe = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Environment.ProcessPath was null.");
            string[] replay = StripHostArgs(Environment.GetCommandLineArgs().Skip(1));

            RestartContext context = overrideContext ?? CaptureCurrent();
            _log($"restart context: loaded={context.LoadedScript ?? "(none)"} running={context.ScriptWasRunning} storage={context.ScriptOptionsStorage ?? "(none)"}");

            WriteJson(new RestartRequest
            {
                Action               = "restart",
                Pid                  = Environment.ProcessId,
                Exe                  = exe,
                Args                 = replay,
                Reason               = reason,
                WrittenAt            = DateTime.UtcNow.ToString("O"),
                Username             = username,
                LoadedScript         = context.LoadedScript,
                ScriptWasRunning     = context.ScriptWasRunning,
                ScriptOptionsStorage = context.ScriptOptionsStorage,
                PingTarget           = _pingTarget(),
                PingWaitMaxSec       = 600,
                PingIntervalSec      = 5,
                PingTimeoutMs        = _pingTimeoutMs(),
            });
            _log($"restart request written for {username} (pid {Environment.ProcessId})");
            return WriteOutcome.Restart;
        }
        catch (Exception ex)
        {
            _log($"write restart request failed: {ex.Message}");
            return WriteOutcome.Failed;
        }
    }

    public static void PruneState(string username)
    {
        try
        {
            string path = HistoryPath(username);
            if (!File.Exists(path)) return;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
            List<DateTime> kept = ReadHistory(username).Where(t => t > cutoff).ToList();
            WriteHistory(username, kept);
        }
        catch { }
    }

    private static void WriteJson(RestartRequest req)
    {
        Directory.CreateDirectory(RequestsDir);
        string path = Path.Combine(RequestsDir, req.FileName);
        string tmp  = path + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true }));

        // Atomic rename within the same directory. The FileSystemWatcher's
        // *.restart.json filter never sees the .tmp file, so the Created event
        // only fires once the payload is fully written.
        File.Move(tmp, path, overwrite: true);
    }

    private static string HistoryPath(string username) =>
        Path.Combine(StateDir, $"{username.ToLowerInvariant()}.restarts");

    private static List<DateTime> ReadHistory(string username)
    {
        try
        {
            string path = HistoryPath(username);
            if (!File.Exists(path)) return new();
            List<DateTime> result = new();
            foreach (string line in File.ReadAllLines(path))
            {
                if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t))
                    result.Add(t);
            }
            return result;
        }
        catch { return new(); }
    }

    private static void WriteHistory(string username, IEnumerable<DateTime> values)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllLines(HistoryPath(username), values.Select(t => t.ToString("O")));
        }
        catch { }
    }

    private static string[] StripHostArgs(IEnumerable<string> args)
    {
        List<string> result = new();
        bool skipNext = false;
        foreach (string a in args)
        {
            if (skipNext) { skipNext = false; continue; }
            if (a == "--embed" || a == "--host-pid") { skipNext = true; continue; }
            result.Add(a);
        }
        return result.ToArray();
    }
}
