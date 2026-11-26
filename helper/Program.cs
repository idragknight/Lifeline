using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace LifelineHelper;

internal static class Program
{
    private static readonly string[] RequestDirs =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua", "lifeline", "requests"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "lifeline", "requests"),
    };

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeSkua", "lifeline", "helper.log");

    private static readonly object LogLock = new();
    private static readonly ConcurrentDictionary<string, byte> ActiveFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PidDeathTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleProcessingAge = TimeSpan.FromMinutes(15);

    private static int Main(string[] args)
    {
        using Mutex mutex = new(true, @"Global\LifelineHelperMutex", out bool createdNew);
        if (!createdNew)
        {
            try { Console.Error.WriteLine("LifelineHelper is already running."); } catch { }
            return 1;
        }

        Log($"LifelineHelper starting (pid {Environment.ProcessId})");
        WriteVersionFile();

        List<FileSystemWatcher> watchers = new();
        try
        {
            foreach (string dir in RequestDirs)
            {
                Directory.CreateDirectory(dir);
                ProcessPending(dir);

                FileSystemWatcher watcher = new(dir, "*.restart.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += (_, e) => _ = Task.Run(() => ProcessRequest(e.FullPath));
                watchers.Add(watcher);
            }

            Log("Watching for restart requests. Ctrl+C to stop.");

            using ManualResetEventSlim shutdown = new(false);

            _ = Task.Run(async () =>
            {
                while (!shutdown.IsSet)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30)); } catch { }
                    if (shutdown.IsSet) return;
                    foreach (string dir in RequestDirs)
                    {
                        try { ProcessPending(dir); } catch { }
                    }
                }
            });

            Thread trayThread = new(() => RunTrayIcon(shutdown))
            {
                IsBackground = true
            };
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.Start();

            try { Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Set(); }; } catch { }
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { shutdown.Set(); } catch { } }; } catch { }

            int hostPid = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host-pid" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out hostPid);
                    break;
                }
            }

            if (hostPid > 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var host = Process.GetProcessById(hostPid);
                        Log($"Monitoring host process (pid {hostPid})...");
                        host.WaitForExit();
                        Log($"Host process (pid {hostPid}) exited. Shutting down helper.");
                    }
                    catch (ArgumentException)
                    {
                        Log($"Host process (pid {hostPid}) not found. Shutting down helper.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error monitoring host process: {ex.Message}");
                    }
                    finally
                    {
                        shutdown.Set();
                    }
                });
            }

            shutdown.Wait();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            return 2;
        }
        finally
        {
            foreach (FileSystemWatcher w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
        }
    }

    private static void RunTrayIcon(ManualResetEventSlim shutdown)
    {
        try
        {
            Icon? icon = null;
            try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
            if (icon == null) icon = SystemIcons.Application;

            NotifyIcon tray = new()
            {
                Icon = icon,
                Text = "Lifeline Helper is running",
                Visible = true
            };

            ContextMenuStrip menu = new();
            var header = new ToolStripMenuItem("Lifeline Helper") { Enabled = false, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Log Window", null, (_, _) => SendOpenLogRequest());
            menu.Items.Add("Open Plugin Settings", null, (_, _) => SendOpenSettingsRequest());
            menu.Items.Add("Open Install Folder", null, (_, _) =>
            {
                try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) =>
            {
                tray.Visible = false;
                shutdown.Set();
                Application.Exit();
            });

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => SendOpenLogRequest();
            tray.ShowBalloonTip(3000, "Lifeline Helper", "Running in the background. Watching for restart requests.", ToolTipIcon.Info);

            Task.Run(async () =>
            {
                while (!shutdown.IsSet)
                {
                    await Task.Delay(500);
                }
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                }
                Application.Exit();
            });

            Application.Run();
        }
        catch (Exception ex)
        {
            Log($"Tray icon failed: {ex.Message}");
        }
    }

    private static void SendOpenLogRequest()
    {
        try
        {
            string dir = LifelineDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"openlog.{Guid.NewGuid():N}.request");
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            Log($"tray: wrote {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log($"tray: could not write openlog request: {ex.Message}");
        }
    }

    private static void SendOpenSettingsRequest()
    {
        try
        {
            string dir = LifelineDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"opensettings.{Guid.NewGuid():N}.request");
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            Log($"tray: wrote {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log($"tray: could not write opensettings request: {ex.Message}");
        }
    }

    private static string LifelineDir()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VibeSkua", "lifeline");
    }

    private static void WriteVersionFile()
    {
        try
        {
            string version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";

            string json = JsonSerializer.Serialize(new
            {
                version,
                pid = Environment.ProcessId,
                startedAt = DateTime.UtcNow.ToString("O"),
            });

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string root in new[] { "VibeSkua", "Skua" })
            {
                try
                {
                    string dir = Path.Combine(appData, root, "lifeline");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "helper.version"), json);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ProcessPending(string dir)
    {
        // 1. Clean up stale .processing files
        foreach (string file in Directory.EnumerateFiles(dir, "*.restart.processing"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleProcessingAge)
                {
                    Log($"deleting stale processing file: {Path.GetFileName(file)}");
                    File.Delete(file);
                }
            }
            catch { }
        }

        // 2. Clean up stale .json files that are hopelessly stuck (prevents infinite loops)
        foreach (string file in Directory.EnumerateFiles(dir, "*.restart.json"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleProcessingAge)
                {
                    string failedPath = file + ".failed";
                    try 
                    { 
                        File.Move(file, failedPath, overwrite: true); 
                        Log($"moved stale unclaimed request to failed: {Path.GetFileName(failedPath)}");
                    }
                    catch 
                    { 
                        File.Delete(file); 
                        Log($"deleted stale unclaimed request file: {Path.GetFileName(file)}");
                    }
                }
            }
            catch { }
        }

        // 3. Process fresh .json files
        foreach (string file in Directory.EnumerateFiles(dir, "*.restart.json"))
        {
            if (File.Exists(file))
            {
                _ = Task.Run(() => ProcessRequest(file));
            }
        }
    }

    private static void ProcessRequest(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath)) return;
        
        if (!ActiveFiles.TryAdd(normalizedPath, 0)) return;

        string processing = normalizedPath + ".processing";
        
        try
        {
            bool claimed = false;
            string? lastIoError = null;
            
            // Increased to 60 attempts @ 100ms (6 seconds) to give AV/OneDrive time to release the lock
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    File.Move(normalizedPath, processing);
                    claimed = true;
                    break;
                }
                catch (FileNotFoundException) { return; }
                catch (IOException ex)
                {
                    lastIoError = ex.Message;
                    if (attempt < 59) Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Log($"claim failed: {ex.Message}");
                    return;
                }
            }

            if (!claimed)
            {
                string reason = string.IsNullOrWhiteSpace(lastIoError) ? "unknown lock" : lastIoError;
                Log($"could not claim {Path.GetFileName(normalizedPath)} after 60 attempts ({reason}); will sweep later");
                return;
            }

            bool keep = false;
            try
            {
                RestartRequest? req;
                try { req = JsonSerializer.Deserialize<RestartRequest>(File.ReadAllText(processing)); }
                catch (Exception ex) { Log($"parse failed: {ex.Message}"); return; }

                if (req is null) { Log("empty request"); return; }
                // ==========================================
                // NEW: HANDLE UPDATE REQUEST
                // ==========================================
                if (req.Action.Equals("update", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Update requested by {req.Username}. Waiting for client to exit...");
                    if (req.Pid > 0 && !WaitForExit(req.Pid))
                    {
                        Log("Update aborted: Could not close client.");
                        return;
                    }

                    string stagingDir = req.StagingDir ?? "";
                    if (string.IsNullOrWhiteSpace(stagingDir) || !Directory.Exists(stagingDir))
                    {
                        Log("Update failed: Staging directory not found.");
                        return;
                    }

                    try
                    {
                        // 1. Update the Plugin DLL
                        string pluginDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua", "plugins");
                        string newPluginDll = Path.Combine(stagingDir, "Lifeline.dll");
                        if (File.Exists(newPluginDll))
                        {
                            File.Copy(newPluginDll, Path.Combine(pluginDir, "Lifeline.dll"), true);
                            Log("Updated Lifeline.dll");
                        }

                        // 2. Update the Helper Files (Self-update trick)
                        string helperDir = AppDomain.CurrentDomain.BaseDirectory;
                        string[] helperFiles = { "LifelineHelper.exe", "LifelineHelper.dll", "LifelineHelper.deps.json", "LifelineHelper.runtimeconfig.json" };
                        
                        foreach (string file in helperFiles)
                        {
                            string source = Path.Combine(stagingDir, file);
                            string dest = Path.Combine(helperDir, file);
                            if (File.Exists(source))
                            {
                                if (file == "LifelineHelper.exe")
                                {
                                    // Windows locks running EXEs. Rename the current one to .old, then copy the new one.
                                    string oldExe = dest + ".old";
                                    if (File.Exists(oldExe)) File.Delete(oldExe);
                                    File.Move(dest, oldExe); 
                                }
                                File.Copy(source, dest, true);
                                Log($"Updated {file}");
                            }
                        }

                        Log("Update complete! Relaunching client with original script...");
                        
                        // Clean up staging folder
                        try { Directory.Delete(stagingDir, true); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Log($"Update file replacement failed: {ex.Message}");
                        return;
                    }

                    // Fall through to the normal relaunch logic below! 
                    // BuildRelaunchArgs will automatically restore the --run-script argument.
                }
                // ==========================================
                // END UPDATE BLOCK
                // ==========================================
                else if (string.Equals(req.Action, "give-up", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"GIVE-UP for {req.Username}: {req.Reason}. Not restarting.");
                    return;
                }

                string scriptName = string.IsNullOrWhiteSpace(req.LoadedScript) ? "(none)" : Path.GetFileName(req.LoadedScript);
                Log($"Restart Request: {req.Username} ({req.Reason}) | PID: {req.Pid} | Script: {scriptName} (Running: {req.ScriptWasRunning})");

                if (string.Equals(req.Action, "give-up", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"GIVE-UP for {req.Username}: {req.Reason}. Not restarting.");
                    return;
                }

                if (req.Pid > 0 && !WaitForExit(req.Pid))
                {
                    keep = true;
                    return;
                }

                if (string.IsNullOrWhiteSpace(req.Exe) || !File.Exists(req.Exe))
                {
                    Log($"exe not found: '{req.Exe}' — cannot restart");
                    return;
                }

                WaitForNetwork(req);

                List<string> args = BuildRelaunchArgs(req);
                ProcessStartInfo psi = new(req.Exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(req.Exe) ?? "",
                };

                foreach (string a in args) psi.ArgumentList.Add(a);

                List<string> logArgs = new();
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i].Equals("-p", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--password", StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                        continue;
                    }
                    if (args[i].Equals("--use-theme", StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                        continue;
                    }
                    logArgs.Add(args[i].Contains(' ') ? $"\"{args[i]}\"" : args[i]);
                }

                Log($"  Relaunch args: {string.Join(" ", logArgs)}");
                Process? spawned = Process.Start(psi);
                Log($"spawned {req.Exe} (new pid {spawned?.Id})");
            }
            finally
            {
                try
                {
                    if (keep && File.Exists(processing))
                        File.Move(processing, normalizedPath, overwrite: true);
                    else if (File.Exists(processing))
                        File.Delete(processing);
                }
                catch { }
            }
        }
        finally
        {
            ActiveFiles.TryRemove(normalizedPath, out _);
        }
    }

    private static void WaitForNetwork(RestartRequest req)
    {
        if (req.PingWaitMaxSec <= 0 || string.IsNullOrWhiteSpace(req.PingTarget)) return;

        int intervalSec = req.PingIntervalSec > 0 ? req.PingIntervalSec : 5;
        int timeoutMs   = req.PingTimeoutMs   > 0 ? req.PingTimeoutMs   : 2000;
        DateTime deadline = DateTime.UtcNow.AddSeconds(req.PingWaitMaxSec);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Ping p = new();
                PingReply reply = p.Send(req.PingTarget!, timeoutMs);
                if (reply.Status == IPStatus.Success)
                {
                    Log($"Ping {req.PingTarget}: OK");
                    return;
                }
                Log($"Ping {req.PingTarget}: Failed ({reply.Status})");
            }
            catch (Exception ex) { Log($"Ping {req.PingTarget}: Error ({ex.Message})"); }

            try { Thread.Sleep(TimeSpan.FromSeconds(intervalSec)); }
            catch { return; }
        }

        Log($"Ping wait exceeded {req.PingWaitMaxSec}s — relaunching anyway");
    }

    private static List<string> BuildRelaunchArgs(RestartRequest req)
    {
        string? origRun = null;
        string? origLoad = null;
        string? origStorage = null;
        List<string> others = new();

        for (int i = 0; i < req.Args.Length; i++)
        {
            string a = req.Args[i];
            switch (a)
            {
                case "--run-script":
                    if (i + 1 < req.Args.Length) origRun = req.Args[++i];
                    break;
                case "--load-script":
                    if (i + 1 < req.Args.Length) origLoad = req.Args[++i];
                    break;
                case "--script-options-storage":
                    if (i + 1 < req.Args.Length) origStorage = req.Args[++i];
                    break;
                default:
                    others.Add(a);
                    break;
            }
        }

        string? runScript = null;
        string? loadScript = null;

        if (!string.IsNullOrWhiteSpace(req.LoadedScript))
        {
            if (req.ScriptWasRunning) runScript = req.LoadedScript;
            else loadScript = req.LoadedScript;
        }
        else
        {
            runScript = origRun;
            loadScript = origLoad;
        }

        string? storage = !string.IsNullOrWhiteSpace(req.ScriptOptionsStorage)
            ? req.ScriptOptionsStorage
            : origStorage;

        if (runScript != null)
        {
            others.Add("--run-script");
            others.Add(runScript);
        }
        else if (loadScript != null)
        {
            others.Add("--load-script");
            others.Add(loadScript);
        }

        if (storage != null)
        {
            others.Add("--script-options-storage");
            others.Add(storage);
        }

        if (!others.Contains("--lifeline-restart"))
            others.Add("--lifeline-restart");

        return others;
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using Process old = Process.GetProcessById(pid);
            Log($"Waiting for client (PID {pid}) to exit...");

            if (old.WaitForExit((int)PidDeathTimeout.TotalMilliseconds))
            {
                Log($"Client exited cleanly.");
                return true;
            }

            Log($"Client did not exit within timeout — attempting kill.");
            try
            {
                old.Kill(entireProcessTree: true);
                if (old.WaitForExit(5000))
                {
                    Log($"Client killed.");
                    return true;
                }
            }
            catch (Exception ex) { Log($"kill failed: {ex.Message}"); }

            Log($"Could not terminate client — leaving request in place");
            return false;
        }
        catch (ArgumentException)
        {
            Log($"Client PID {pid} already gone.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"WaitForExit({pid}) failed: {ex.Message}");
            return false;
        }
    }

    private static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [HELPER] {message}";
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
            try { Console.WriteLine(line); } catch { }
        }
    }
}