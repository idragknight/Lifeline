using System.Reflection;

namespace Lifeline;

/// <summary>
/// Distinguishes the VibeSkua tab host process from its child client processes.
/// The plugin must NEVER run recovery or write restart requests in the host —
/// Environment.Exit in the host would kill every child.
/// </summary>
internal static class ProcessKind
{
    private static bool? _isHost;

    public static bool IsHost => _isHost ??= Detect();

    public static string Label(string? username)
    {
        if (IsHost) return "host";
        return string.IsNullOrEmpty(username) ? "child" : username!;
    }

    private static bool Detect()
    {
        try
        {
            if (Environment.GetCommandLineArgs().Any(a => a == "--embed"))
                return false;
        }
        catch { }

        try
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? hotKeys = asm.GetType("Skua.Core.AppStartup.HotKeys");
                if (hotKeys is null) continue;
                PropertyInfo? prop = hotKeys.GetProperty("IsHostProcess", BindingFlags.Public | BindingFlags.Static);
                if (prop?.GetValue(null) is bool b)
                    return b;
            }
        }
        catch { }

        return false;
    }
}
