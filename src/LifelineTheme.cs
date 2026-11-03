using Avalonia.Controls;
using Skua.Shared.Avalonia.Services;

namespace Lifeline;

/// <summary>
/// Wires a plugin window into VibeSkua's theme system so it matches the host:
/// <list type="bullet">
///   <item>The window's <c>RequestedThemeVariant</c> is aligned with the active theme (so every
///         <c>DynamicResource</c> in the XAML resolves against the correct variant dictionary).</item>
///   <item>The native DWM window frame (resize border / dark-mode flag) is applied once the HWND
///         exists. This is the same call VibeSkua's own <c>DialogService</c> makes for its dialogs.</item>
/// </list>
/// The <c>DynamicResource</c> bindings in the XAML already track the Skua brushes in
/// <c>Application.Resources</c>; <c>ThemeResourceApplicator.ApplyAccentBrushes</c> mutates those
/// brushes in place, so the window body re-colors automatically whenever the active theme changes.
/// </summary>
internal static class LifelineTheme
{
    public static void Apply(Window window)
    {
        try
        {
            // Passing no isDarkTheme lets ThemeResourceApplicator infer it from
            // window.ActualThemeVariant / Application.RequestedThemeVariant - no IThemeService
            // lookup needed (the plugin has no reference to CommunityToolkit.Mvvm.DependencyInjection).
            ThemeResourceApplicator.ApplyTitleBarTheme(window);
        }
        catch
        {
            // A theme failure must never stop a diagnostic/log window from opening.
        }
    }
}
