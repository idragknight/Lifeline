using Skua.Core.Models;

namespace Lifeline;

/// <summary>
/// Simple append-only log at %APPDATA%\VibeSkua\plugins\Lifeline\lifeline.log.
/// Rotates at 2 MB. Never throws (logging must never take the client down).
/// </summary>
internal static class LifelineLog
{
    private static readonly object _lock = new();

    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(ClientFileSources.SkuaPluginsDIR, "Lifeline");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "lifeline.log");
        }
    }

    public static void Write(string who, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{who}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                string path = FilePath;
                FileInfo info = new(path);
                if (info.Exists && info.Length > 2_000_000)
                {
                    try { File.Copy(path, path + ".old", overwrite: true); File.WriteAllText(path, ""); } catch { }
                }
                File.AppendAllText(path, line);
            }
            catch { }
        }
    }
}
