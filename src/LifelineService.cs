using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Lifeline;

internal sealed class LifelineService : IDisposable
{
    private readonly IScriptInterface _bot;
    private readonly Func<IOptionContainer?> _options;
    private readonly BlipDetector _detector;
    private readonly RestartRequestWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cachedUserLock = new();
    private readonly object _recoverLock = new();
    private readonly object _autoReloginLock = new();
    private readonly object _heartbeatLock = new();

    private FlashModalWatcher? _modalWatcher;
    private string _cachedUsername = "";
    private bool _recovering;
    private bool _disposed;

    private bool _autoReloginSuppressed;
    private bool _originalAutoRelogin;

    private ScriptHeartbeat _lastRunningHeartbeat;
    private int _restartInFlight;
    private RestartContext _blipContext;

    private readonly record struct ScriptHeartbeat(string? LoadedScript, string? Storage, DateTime Timestamp);
    private readonly record struct HealthResult(bool Healthy, string Reason);

    public LifelineService(IScriptInterface bot, Func<IOptionContainer?> options)
    {
        _bot = bot;
        _options = options;
        _writer = new RestartRequestWriter(
            bot, Log,
            () => Opt("maxRestartsPerHour", 5),
            () => Opt("pingTarget", "8.8.8.8"),
            () => Opt("pingTimeoutMs", 2000));
        _detector = new BlipDetector(
            () => Opt("enabled", true),
            () => Opt("pingTarget", "8.8.8.8"),
            () => Opt("failureThreshold", 2),
            () => Opt("pingTimeoutMs", 2000));
        _detector.EarlyWarning        += OnEarlyWarning;
        _detector.EarlyWarningCleared += OnEarlyWarningCleared;
        _detector.BlipDetected        += OnBlipDetected;
        _detector.ConnectionRestored  += () => Log("network restored (first successful ping after blip)");
    }

    public void Start()
    {
        if (_disposed) return;
        RefreshCachedUsername();

        // FlashModalWatcher is now started on demand from OnEarlyWarning() and
        // stopped when the recovery cycle finishes — see EnsureModalWatcherRunning
        // and StopModalWatcher. This avoids 5 Hz desktop-wide EnumWindows during
        // normal play, when the modal can't possibly appear.

        _detector.Start();
        _ = Task.Run(HeartbeatLoop, _cts.Token);
        _ = Task.Run(HousekeepingLoop, _cts.Token);

        Log($"Lifeline Started (Ping: {Opt("pingTarget", "8.8.8.8")} | Threshold: {Opt("failureThreshold", 2)} | Restore: {Opt("restoreStableSec", 10)}s | Grace: {Opt("graceAfterRestoreSec", 10)}s)");
    }

    public void Stop() => _cts.Cancel();

    /// <summary>
    /// Exposed for the Settings window Test tab. Bypasses the watcher and triggers
    /// the exact same logic used when a Flash modal is actually detected.
    /// </summary>
    public void SimulateFlashModal() => HandleFlashModal();

    private void OnEarlyWarning()
    {
        RefreshCachedUsername();
        Log("first ping failed (early warning)");
        TrySuppressAutoRelogin();
        EnsureModalWatcherRunning();
    }

    private void OnEarlyWarningCleared()
    {
        Log("ping recovered before blip threshold");
        if (!_recovering)
        {
            RestoreAutoReloginIfSuppressed("early-warning-cleared");
            StopModalWatcher("early-warning-cleared");
        }
    }

    private void OnBlipDetected()
    {
        RefreshCachedUsername();
        Log("blip detected");

        RestartContext live = _writer.CaptureCurrent();
        RestartContext captured;
        lock (_heartbeatLock)
        {
            bool recent = _lastRunningHeartbeat.Timestamp != default &&
                          (DateTime.UtcNow - _lastRunningHeartbeat.Timestamp).TotalSeconds <= 30;
            if (!live.ScriptWasRunning && recent)
            {
                Log($"using heartbeat script state (loaded={Path.GetFileName(_lastRunningHeartbeat.LoadedScript ?? "(none)")} storage={_lastRunningHeartbeat.Storage ?? "(none)"})");
                captured = new RestartContext(
                    _lastRunningHeartbeat.LoadedScript,
                    true,
                    _lastRunningHeartbeat.Storage ?? live.ScriptOptionsStorage);
            }
            else
            {
                captured = live;
            }
        }
        _blipContext = captured;

        _ = Task.Run(() => RecoveryCycle(captured, _cts.Token), _cts.Token);
    }

    private async Task RecoveryCycle(RestartContext context, CancellationToken ct)
    {
        lock (_recoverLock)
        {
            if (_recovering) return;
            _recovering = true;
        }
        try
        {
            bool wasLoggedIn = false;
            try { wasLoggedIn = _bot.Player.LoggedIn && !string.IsNullOrEmpty(_bot.Player.Username); } catch { }
            Log($"recovery start: wasLoggedIn={wasLoggedIn} scriptWasRunning={context.ScriptWasRunning}");

            if (!wasLoggedIn)
            {
                Log("recovery: skipping — player not logged in at blip time");
                RestoreAutoReloginIfSuppressed("not-logged-in");
                return;
            }

            int stableSec = Math.Max(3, Opt("restoreStableSec", 10));
            if (!await WaitForStableRestore(stableSec, ct))
            {
                Log("recovery: cancelled while waiting for network");
                RestoreAutoReloginIfSuppressed("cancelled");
                return;
            }

            int graceSec = Math.Max(0, Opt("graceAfterRestoreSec", 10));
            if (graceSec > 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(graceSec), ct); }
                catch (OperationCanceledException) { RestoreAutoReloginIfSuppressed("cancelled"); return; }
            }

            HealthResult health = await HealthCheck(context, ct);
            Log($"health: {(health.Healthy ? "OK" : "FAIL")} — {health.Reason}");

            if (health.Healthy)
            {
                RestoreAutoReloginIfSuppressed("healthy");
                RestartRequestWriter.PruneState(GetCachedUsername());
                return;
            }

            if (!Opt("restartOnFailure", true))
            {
                Log("stage 2 disabled; leaving client running");
                RestoreAutoReloginIfSuppressed("stage2-disabled");
                return;
            }
            if (Opt("respectExitOnScriptStop", true) && LaunchedWithExitOnScriptStop())
            {
                Log("declining restart: client was launched with --exit-on-script-stop");
                RestoreAutoReloginIfSuppressed("exit-on-script-stop");
                return;
            }

            if (Interlocked.CompareExchange(ref _restartInFlight, 1, 0) != 0)
            {
                Log("restart already in flight; skipping duplicate request");
                return;
            }

            try
            {
                EnsureHelperRunning();
                Log($"stage 2: writing restart request (reason=health-failed [{health.Reason}])");
                WriteOutcome outcome = _writer.Write($"health-failed [{health.Reason}]", context);
                switch (outcome)
                {
                    case WriteOutcome.Restart:
                        Log("exiting so LifelineHelper can relaunch the client");
                        try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
                        Environment.Exit(0);
                        break;
                    case WriteOutcome.GiveUp:
                        Log("gave up (rate limit)");
                        RestoreAutoReloginIfSuppressed("give-up");
                        break;
                    case WriteOutcome.Failed:
                        Log("could not write restart request");
                        RestoreAutoReloginIfSuppressed("write-failed");
                        break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _restartInFlight, 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"recovery cycle threw: {ex.Message}"); }
        finally
        {
            _recovering = false;
            StopModalWatcher("recovery-cycle-complete");
        }
    }

    private async Task<bool> WaitForStableRestore(int requiredSec, CancellationToken ct)
    {
        int consecutive = 0;
        DateTime lastLog = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            bool ok = await PingOnceAsync(Opt("pingTarget", "8.8.8.8"), Opt("pingTimeoutMs", 2000));
            if (ok)
            {
                consecutive++;
                if (consecutive * 3 >= requiredSec)
                {
                    Log($"network stable ({consecutive} consecutive pings ok, ~{consecutive * 3}s)");
                    return true;
                }
            }
            else
            {
                if (consecutive > 0) Log($"ping failed again after {consecutive} successes; resetting");
                consecutive = 0;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 60)
                {
                    lastLog = DateTime.UtcNow;
                    Log($"still waiting for network (ping {Opt("pingTarget", "8.8.8.8")} failing)");
                }
            }
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static async Task<bool> PingOnceAsync(string target, int timeoutMs)
    {
        try
        {
            using Ping p = new();
            PingReply reply = await p.SendPingAsync(target, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private async Task<HealthResult> HealthCheck(RestartContext context, CancellationToken ct)
    {
        bool loggedIn = false, playing = false, mapLoaded = false, scriptRunningNow = false;
        string user = "", cell = "";
        try { loggedIn = _bot.Player.LoggedIn; } catch { }
        try { playing = _bot.Player.Playing; } catch { }
        try { mapLoaded = _bot.Map.Loaded; } catch { }
        try { user = _bot.Player.Username ?? ""; } catch { }
        try { cell = _bot.Player.Cell ?? ""; } catch { }
        try { scriptRunningNow = _bot.Manager.ScriptRunning; } catch { }

        if (!loggedIn) return new(false, "loggedIn=false");
        if (!playing) return new(false, "playing=false");
        if (string.IsNullOrEmpty(user)) return new(false, "username empty");
        if (!mapLoaded) return new(false, "map not loaded");
        if (string.IsNullOrEmpty(cell) || cell == "null") return new(false, $"invalid cell '{cell}'");

        if (context.ScriptWasRunning && !scriptRunningNow)
        {
            if (Opt("restartScriptIfStopped", true))
            {
                Log("client healthy but script stopped — restarting script in-process");
                if (!await TryRestartScript(context))
                    return new(false, "could not restart stopped script");
                if (await WaitForActivity(15, ct, "script restarted"))
                    return new(true, "script restarted, activity observed");
                return new(false, "script restarted but no activity in 15s");
            }
            return new(true, "playing (script stopped; restart disabled)");
        }

        if (context.ScriptWasRunning && scriptRunningNow)
        {
            int window = Math.Max(5, Opt("activityWindowSec", 30));
            if (await WaitForActivity(window, ct, "script running"))
                return new(true, $"activity observed within {window}s");
            return new(false, $"no activity in {window}s while script running");
        }

        return new(true, "playing (script idle at blip)");
    }

    private async Task<bool> WaitForActivity(int windowSec, CancellationToken ct, string label)
    {
        string baseline = ProgressSignature();
        int waited = 0;
        while (waited < windowSec * 1000)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return false; }
            waited += 1000;

            bool stillPlaying = false;
            try { stillPlaying = _bot.Player.Playing; } catch { }
            if (!stillPlaying) return false;

            string now = ProgressSignature();
            if (now != baseline)
            {
                Log($"{label}: activity observed: {now}");
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryRestartScript(RestartContext context)
    {
        if (string.IsNullOrWhiteSpace(context.LoadedScript)) { Log("script restart: no loaded script"); return false; }
        if (!File.Exists(context.LoadedScript)) { Log($"script restart: file missing: {context.LoadedScript}"); return false; }
        try
        {
            _bot.Manager.SetLoadedScript(context.LoadedScript);
            PropertyInfo? silent = _bot.Manager.GetType().GetProperty("SilentConfig");
            object? prev = null;
            try
            {
                if (silent?.CanWrite == true)
                {
                    prev = silent.GetValue(_bot.Manager);
                    silent.SetValue(_bot.Manager, true);
                }
                Exception? err = await _bot.Manager.StartScript();
                if (err is not null) { Log($"StartScript: {err.Message}"); return false; }
                return true;
            }
            finally
            {
                if (prev is not null && silent?.CanWrite == true) silent.SetValue(_bot.Manager, prev);
            }
        }
        catch (Exception ex) { Log($"restart script: {ex.Message}"); return false; }
    }

    private string ProgressSignature()
    {
        try
        {
            return string.Join("|",
                _bot.Stats.Kills, _bot.Stats.Drops, _bot.Stats.QuestsCompleted,
                _bot.Stats.QuestsAccepted, _bot.Player.Gold, _bot.Map.FullName, _bot.Player.Cell);
        }
        catch { return ""; }
    }

    private void EnsureModalWatcherRunning()
    {
        if (_disposed) return;
        if (!Opt("restartOnFailure", true) || !Opt("restartOnFlashModal", true)) return;
        if (_modalWatcher is not null) return;

        try
        {
            _modalWatcher = new FlashModalWatcher(
                msg => LifelineLog.Write(GetCachedUsername(), msg),
                HandleFlashModal,
                _cts.Token);
            _modalWatcher.Start();
            Log("flash modal watcher started (blip window active)");
        }
        catch (Exception ex)
        {
            Log($"could not start flash modal watcher: {ex.Message}");
            _modalWatcher = null;
        }
    }

    private void StopModalWatcher(string reason)
    {
        if (_modalWatcher is null) return;
        try { _modalWatcher.Dispose(); } catch { }
        _modalWatcher = null;
        Log($"flash modal watcher stopped ({reason})");
    }

    private void HandleFlashModal()
    {
        try
        {
            if (Interlocked.CompareExchange(ref _restartInFlight, 1, 0) != 0)
            {
                LifelineLog.Write(GetCachedUsername(), "flash modal handler: restart already in flight; skipping");
                return;
            }
            try
            {
                string user = GetCachedUsername();
                RestartContext context = (_blipContext.LoadedScript is not null || _blipContext.ScriptWasRunning)
                    ? _blipContext
                    : _writer.CaptureCurrent();
                LifelineLog.Write(user, $"flash modal handler: writing restart request (loaded={context.LoadedScript ?? "(none)"} running={context.ScriptWasRunning})");
                EnsureHelperRunning();
                WriteOutcome outcome = _writer.Write("flash-modal", context);
                if (outcome == WriteOutcome.Restart)
                {
                    LifelineLog.Write(user, "flash modal handler: exiting so LifelineHelper can relaunch the client");
                    Thread.Sleep(200);
                    Environment.Exit(0);
                }
                else
                {
                    LifelineLog.Write(user, $"flash modal handler: restart not written (outcome={outcome})");
                    Interlocked.Exchange(ref _restartInFlight, 0);
                }
            }
            catch
            {
                Interlocked.Exchange(ref _restartInFlight, 0);
                throw;
            }
        }
        catch (Exception ex)
        {
            try { LifelineLog.Write(GetCachedUsername(), $"flash modal handler failed: {ex.Message}"); } catch { }
        }
    }

    private async Task HeartbeatLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool running = false;
                try { running = _bot.Manager.ScriptRunning; } catch { }
                if (running)
                {
                    string? loaded = null; string? storage = null;
                    try { loaded = _bot.Manager.LoadedScript; } catch { }
                    storage = TryGetStorage();
                    lock (_heartbeatLock)
                        _lastRunningHeartbeat = new ScriptHeartbeat(loaded, storage, DateTime.UtcNow);
                }
            }
            catch { }
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private string? TryGetStorage()
    {
        try
        {
            PropertyInfo? overrideProp = _bot.Manager.GetType()
                .GetProperty("OverrideStorage", BindingFlags.Public | BindingFlags.Instance);
            if (overrideProp?.GetValue(_bot.Manager) is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        catch { }
        try { return _bot.Manager.Config?.Storage; } catch { return null; }
    }

    private async Task HousekeepingLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token); }
            catch (OperationCanceledException) { break; }
            catch { }
            try
            {
                string user = GetCachedUsername();
                if (!string.IsNullOrEmpty(user) && user != "?")
                    RestartRequestWriter.PruneState(user);

                if (Opt("enabled", true) && Opt("restartOnFailure", true) && !IsHelperRunning())
                {
                    Log("helper not running — attempting to start it");
                    EnsureHelperRunning();
                }
            }
            catch { }
        }
    }

    private void TrySuppressAutoRelogin()
    {
        if (!Opt("suppressAutoRelogin", true)) return;
        lock (_autoReloginLock)
        {
            if (_autoReloginSuppressed) return;
            try
            {
                _originalAutoRelogin = _bot.Options.AutoRelogin;
                if (_originalAutoRelogin)
                {
                    _bot.Options.AutoRelogin = false;
                    _autoReloginSuppressed = true;
                    Log("suppressed VibeSkua auto-relogin during blip window");
                }
            }
            catch (Exception ex) { Log($"could not suppress auto-relogin: {ex.Message}"); }
        }
    }

    private void RestoreAutoReloginIfSuppressed(string reason)
    {
        lock (_autoReloginLock)
        {
            if (!_autoReloginSuppressed) return;
            try
            {
                _bot.Options.AutoRelogin = _originalAutoRelogin;
                Log($"restored VibeSkua auto-relogin ({reason})");
            }
            catch (Exception ex) { Log($"could not restore auto-relogin: {ex.Message}"); }
            finally { _autoReloginSuppressed = false; }
        }
    }

    private static bool LaunchedWithExitOnScriptStop()
    {
        foreach (string a in Environment.GetCommandLineArgs())
            if (a == "--exit-on-script-stop" || a == "--exit-on-finish")
                return true;
        return false;
    }

    private void RefreshCachedUsername()
    {
        try
        {
            string? user = _bot.Player?.Username;
            if (!string.IsNullOrEmpty(user))
            {
                lock (_cachedUserLock)
                {
                    _cachedUsername = GetSafeLabel(user);
                }
            }
        }
        catch { }
    }

    private string GetSafeLabel(string username)
    {
        try
        {
            var accounts = _bot.Accounts?.GetAllAccounts();
            if (accounts != null)
            {
                var acc = accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (acc != null && !string.IsNullOrWhiteSpace(acc.DisplayName))
                    return acc.DisplayName;
            }
        }
        catch { }
        return username;
    }

    private string GetCachedUsername()
    {
        lock (_cachedUserLock) { if (!string.IsNullOrEmpty(_cachedUsername)) return _cachedUsername; }
        return "?";
    }

    private T Opt<T>(string name, T fallback) where T : IConvertible
    {
        try
        {
            IOptionContainer? o = _options();
            if (o is null) return fallback;
            IOption? opt = o.Options.Find(x => x.Name == name);
            if (opt is null) return fallback;
            string raw = o.GetDirect(opt);
            if (string.IsNullOrEmpty(raw)) return fallback;
            object? parsed = o.Get<T>(opt);
            return parsed is T v ? v : fallback;
        }
        catch { return fallback; }
    }

    private void Log(string message)
    {
        // Only write to the Lifeline log (lifeline.log → Lifeline log window).
        // Do NOT write to the Skua script log — the user wants Lifeline messages
        // kept out of the client UI's script output entirely.
        string who = GetCachedUsername();
        LifelineLog.Write(who, message);
    }

    private void EnsureHelperRunning()
    {
        if (IsHelperRunning()) return;
        string[] candidates =
        {
            Path.Combine(ClientFileSources.SkuaPluginsDIR, "Lifeline", "LifelineHelper.exe"),
            Path.Combine(ClientFileSources.SkuaDIR, "lifeline", "LifelineHelper.exe"),
            Path.Combine(AppContext.BaseDirectory, "LifelineHelper.exe"),
        };
        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            Log("LifelineHelper.exe not found — cannot ensure helper is running");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host-pid" && i + 1 < args.Length)
                {
                    psi.ArgumentList.Add("--host-pid");
                    psi.ArgumentList.Add(args[i + 1]);
                    break;
                }
            }

            Process.Start(psi)?.Dispose();
            Log($"started LifelineHelper from {exe}");
        }
        catch (Exception ex) { Log($"could not start LifelineHelper: {ex.Message}"); }
    }

    private static bool IsHelperRunning()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName("LifelineHelper");
            bool any = procs.Length > 0;
            foreach (Process p in procs) { try { p.Dispose(); } catch { } }
            return any;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _modalWatcher?.Dispose(); } catch { }
        try { _detector.Dispose(); } catch { }
        try { RestoreAutoReloginIfSuppressed("dispose"); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
