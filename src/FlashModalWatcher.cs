using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lifeline;

/// <summary>
/// Polls for the "Please reinstall CleanFlash" or "SetOptions" message box.
/// 
/// v1.2.0 fix: VibeSkua 2.0+ uses Avalonia for its UI. Avalonia draws text
/// on a Skia canvas rather than creating native HWNDs for every label. 
/// Therefore, EnumChildWindows will NEVER find the text inside the popup.
/// We must rely entirely on matching the Window Title (e.g. "SetOptions")
/// while verifying the window belongs to our process or a Flash helper.
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
        "Please log-in before starting the bot"
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
        string? found = null;

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;

                string title = "";
                int len = GetWindowTextLength(hWnd);
                if (len > 0)
                {
                    StringBuilder sb = new(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                if (string.IsNullOrWhiteSpace(title)) return true;

                // 1. Check if the title matches our patterns
                bool matches = false;
                foreach (string pat in TitlePatterns)
                {
                    if (title.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches = true;
                        break;
                    }
                }

                if (!matches) return true;

                // 2. Verify ownership (Prevents false positives from other apps)
                GetWindowThreadProcessId(hWnd, out uint pid);
                
                // If it's our own process, it's definitely VibeSkua
                if ((int)pid == ourPid) 
                { 
                    found = title; 
                    return false; 
                }

                // If it's a child process (e.g., FlashUtil spawned by VibeSkua)
                string ownerName = "";
                try
                {
                    using Process p = Process.GetProcessById((int)pid);
                    ownerName = p.ProcessName ?? "";
                }
                catch { }

                bool isFlashProcess = 
                    ownerName.StartsWith("Flash", StringComparison.OrdinalIgnoreCase) ||
                    ownerName.StartsWith("CleanFlash", StringComparison.OrdinalIgnoreCase);

                if (isFlashProcess)
                {
                    found = title;
                    return false;
                }

                // If we get here, the title matched, but it belongs to an unrelated app.
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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
}
