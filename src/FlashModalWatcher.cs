using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lifeline;

/// <summary>
/// Polls every 200 ms for the "Please reinstall CleanFlash" message box that
/// VibeSkua's FlashUtil shows when the Flash OCX fails to initialize.
///
/// v1.1.0 fix: no longer restricts matches to our own PID. The modal can be owned
/// by FlashUtil.exe (a separate process) — the previous PID filter silently
/// missed it, which is why some clients just died with no restart request. A
/// match is now accepted if the owner is (a) us, (b) an exe in the same directory
/// as us, or (c) a process named Flash*/CleanFlash* (FlashUtil, FlashPlayerUpdate).
/// That avoids false positives from other VibeSkua installs running in parallel.
///
/// v1.1.1 fix: also inspects the text of standard Win32 dialogs (#32770), because
/// the Flash Player can pop a dialog with a generic title (e.g. "SetOptions")
/// whose *body* says to reinstall CleanFlash. Also adds "SetOptions" to the
/// title patterns in case the dialog is rendered by Avalonia.
/// </summary>
internal sealed class FlashModalWatcher : IDisposable
{
    private static readonly string[] TitlePatterns =
    {
        "Clean Flash missing",
        "Flash missing",
        "reinstall Flash",
        "reinstall CleanFlash",
        "Flash Player required",
        "flash player required",
        "SetOptions",
    };

    private readonly Action<string> _log;
    private readonly Action _onDetected;
    private readonly CancellationToken _ct;
    private Thread? _thread;
    private volatile bool _disposed;
    private int _detected;

    public FlashModalWatcher(Action<string> log, Action onDetected, CancellationToken ct)
    {
        _log = log;
        _onDetected = onDetected;
        _ct = ct;
    }

    public void Start()
    {
        _thread = new Thread(WatchLoop) { IsBackground = true, Name = "Lifeline.ModalWatcher" };
        _thread.Start();
    }

    private void WatchLoop()
    {
        while (!_disposed && !_ct.IsCancellationRequested)
        {
            try
            {
                string? title = FindMatchingWindowTitle();
                if (title is not null && Interlocked.Exchange(ref _detected, 1) == 0)
                {
                    try { _log($"flash modal detected: window title='{title}'"); } catch { }
                    try { _onDetected(); } catch { }
                    return;
                }
            }
            catch { }
            try { _ct.WaitHandle.WaitOne(200); } catch { return; }
        }
    }

    private static string? FindMatchingWindowTitle()
    {
        int ourPid = Environment.ProcessId;
        string ourDir = "";
        try { ourDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ""; } catch { }
        string? found = null;

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;

                bool matches = false;
                string title = "";

                // 1. Check title
                int len = GetWindowTextLength(hWnd);
                if (len > 0)
                {
                    StringBuilder sb = new(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    title = sb.ToString();

                    foreach (string pat in TitlePatterns)
                    {
                        if (title.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches = true;
                            break;
                        }
                    }
                }

                // 2. Check dialog body text (if title didn't match)
                if (!matches)
                {
                    StringBuilder classSb = new(256);
                    GetClassName(hWnd, classSb, classSb.Capacity);
                    // #32770 is the standard Win32 dialog class
                    if (classSb.ToString().Equals("#32770", StringComparison.OrdinalIgnoreCase))
                    {
                        EnumChildWindows(hWnd, (childHwnd, _) =>
                        {
                            int cLen = GetWindowTextLength(childHwnd);
                            if (cLen > 0)
                            {
                                StringBuilder cSb = new(cLen + 1);
                                GetWindowText(childHwnd, cSb, cSb.Capacity);
                                string childText = cSb.ToString();
                                if (childText.IndexOf("reinstall CleanFlash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    childText.IndexOf("reinstall Flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    childText.IndexOf("Clean Flash missing", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matches = true;
                                    if (string.IsNullOrEmpty(title))
                                        title = "Dialog body matched 'reinstall CleanFlash'";
                                    return false; // stop enumerating children
                                }
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                }

                if (!matches) return true;

                // 3. Verify ownership
                GetWindowThreadProcessId(hWnd, out uint pid);
                if ((int)pid == ourPid) { found = title; return false; }

                string ownerExe = "", ownerName = "";
                try
                {
                    using Process p = Process.GetProcessById((int)pid);
                    try { ownerExe = p.MainModule?.FileName ?? ""; } catch { }
                    try { ownerName = p.ProcessName ?? ""; } catch { }
                }
                catch { }

                bool sameDir = !string.IsNullOrEmpty(ownerExe) && !string.IsNullOrEmpty(ourDir) &&
                    string.Equals(Path.GetDirectoryName(ownerExe), ourDir, StringComparison.OrdinalIgnoreCase);
                bool flashHelper =
                    ownerName.StartsWith("Flash", StringComparison.OrdinalIgnoreCase) ||
                    ownerName.StartsWith("CleanFlash", StringComparison.OrdinalIgnoreCase);

                if (sameDir || flashHelper)
                {
                    found = title;
                    return false;
                }
                return true;
            }
            catch { return true; }
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _thread?.Join(2000); } catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
}
