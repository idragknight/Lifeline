using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Lifeline;

public sealed class LifelinePlugin : ISkuaPlugin
{
    private const string MenuSettings = "Lifeline";

    public string Name        => "Lifeline";
    public string Author      => "Excite";
    public string Description =>
        "Watches the connection, waits for it to return, and only restarts the client " +
        "when it didn't come back healthy. Suppresses VibeSkua's own auto-relogin during a blip " +
        "so the two recovery paths don't race and trigger the Flash modal. If the client still " +
        "goes bad, LifelineHelper relaunches it with the same account and the same script — " +
        "after verifying the network is reachable. The fixed modal watcher catches the " +
        "'reinstall CleanFlash' box even when it's owned by FlashUtil rather than the client. " +
        "A restarted client signals the host to switch to grid view once it logs in. " +
        "Requires VibeSkua 2.0+.";

    public string OptionsStorage => "Lifeline";

    public List<IOption>? Options { get; } = new()
    {
        LifelineOpt.Bool("enabled", "Enable Lifeline", 
            "Master switch. When off, no detection or recovery will run.", true),
            
        LifelineOpt.Bool("restartOnFailure", "Restart on Health Check Failure", 
            "Request an external restart if the client fails to recover after a blip.", true),
            
        LifelineOpt.Bool("restartOnFlashModal", "Restart on 'Reinstall Flash' Popup", 
            "Detect the Flash error popup and restart immediately.", true),
            
        LifelineOpt.Bool("restartScriptIfStopped", "Restart script if stopped", 
            "Start the script in-process if it stopped after a blip.", true),
            
        LifelineOpt.Bool("suppressAutoRelogin", "Suppress VibeSkua auto-relogin", 
            "Temporarily disable VibeSkua's AutoRelogin during blips to prevent races.", true),
            
        LifelineOpt.Bool("restartGridOnLogin", "Enter Grid View after restart", 
            "Switch to Grid View in the host after this client restarts.", true),
            
        LifelineOpt.Bool("respectExitOnScriptStop", "Respect --exit-on-script-stop", 
            "Don't restart if launched with --exit-on-script-stop.", true),
            
        LifelineOpt.String("pingTarget", "Ping target", 
            "Host to ping for connectivity checks.", "8.8.8.8"),
            
        LifelineOpt.Int("failureThreshold", "Failure threshold", 
            "Number of failed pings to trigger a blip.", 2),
            
        LifelineOpt.Int("pingTimeoutMs", "Ping timeout (ms)", 
            "Timeout per ping in milliseconds.", 2000),
            
        LifelineOpt.Int("restoreStableSec", "Restore stability window (s)", 
            "Seconds the network must stay stable.", 10),
            
        LifelineOpt.Int("graceAfterRestoreSec", "Grace after restore (s)", 
            "Extra seconds to wait before checking client health.", 10),
            
        LifelineOpt.Int("activityWindowSec", "Activity window (s)", 
            "Seconds to wait for stats to change to confirm health.", 30),
            
        LifelineOpt.Int("maxRestartsPerHour", "Max restarts per hour", 
            "Maximum restart requests per account per hour.", 5),
    };

    private IScriptInterface? _bot;
    private IPluginHelper? _helper;
    private IPluginManager? _plugins;
    private LifelineService? _service;
    private IDispatcherService? _dispatcher;
    private IDialogService? _dialogs;
    private IOptionContainer? _optionContainer;

    private CancellationTokenSource? _gridTriggerCts;
    private FileSystemWatcher? _gridRequestWatcher;
    private FileSystemWatcher? _openLogWatcher;
    private FileSystemWatcher? _openSettingsWatcher;
    private CancellationTokenSource? _trayPollCts;

    private LifelineSettingsWindow? _settingsWindow;
    private LifelineLogWindow? _logWindow;

    private DateTime _lastSettingsOpenUtc = DateTime.MinValue;
    private DateTime _lastLogOpenUtc = DateTime.MinValue;
    private static readonly TimeSpan TrayDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TrayRequestFreshness = TimeSpan.FromSeconds(30);

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _helper = helper;
        _bot = provider.GetService(typeof(IScriptInterface)) as IScriptInterface ?? IScriptInterface.Instance;
        _plugins = provider.GetService(typeof(IPluginManager)) as IPluginManager;
        _dispatcher = provider.GetService(typeof(IDispatcherService)) as IDispatcherService;
        _dialogs = provider.GetService(typeof(IDialogService)) as IDialogService;
        _optionContainer = _plugins?.GetContainer(this)?.OptionContainer;

        if (_bot is null)
        {
            LifelineLog.Write("?", "Lifeline: IScriptInterface unavailable; plugin inert.");
            return;
        }

        if (ProcessKind.IsHost)
        {
            ClearLogs();

            LifelineLog.Write("host", $"Lifeline loaded in host (pid {Environment.ProcessId}); recovery disabled.");
            StartGridRequestWatcher();
            StartTrayRequestHandling();
            try { helper.AddMenuButton(MenuSettings, ShowSettings); } catch { }
            return;
        }

        _service = new LifelineService(_bot, () => _plugins?.GetContainer(this)?.OptionContainer);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);

                for (int i = 0; i < 120; i++)
                {
                    if (!string.IsNullOrEmpty(_bot?.Player?.Username)) break;
                    await Task.Delay(1000);
                }

                _service?.Start();
            }
            catch (Exception ex)
            {
                LifelineLog.Write(ProcessLabel(), $"deferred service start failed: {ex.Message}");
            }
        });

        try { helper.AddMenuButton(MenuSettings, ShowSettings); } catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 120; i++)
                {
                    if (!string.IsNullOrEmpty(_bot?.Player?.Username)) break;
                    await Task.Delay(1000);
                }

                EnsureHelperRunning();
                StartTrayRequestHandling();
                LifelineLog.Write(ProcessLabel(), $"Lifeline loaded in child (pid {Environment.ProcessId}).");

                try
                {
                    var v = new LifelineVersionInfo();
                    if (v.HelperInstalled && !v.VersionsMatch)
                        LifelineLog.Write(ProcessLabel(),
                            $"WARNING: helper v{v.HelperVersion} != plugin v{v.PluginVersion} — reinstall the helper");
                }
                catch { }

                if (IsLifelineRestart())
                {
                    LifelineLog.Write(ProcessLabel(), "detected --lifeline-restart; will trigger VibeSkua grid view after login");
                    _gridTriggerCts = new CancellationTokenSource();
                    CancellationToken token = _gridTriggerCts.Token;
                    _ = Task.Run(() => SignalGridAfterLoginAsync(token), token);
                }
            }
            catch { }
        });
    }

    public void Unload()
    {
        try { _gridTriggerCts?.Cancel(); } catch { }
        try { _gridTriggerCts?.Dispose(); } catch { }
        _gridTriggerCts = null;

        try { _trayPollCts?.Cancel(); } catch { }
        try { _trayPollCts?.Dispose(); } catch { }
        _trayPollCts = null;

        try { _gridRequestWatcher?.Dispose(); } catch { }
        _gridRequestWatcher = null;

        try { _openLogWatcher?.Dispose(); } catch { }
        _openLogWatcher = null;

        try { _openSettingsWatcher?.Dispose(); } catch { }
        _openSettingsWatcher = null;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { _settingsWindow?.Close(); } catch { }
            try { _logWindow?.Close(); } catch { }
        });

        try { _helper?.RemoveMenuButton(MenuSettings); } catch { }
        try { _service?.Dispose(); } catch { }
        _service = null;
    }

    private void ClearLogs()
    {
        try { File.WriteAllText(LifelineLog.FilePath, ""); } catch { }
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string helperLog1 = Path.Combine(appData, "VibeSkua", "lifeline", "helper.log");
            if (File.Exists(helperLog1)) File.WriteAllText(helperLog1, "");
        }
        catch { }
    }

    private string ProcessLabel()
    {
        if (ProcessKind.IsHost) return "host";
        string? user = _bot?.Player?.Username;
        if (string.IsNullOrEmpty(user)) return "child";
        return GetSafeLabel();
    }

    private string GetSafeLabel()
    {
        string? user = _bot?.Player?.Username;
        if (string.IsNullOrEmpty(user)) return "?";
        try
        {
            var accounts = _bot?.Accounts?.GetAllAccounts();
            if (accounts != null)
            {
                var acc = accounts.FirstOrDefault(a => a.Username.Equals(user, StringComparison.OrdinalIgnoreCase));
                if (acc != null && !string.IsNullOrWhiteSpace(acc.DisplayName))
                    return acc.DisplayName;
            }
        }
        catch { }
        return user;
    }

    /// <summary>
    /// Forces a window to the front. Windows restricts background processes
    /// from stealing focus, so simply calling Activate() often only flashes
    /// the taskbar. Toggling Topmost trick forces the OS to bring it forward.
    /// </summary>
    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        
        window.Activate();
        
        // The Topmost toggle bypasses Windows foreground lock restrictions
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void ShowSettings()
    {
        if ((DateTime.UtcNow - _lastSettingsOpenUtc) < TrayDebounce)
        {
            LifelineLog.Write(ProcessLabel(), "settings open: debounced (recently opened)");
            return;
        }
        _lastSettingsOpenUtc = DateTime.UtcNow;

        try
        {
            IOptionContainer? oc = _optionContainer ?? _plugins?.GetContainer(this)?.OptionContainer;
            if (oc is null)
            {
                LifelineLog.Write(ProcessLabel(), "settings window failed: options container unavailable");
                return;
            }
            oc.Load();
            _optionContainer = oc;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_settingsWindow is { IsVisible: true })
                {
                    BringToFront(_settingsWindow);
                    return;
                }

                Action? forceRestart = null;
                if (_service != null)
                {
                    forceRestart = () =>
                    {
                        if (_dialogs?.ShowMessageBox("Are you sure you want to restart the client?", "Force Restart", true) == true)
                        {
                            try
                            {
                                var writer = new RestartRequestWriter(
                                    _bot!,
                                    msg => LifelineLog.Write(ProcessLabel(), msg),
                                    () => 100,
                                    () => "8.8.8.8",
                                    () => 2000);
                                var ctx = writer.CaptureCurrent();
                                writer.Write("manual-test-restart", ctx);
                                Environment.Exit(0);
                            }
                            catch { Environment.Exit(0); }
                        }
                    };
                }

                Action? testGrid = null;
                if (_service != null || ProcessKind.IsHost)
                {
                    testGrid = () =>
                    {
                        try
                        {
                            if (ProcessKind.IsHost) TryInvokeGridViewAction("toggle");
                            else WriteGridRequest("toggle");
                        }
                        catch (Exception ex)
                        {
                            LifelineLog.Write(ProcessLabel(), $"test grid failed: {ex.Message}");
                        }
                    };
                }

                try
                {
                    _settingsWindow = new LifelineSettingsWindow(oc, OpenLog, forceRestart, testGrid, EnsureHelperRunning);
                    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                    _settingsWindow.Show();
                    
                    BringToFront(_settingsWindow);
                    
                    LifelineLog.Write(ProcessLabel(), "settings window shown");
                }
                catch (Exception ex)
                {
                    LifelineLog.Write(ProcessLabel(), $"CRITICAL: Failed to load settings window: {ex.Message}");
                    _dialogs?.ShowMessageBox($"Failed to open Lifeline settings:\n{ex.Message}", "Lifeline Error");
                }
            });
        }
        catch (Exception ex)
        {
            LifelineLog.Write(ProcessLabel(), $"settings window failed: {ex.Message}");
        }
    }

    private void OpenLog()
    {
        if ((DateTime.UtcNow - _lastLogOpenUtc) < TrayDebounce)
        {
            LifelineLog.Write(ProcessLabel(), "log open: debounced (recently opened)");
            return;
        }
        _lastLogOpenUtc = DateTime.UtcNow;

        try
        {
            string path = LifelineLog.FilePath;
            if (!File.Exists(path))
                LifelineLog.Write(ProcessLabel(), "log opened (created on first write).");

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_logWindow is { IsVisible: true })
                {
                    BringToFront(_logWindow);
                    return;
                }

                _logWindow = new LifelineLogWindow(path);
                _logWindow.Closed += (_, _) => _logWindow = null;
                _logWindow.Show();
                
                BringToFront(_logWindow);
                
                LifelineLog.Write(ProcessLabel(), "log window shown");
            });
        }
        catch (Exception ex)
        {
            try { LifelineLog.Write(ProcessLabel(), $"log window failed: {ex.Message}"); } catch { }
        }
    }

    private void StartTrayRequestHandling()
    {
        try
        {
            string dir = LifelineRequestDir();
            Directory.CreateDirectory(dir);

            foreach (string oldName in new[] { "openlog.request", "opensettings.request" })
            {
                try { File.Delete(Path.Combine(dir, oldName)); } catch { }
            }

            SweepTrayRequests(dir);

            _openLogWatcher = new FileSystemWatcher(dir, "openlog.*.request")
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _openLogWatcher.Created += (_, e) =>
            {
                if (TryClaimRequest(e.FullPath, "log"))
                    Avalonia.Threading.Dispatcher.UIThread.Post(OpenLog);
            };

            _openSettingsWatcher = new FileSystemWatcher(dir, "opensettings.*.request")
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _openSettingsWatcher.Created += (_, e) =>
            {
                if (TryClaimRequest(e.FullPath, "settings"))
                    ShowSettings();
            };

            _trayPollCts = new CancellationTokenSource();
            CancellationToken ct = _trayPollCts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                    catch (OperationCanceledException) { return; }
                    if (ct.IsCancellationRequested) return;
                    SweepTrayRequests(dir);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            LifelineLog.Write(ProcessLabel(), $"tray request handling failed to start: {ex.Message}");
        }
    }

    private void SweepTrayRequests(string dir)
    {
        try
        {
            var now = DateTime.UtcNow;

            foreach (string f in Directory.EnumerateFiles(dir, "openlog.*.request"))
            {
                try
                {
                    if ((now - File.GetLastWriteTimeUtc(f)) > TrayRequestFreshness)
                    {
                        LifelineLog.Write(ProcessLabel(), $"sweep: deleted stale {Path.GetFileName(f)}");
                        File.Delete(f);
                        continue;
                    }
                    if (TryClaimRequest(f, "log"))
                    {
                        LifelineLog.Write(ProcessLabel(), "sweep: claimed log request");
                    }
                }
                catch { }
            }

            foreach (string f in Directory.EnumerateFiles(dir, "opensettings.*.request"))
            {
                try
                {
                    if ((now - File.GetLastWriteTimeUtc(f)) > TrayRequestFreshness)
                    {
                        LifelineLog.Write(ProcessLabel(), $"sweep: deleted stale {Path.GetFileName(f)}");
                        File.Delete(f);
                        continue;
                    }
                    if (TryClaimRequest(f, "settings"))
                    {
                        LifelineLog.Write(ProcessLabel(), "sweep: claimed settings request");
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string LifelineRequestDir()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VibeSkua", "lifeline");
    }

    private bool TryClaimRequest(string path, string kind)
    {
        string file = Path.GetFileName(path);

        if (!ProcessKind.IsHost && HostProcessAlive())
        {
            return false;
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);

                LifelineLog.Write(ProcessLabel(), $"claim WIN {kind} pid={Environment.ProcessId} file={file} attempt={attempt + 1}");
                return true;
            }
            catch (FileNotFoundException)
            {
                LifelineLog.Write(ProcessLabel(), $"claim LOSE {kind} pid={Environment.ProcessId} file={file} (already gone)");
                return false;
            }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException)
            {
                if (attempt < 19) Thread.Sleep(25 + (Environment.ProcessId % 25));
            }
            catch (UnauthorizedAccessException)
            {
                LifelineLog.Write(ProcessLabel(), $"claim LOSE {kind} pid={Environment.ProcessId} file={file} (unauthorized)");
                return false;
            }
            catch (Exception ex)
            {
                LifelineLog.Write(ProcessLabel(), $"claim LOSE {kind} pid={Environment.ProcessId} file={file} ({ex.GetType().Name})");
                return false;
            }
        }

        LifelineLog.Write(ProcessLabel(), $"claim LOSE {kind} pid={Environment.ProcessId} file={file} (20 attempts exhausted)");
        return false;
    }

    private static bool? _hostAliveCache;

    private static bool HostProcessAlive()
    {
        if (_hostAliveCache.HasValue) return _hostAliveCache.Value;

        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host-pid" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int hostPid) && hostPid > 0)
                    {
                        try
                        {
                            using var p = Process.GetProcessById(hostPid);
                            _hostAliveCache = !p.HasExited;
                            return _hostAliveCache.Value;
                        }
                        catch
                        {
                            _hostAliveCache = false;
                            return false;
                        }
                    }
                }
            }
        }
        catch { }

        _hostAliveCache = false;
        return false;
    }

    public void EnsureHelperRunning()
    {
        if (IsHelperRunning())
        {
            LifelineLog.Write(ProcessLabel(), "LifelineHelper already running.");
            return;
        }

        string[] candidates = new[]
        {
            Path.Combine(ClientFileSources.SkuaPluginsDIR, "Lifeline", "LifelineHelper.exe"),
            Path.Combine(ClientFileSources.SkuaDIR, "lifeline", "LifelineHelper.exe"),
            Path.Combine(AppContext.BaseDirectory, "LifelineHelper.exe"),
        };
        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            LifelineLog.Write(ProcessLabel(), "LifelineHelper.exe not found — install it manually. See README.");
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
            LifelineLog.Write(ProcessLabel(), $"started LifelineHelper from {exe}");
        }
        catch (Exception ex)
        {
            LifelineLog.Write(ProcessLabel(), $"could not start LifelineHelper: {ex.Message}");
        }
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

    private static bool IsLifelineRestart()
    {
        try
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.Equals("--lifeline-restart", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    private bool GetOptionBool(string name, bool fallback)
    {
        try
        {
            IOptionContainer? oc = _optionContainer ?? _plugins?.GetContainer(this)?.OptionContainer;
            if (oc is null) return fallback;
            IOption? opt = oc.Options.FirstOrDefault(x => x.Name == name);
            if (opt is null) return fallback;
            string raw = oc.GetDirect(opt);
            if (bool.TryParse(raw, out bool parsed)) return parsed;
            return fallback;
        }
        catch { return fallback; }
    }

    private static string GridRequestDir =>
        Path.Combine(ClientFileSources.SkuaDIR, "lifeline", "gridrequest");

    private async Task SignalGridAfterLoginAsync(CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < 300 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    if (_bot?.Player?.Playing == true)
                    {
                        if (!GetOptionBool("restartGridOnLogin", true))
                        {
                            LifelineLog.Write(ProcessLabel(),
                                "logged in, but restartGridOnLogin is off — skipping grid view trigger");
                            return;
                        }
                        WriteGridRequest("enter");
                        return;
                    }
                }
                catch { }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }

            LifelineLog.Write(ProcessLabel(),
                "no login within 5 minutes — skipping grid view trigger");
        }
        catch (Exception ex)
        {
            LifelineLog.Write(ProcessLabel(),
                $"grid trigger failed: {ex.Message}");
        }
    }

    private void WriteGridRequest(string action = "enter")
    {
        try
        {
            Directory.CreateDirectory(GridRequestDir);
            string path = Path.Combine(GridRequestDir, $"{Environment.ProcessId}.json");
            string user = _bot?.Player?.Username ?? "";
            string payload = $"{{\"pid\":{Environment.ProcessId},\"user\":\"{user}\",\"at\":\"{DateTime.UtcNow:O}\",\"action\":\"{action}\"}}";
            File.WriteAllText(path, payload);
            LifelineLog.Write(ProcessLabel(), $"wrote grid request (action={action}): {path}");
        }
        catch (Exception ex)
        {
            LifelineLog.Write(ProcessLabel(),
                $"grid request write failed: {ex.Message}");
        }
    }

    private void StartGridRequestWatcher()
    {
        try
        {
            Directory.CreateDirectory(GridRequestDir);

            foreach (string file in Directory.GetFiles(GridRequestDir, "*.json"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(5))
                    {
                        File.Delete(file);
                        continue;
                    }

                    string content = File.ReadAllText(file);
                    if (content.Contains("\"action\":\"toggle\"", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        continue;
                    }

                    HandleGridRequest(file, content);
                }
                catch { }
            }

            _gridRequestWatcher = new FileSystemWatcher(GridRequestDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _gridRequestWatcher.Created += OnGridRequest;

            LifelineLog.Write("host", $"grid request watcher started on {GridRequestDir}");
        }
        catch (Exception ex)
        {
            LifelineLog.Write("host", $"grid request watcher failed to start: {ex.Message}");
        }
    }

    private void OnGridRequest(object sender, FileSystemEventArgs e)
    {
        _ = Task.Delay(150).ContinueWith(_ => HandleGridRequest(e.FullPath, null), TaskScheduler.Default);
    }

    private void HandleGridRequest(string path, string? content)
    {
        try
        {
            string action = "enter";
            try
            {
                content ??= File.ReadAllText(path);
                int idx = content.IndexOf("\"action\":", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int start = content.IndexOf('"', idx + 9);
                    if (start >= 0)
                    {
                        int end = content.IndexOf('"', start + 1);
                        if (end > start)
                            action = content.Substring(start + 1, end - start - 1);
                    }
                }
            }
            catch { }

            try { if (File.Exists(path)) File.Delete(path); } catch { }

            Action invokeAction = () => TryInvokeGridViewAction(action);
            if (_dispatcher != null)
                _dispatcher.Invoke(invokeAction);
            else
                invokeAction();
        }
        catch (Exception ex)
        {
            LifelineLog.Write("host", $"grid request handling failed: {ex.Message}");
        }
    }

    private void TryInvokeGridViewAction(string action)
    {
        try
        {
            object? app = FindAvaloniaApplicationCurrent();
            if (app is null)
            {
                LifelineLog.Write("host", "grid action: Avalonia.Application.Current not available (non-Avalonia host?)");
                return;
            }

            object? lifetime = app.GetType()
                .GetProperty("ApplicationLifetime", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(app);
            object? mainWindow = lifetime?.GetType()
                .GetProperty("MainWindow", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(lifetime);
            if (mainWindow is null)
            {
                LifelineLog.Write("host", "grid action: MainWindow not available");
                return;
            }

            string methodName;
            if (action.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                FieldInfo? gridEnabledField = mainWindow.GetType()
                    .GetField("_isGridViewEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
                bool isGridEnabled = gridEnabledField?.GetValue(mainWindow) is bool b && b;
                methodName = isGridEnabled ? "ExitGridView" : "EnterGridView";
            }
            else if (action.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                methodName = "ExitGridView";
            }
            else
            {
                methodName = "EnterGridView";
            }

            MethodInfo? method = mainWindow.GetType()
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method is null)
            {
                LifelineLog.Write("host", $"grid action: {methodName} method not found on {mainWindow.GetType().FullName}");
                return;
            }

            method.Invoke(mainWindow, null);
            LifelineLog.Write("host", $"grid action: {methodName} invoked on {mainWindow.GetType().Name}");
        }
        catch (Exception ex)
        {
            LifelineLog.Write("host", $"grid action failed: {ex.Message}");
        }
    }

    private static object? FindAvaloniaApplicationCurrent()
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? appType = asm.GetType("Avalonia.Application");
                if (appType is null) continue;
                PropertyInfo? prop = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                if (prop is not null) return prop.GetValue(null);
            }
            catch { }
        }
        return null;
    }
}

internal sealed class LifelineOpt : IOption
{
    public string Category { get; set; } = "Lifeline";
    public string Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public object? DefaultValue { get; }
    public bool Transient { get; private set; }
    public Type Type { get; }

    private LifelineOpt(string name, string display, string desc, object? value, Type type)
    {
        Name = name; DisplayName = display; Description = desc; DefaultValue = value; Type = type;
    }

    public static LifelineOpt Bool(string n, string d, string desc, bool v)     => new(n, d, desc, v, typeof(bool));
    public static LifelineOpt Int(string n, string d, string desc, int v)       => new(n, d, desc, v, typeof(int));
    public static LifelineOpt String(string n, string d, string desc, string v) => new(n, d, desc, v, typeof(string));
}
