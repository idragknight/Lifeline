using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Core.Interfaces;

namespace Lifeline;

public enum LogSourceType { All, Host, Helper, Client }

/// <summary>Non-selectable sidebar group header ("SYSTEM", "CLIENTS").</summary>
public class LogGroupHeader
{
    public string Name { get; set; } = "";
}

/// <summary>Selectable sidebar source with a count badge.</summary>
public class LogSourceItem
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public bool HasCount => Count > 0;
    public LogSourceType Type { get; set; }
    public string? FilterWho { get; set; }
    public Thickness Indent { get; set; } = new Thickness(0);
}

/// <summary>One rendered log row (tag + timestamp + message).</summary>
public class LogLine
{
    public string Text { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string SourceTag { get; set; } = "";
    public IBrush SourceColor { get; set; } = Brushes.Gray;
    public IBrush TextColor { get; set; } = new SolidColorBrush(Color.Parse("#D4D4D4"));
}

// Internal: only LifelinePlugin constructs this, and nothing outside the
// assembly needs to reference the type. Keeping it internal is consistent
// with the settings window and avoids exposing types like LifelineVersionInfo
// (internal) through a public constructor.
internal partial class LifelineLogWindow : Window
{
    private readonly record struct RawLine(DateTime Timestamp, string Who, string Message, string Source);

    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<LogLine> _logLines = new();

    private LogSourceItem? _currentSource;
    private bool _suppressSelection;
    private string _lastSignature = "";

    private List<RawLine> _lastLifeline = new();
    private List<RawLine> _lastHelper = new();
    private List<RawLine> _lastAll = new();

    private static readonly IBrush HostBrush      = new SolidColorBrush(Color.Parse("#8b5cf6"));
    private static readonly IBrush HelperBrush    = new SolidColorBrush(Color.Parse("#f43f5e"));
    private static readonly IBrush ClientBrush    = new SolidColorBrush(Color.Parse("#3b82f6"));
    private static readonly IBrush GrayBrush      = new SolidColorBrush(Color.Parse("#6b7280"));
    private static readonly IBrush DefaultText    = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush ErrorText      = new SolidColorBrush(Color.Parse("#FCA5A5"));
    private static readonly IBrush WarnText       = new SolidColorBrush(Color.Parse("#FDE047"));
    private static readonly IBrush InfoText       = new SolidColorBrush(Color.Parse("#86EFAC"));

    public LifelineLogWindow(string path)
    {
        InitializeComponent();
        LifelineTheme.Apply(this);

        _path = path;
        LogList.ItemsSource = _logLines;

        _lastSignature = ComputeSignature();
        RefreshAll();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private void OnTimerTick()
    {
        if (AutoRefreshCheck?.IsChecked != true) return;
        string sig = ComputeSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        RefreshAll();
    }

    private void RefreshAll()
    {
        _lastLifeline = ReadLifelineLines();
        _lastHelper   = ReadHelperLines();
        _lastAll      = _lastLifeline.Concat(_lastHelper)
                                      .OrderBy(l => l.Timestamp)
                                      .ToList();

        RebuildSidebar();
        ApplyContent(_currentSource);
    }

    private void RebuildSidebar()
    {
        int hostCount    = _lastLifeline.Count(l => l.Who == "host");
        int helperCount  = _lastHelper.Count;
        int unknownCount = _lastLifeline.Count(l => l.Who == "?");

        var clients = _lastLifeline
            .Where(l => l.Who != "host" && l.Who != "?")
            .GroupBy(l => l.Who)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? prevKey = _currentSource is null
            ? null
            : $"{_currentSource.Type}|{_currentSource.Name}";

        var nodes = new List<object>();

        var allNode = new LogSourceItem
        {
            Name = "All Logs",
            Count = _lastAll.Count,
            Type = LogSourceType.All,
            Indent = new Thickness(0),
        };
        nodes.Add(allNode);

        nodes.Add(new LogGroupHeader { Name = "SYSTEM" });
        nodes.Add(new LogSourceItem
        {
            Name = "Host",
            Count = hostCount,
            Type = LogSourceType.Host,
            FilterWho = "host",
            Indent = new Thickness(12, 0, 0, 0),
        });
        nodes.Add(new LogSourceItem
        {
            Name = "Helper",
            Count = helperCount,
            Type = LogSourceType.Helper,
            Indent = new Thickness(12, 0, 0, 0),
        });

        if (clients.Count > 0 || unknownCount > 0)
        {
            nodes.Add(new LogGroupHeader { Name = "CLIENTS" });

            foreach (var c in clients)
            {
                nodes.Add(new LogSourceItem
                {
                    Name = c.Key,
                    Count = c.Count(),
                    Type = LogSourceType.Client,
                    FilterWho = c.Key,
                    Indent = new Thickness(12, 0, 0, 0),
                });
            }

            if (unknownCount > 0)
            {
                nodes.Add(new LogSourceItem
                {
                    Name = "unknown",
                    Count = unknownCount,
                    Type = LogSourceType.Client,
                    FilterWho = "?",
                    Indent = new Thickness(12, 0, 0, 0),
                });
            }
        }

        LogSourceItem? restored = null;
        if (prevKey != null)
        {
            restored = nodes.OfType<LogSourceItem>()
                            .FirstOrDefault(n => $"{n.Type}|{n.Name}" == prevKey);
        }
        restored ??= allNode;

        _suppressSelection = true;
        SidebarList.ItemsSource = nodes;
        SidebarList.SelectedItem = restored;
        _suppressSelection = false;

        _currentSource = restored;
    }

    private void ApplyContent(LogSourceItem? source)
    {
        _logLines.Clear();

        if (source is null)
        {
            StatusLabel.Text = "No source selected";
            return;
        }

        IEnumerable<RawLine> lines = source.Type switch
        {
            LogSourceType.All     => _lastAll,
            LogSourceType.Helper  => _lastHelper,
            LogSourceType.Host    => _lastLifeline.Where(l => l.Who == "host"),
            LogSourceType.Client  => _lastLifeline.Where(l => l.Who == (source.FilterWho ?? "")),
            _ => Enumerable.Empty<RawLine>(),
        };

        foreach (var raw in lines)
            _logLines.Add(MakeDisplayLine(raw));

        long size = 0;
        foreach (var f in EnumerateFiles())
        {
            try { size += new FileInfo(f).Length; } catch { }
        }

        StatusLabel.Text =
            $"{_logLines.Count} lines | Size: {size / 1024.0:F1} KB | Updated: {DateTime.Now:HH:mm:ss}";
    }

    private LogLine MakeDisplayLine(RawLine raw)
    {
        (string tag, IBrush color) = raw.Source switch
        {
            "host"    => ("HOST",   HostBrush),
            "helper"  => ("HELPER", HelperBrush),
            "unknown" => ("?",      GrayBrush),
            _         => (raw.Who.ToUpperInvariant(), ClientBrush),
        };

        IBrush textColor = DefaultText;
        if (Contains(raw.Message, "health: FAIL") ||
            Contains(raw.Message, "Fatal:") ||
            Contains(raw.Message, "Error"))
        {
            textColor = ErrorText;
        }
        else if (Contains(raw.Message, "warning") ||
                 Contains(raw.Message, "blip detected") ||
                 Contains(raw.Message, "stage 2"))
        {
            textColor = WarnText;
        }
        else if (Contains(raw.Message, "EnterGridView invoked") ||
                 Contains(raw.Message, "grid request"))
        {
            textColor = InfoText;
        }

        return new LogLine
        {
            Timestamp = raw.Timestamp.ToString("HH:mm:ss"),
            Text = raw.Message,
            SourceTag = tag,
            SourceColor = color,
            TextColor = textColor,
        };
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private void SidebarList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (SidebarList.SelectedItem is LogSourceItem source)
        {
            _currentSource = source;
            ApplyContent(source);
        }
        else if (SidebarList.SelectedItem is LogGroupHeader)
        {
            // Group headers are not selectable — bounce selection back.
            _suppressSelection = true;
            SidebarList.SelectedItem = _currentSource;
            _suppressSelection = false;
        }
    }

    private void GroupHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Prevent the ListBoxItem wrapper from taking selection.
        e.Handled = true;
    }

    private IEnumerable<string> EnumerateFiles()
    {
        string logDir = Path.GetDirectoryName(_path) ?? "";
        if (Directory.Exists(logDir))
        {
            foreach (var f in Directory.GetFiles(logDir, "lifeline*.log"))
                yield return f;
        }

        foreach (var h in HelperLogCandidates)
        {
            if (File.Exists(h)) yield return h;
        }
    }

    private static IEnumerable<string> HelperLogCandidates
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "VibeSkua", "lifeline", "helper.log");
            yield return Path.Combine(appData, "Skua",     "lifeline", "helper.log");
        }
    }

    private List<RawLine> ReadLifelineLines()
    {
        var result = new List<RawLine>();
        string logDir = Path.GetDirectoryName(_path) ?? "";
        if (!Directory.Exists(logDir)) return result;

        foreach (var file in Directory.GetFiles(logDir, "lifeline*.log"))
        {
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (TryParseLifelineLine(line, out var raw))
                        result.Add(raw);
                }
            }
            catch { }
        }

        return result;
    }

    private List<RawLine> ReadHelperLines()
    {
        var result = new List<RawLine>();

        foreach (var path in HelperLogCandidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (TryParseHelperLine(line, out var raw))
                        result.Add(raw);
                }
                break; // use first existing helper log
            }
            catch { }
        }

        return result;
    }

    private static bool TryParseLifelineLine(string line, out RawLine raw)
    {
        raw = default;

        // Expected: "yyyy-MM-dd HH:mm:ss [who] message"
        if (line.Length < 22) return false;
        if (line[4] != '-' || line[7] != '-' || line[10] != ' ' ||
            line[13] != ':' || line[16] != ':' || line[19] != ' ')
            return false;
        if (line[20] != '[') return false;

        int close = line.IndexOf(']', 21);
        if (close < 0) return false;

        string who     = line.Substring(21, close - 21);
        string message = line.Substring(close + 1).TrimStart();

        DateTime ts = DateTime.TryParse(line.Substring(0, 19), out var parsed)
            ? parsed
            : DateTime.MinValue;

        string source = who == "host" ? "host"
                      : who == "?"    ? "unknown"
                      :                 "client";

        raw = new RawLine(ts, who, message, source);
        return true;
    }

    private static bool TryParseHelperLine(string line, out RawLine raw)
    {
        raw = default;

        // Expected: "yyyy-MM-dd HH:mm:ss message" (no [who])
        if (line.Length < 20) return false;
        if (line[4] != '-' || line[7] != '-' || line[10] != ' ' ||
            line[13] != ':' || line[16] != ':')
            return false;

        string message = line.Length > 20 ? line.Substring(20).TrimStart() : "";

        DateTime ts = DateTime.TryParse(line.Substring(0, 19), out var parsed)
            ? parsed
            : DateTime.MinValue;

        raw = new RawLine(ts, "helper", message, "helper");
        return true;
    }

    private string ComputeSignature()
    {
        var sb = new StringBuilder();

        foreach (var f in EnumerateFiles())
        {
            try
            {
                var info = new FileInfo(f);
                sb.Append(f)
                  .Append('|').Append(info.Length)
                  .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                  .Append(';');
            }
            catch { }
        }

        return sb.ToString();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _lastSignature = ComputeSignature();
        RefreshAll();
    }

    private async void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItems is { Count: > 0 } items)
        {
            var text = string.Join(Environment.NewLine,
                items.Cast<LogLine>().Select(l => $"{l.Timestamp} [{l.SourceTag}] {l.Text}"));

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                StatusLabel.Text = $"Copied {items.Count} lines to clipboard!";
            }
        }
        else
        {
            StatusLabel.Text = "No lines selected. Click and drag to highlight text, or use Copy All.";
        }
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine,
            _logLines.Select(l => $"{l.Timestamp} [{l.SourceTag}] {l.Text}"));

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            StatusLabel.Text = "Copied all lines to clipboard!";
        }
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_path}\""); } catch { }
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_path, "");
            _lastSignature = ComputeSignature();
            RefreshAll();
            StatusLabel.Text = "Log cleared.";
        }
        catch { }
    }
}
