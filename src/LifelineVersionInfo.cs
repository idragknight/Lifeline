using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Skua.Core.Models;

namespace Lifeline;

/// <summary>
/// Snapshot of the plugin's version, the on-disk helper's version, and the
/// version of the helper that is actually *running* on the machine.
///
/// The running helper's version is read from a small JSON file the helper
/// writes at startup (%APPDATA%\VibeSkua\lifeline\helper.version). Reading
/// the on-disk EXE only tells you what *would* start next launch — not what
/// is handling requests right now. Both values matter for support: a user who
/// rebuilt the helper without restarting it will have a "running" version
/// that is older than the "installed" one, and that is exactly the situation
/// that silently fails to process requests.
///
/// Kept `internal` on purpose. This is a plugin implementation detail, not
/// part of any public surface. The settings window is also `internal`, so it
/// can accept an `internal` value type without triggering CS0051.
/// </summary>
internal sealed class LifelineVersionInfo
{
    /// <summary>Short plugin version, e.g. "1.0.0".</summary>
    public string PluginVersion { get; }

    /// <summary>Commit hash embedded by the SDK (or null if not built from a git checkout).</summary>
    public string? PluginCommit { get; }

    /// <summary>Plugin version with "+commit" suffix when available.</summary>
    public string PluginFullVersion => PluginCommit is null
        ? PluginVersion
        : $"{PluginVersion}+{PluginCommit}";

    /// <summary>On-disk helper EXE version, or "Not found".</summary>
    public string HelperVersion { get; }

    /// <summary>Commit hash of the on-disk helper, when present.</summary>
    public string? HelperCommit { get; }

    /// <summary>On-disk helper version with "+commit" suffix, or "not installed".</summary>
    public string HelperFullVersion => HelperInstalled
        ? (HelperCommit is null ? HelperVersion : $"{HelperVersion}+{HelperCommit}")
        : "not installed";

    /// <summary>Version reported by the helper process that is currently running, or null if none.</summary>
    public string? RunningHelperVersion { get; }

    /// <summary>PID of the currently running helper, or null.</summary>
    public int? RunningHelperPid { get; }

    /// <summary>True when a LifelineHelper.exe was located on disk.</summary>
    public bool HelperInstalled { get; }

    /// <summary>True when plugin and on-disk helper share the same short version.</summary>
    public bool VersionsMatch { get; }

    /// <summary>True when the running helper has a different version than the plugin.</summary>
    public bool RunningHelperOutdated => RunningHelperVersion is not null
        && !string.Equals(RunningHelperVersion, PluginFullVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Compact one-line label for the settings window footer.</summary>
    public string ShortLabel { get; }

    /// <summary>Multi-line block suitable for a support ticket ("Copy Diagnostics").</summary>
    public string DiagnosticsSummary { get; }

    public LifelineVersionInfo()
    {
        (PluginVersion, PluginCommit) = ReadPluginVersion();

        string? helperPath = GetHelperPath();
        if (helperPath is not null)
        {
            HelperInstalled = true;
            (HelperVersion, HelperCommit) = ReadExeVersion(helperPath);
        }
        else
        {
            HelperInstalled = false;
            HelperVersion = "Not found";
            HelperCommit = null;
        }

        ReadRunningHelper(out int? pid, out string? version);
        RunningHelperPid = pid;
        RunningHelperVersion = version;

        VersionsMatch = HelperInstalled
            && string.Equals(PluginVersion, HelperVersion, StringComparison.OrdinalIgnoreCase);

        ShortLabel = BuildShortLabel();
        DiagnosticsSummary = BuildDiagnostics();
    }

    private string BuildShortLabel()
    {
        if (!HelperInstalled) return $"v{PluginVersion} · helper not found";
        if (!VersionsMatch) return $"v{PluginVersion} · helper {HelperVersion}";
        return $"v{PluginVersion}";
    }

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Lifeline diagnostics");
        sb.AppendLine($"  plugin          : {PluginFullVersion}");
        sb.AppendLine($"  helper (on disk): {HelperFullVersion}");
        sb.AppendLine(RunningHelperVersion is not null
            ? $"  helper (running): {RunningHelperVersion} (pid {RunningHelperPid})"
            : "  helper (running): not running");
        sb.AppendLine($"  versions match  : {VersionsMatch}");
        sb.AppendLine($"  plugin pid      : {Environment.ProcessId}");
        sb.AppendLine($"  os              : {Environment.OSVersion}");
        sb.AppendLine($"  runtime         : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  generated (utc) : {DateTime.UtcNow:O}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    //  Plugin version
    // ------------------------------------------------------------------
    private static (string Version, string? Commit) ReadPluginVersion()
    {
        try
        {
            Assembly asm = typeof(LifelineVersionInfo).Assembly;

            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return SplitInformational(info);

            Version? v = asm.GetName().Version;
            if (v is not null)
                return (FormatVersion(v), null);

            return ("Unknown", null);
        }
        catch
        {
            return ("Unknown", null);
        }
    }

    // ------------------------------------------------------------------
    //  On-disk EXE version (helper)
    // ------------------------------------------------------------------
    private static (string Version, string? Commit) ReadExeVersion(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

            // .NET Core 3.0+ populates ProductVersion from AssemblyInformationalVersion.
            // That is the string we want — it carries the +commit suffix when present.
            string? product = info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(product))
                return SplitInformational(product);

            if (Version.TryParse(info.FileVersion ?? "", out Version? v))
                return (FormatVersion(v), null);

            return ("Unknown", null);
        }
        catch
        {
            return ("Unknown", null);
        }
    }

    private static (string Version, string? Commit) SplitInformational(string raw)
    {
        int plus = raw.IndexOf('+');
        if (plus < 0) return (raw, null);
        return (raw[..plus], raw[(plus + 1)..]);
    }

    private static string FormatVersion(Version v)
    {
        // Version("1.0") gives Build = -1. Guarding here so we never print "1.0.-1".
        return v.Build >= 0
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : $"{v.Major}.{v.Minor}";
    }

    // ------------------------------------------------------------------
    //  Running helper version
    // ------------------------------------------------------------------
    private static void ReadRunningHelper(out int? pid, out string? version)
    {
        pid = null;
        version = null;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        {
            Path.Combine(appData, "VibeSkua", "lifeline", "helper.version"),
            Path.Combine(appData, "Skua",     "lifeline", "helper.version"),
        };

        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("pid", out JsonElement p) && p.TryGetInt32(out int pv))
                    pid = pv;

                if (root.TryGetProperty("version", out JsonElement v))
                    version = v.GetString();

                break;
            }
            catch { }
        }

        // A version file with a dead PID means the helper exited without cleaning
        // up (crash, forced reboot). Treat that as "not running".
        if (pid.HasValue && !IsProcessAlive(pid.Value))
        {
            pid = null;
            version = null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    //  Paths
    // ------------------------------------------------------------------
    private static string? GetHelperPath()
    {
        string[] candidates =
        {
            Path.Combine(ClientFileSources.SkuaPluginsDIR, "Lifeline", "LifelineHelper.exe"),
            Path.Combine(ClientFileSources.SkuaDIR,        "lifeline", "LifelineHelper.exe"),
            Path.Combine(AppContext.BaseDirectory,                    "LifelineHelper.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
