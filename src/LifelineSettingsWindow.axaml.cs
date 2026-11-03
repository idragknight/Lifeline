using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Core.Interfaces;

namespace Lifeline;

internal partial class LifelineSettingsWindow : Window
{
    private readonly IOptionContainer _container;
    private readonly Action _openLog;
    private readonly Action? _forceRestart;
    private readonly Action? _testGrid;
    private readonly Action? _startHelper;

    private readonly LifelineVersionInfo _versionInfo = new();
    private Dictionary<string, List<OptionRow>> _categorizedOptions = new();

    public LifelineSettingsWindow(
        IOptionContainer container,
        Action openLog,
        Action? forceRestart = null,
        Action? testGrid = null,
        Action? startHelper = null)
    {
        InitializeComponent();
        LifelineTheme.Apply(this);

        _container    = container;
        _openLog      = openLog;
        _forceRestart = forceRestart;
        _testGrid     = testGrid;
        _startHelper  = startHelper;

        VersionLabel.Text = _versionInfo.ShortLabel;
        LoadOptions();

        UpdateAboutTab();
    }

    private void LoadOptions()
    {
        _categorizedOptions = new()
        {
            { "General", new List<OptionRow>() },
            { "Network", new List<OptionRow>() },
            { "Recovery", new List<OptionRow>() }
        };

        // Test & Diagnostics tab exists whenever at least one actionable test
        // is possible in this process. Host gets Test Grid + Helper Status;
        // child gets those plus Test Restart.
        if (_testGrid != null || _forceRestart != null)
        {
            _categorizedOptions.Add("Test & Diagnostics", new List<OptionRow>());
        }

        foreach (var option in _container.Options)
        {
            var row = CreateRow(option);
            string category = option.Name switch
            {
                "pingTarget" or "failureThreshold" or "pingTimeoutMs" => "Network",
                "restoreStableSec" or "graceAfterRestoreSec" or "activityWindowSec" or "maxRestartsPerHour" => "Recovery",
                _ => "General"
            };
            _categorizedOptions[category].Add(row);
        }

        // Test Ping goes in the Network tab — it depends on nothing (no
        // _forceRestart, no _testGrid, no helper), so it should be available
        // in both host and child processes.
        _categorizedOptions["Network"].Add(CreatePingTestRow());

        if (_categorizedOptions.ContainsKey("Test & Diagnostics"))
        {
            BuildNonPingTestRows();
        }

        var sidebarItems = _categorizedOptions.Keys.ToList();
        sidebarItems.Add("About");

        SidebarList.ItemsSource = sidebarItems;
        SidebarList.SelectedIndex = 0;
    }

    private void SidebarList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is string category)
        {
            if (category == "About")
            {
                SettingsScroll.IsVisible = false;
                AboutScroll.IsVisible = true;
                TestStatusBox.IsVisible = false;
                UpdateAboutTab();
            }
            else
            {
                SettingsScroll.IsVisible = true;
                AboutScroll.IsVisible = false;

                if (_categorizedOptions.TryGetValue(category, out var rows))
                {
                    OptionsList.ItemsSource = rows;
                    // Show the status box whenever this tab has actionable tests
                    // (Test Ping lives in Network now, so this is broader than
                    // "category == Test & Diagnostics").
                    TestStatusBox.IsVisible = rows.Any(r => r.IsTestRow);
                }
            }
        }
    }

    private async void RefreshAbout_Click(object? sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Refreshing...";
        StatusLabel.Foreground = this.FindResource("SkuaMutedTextBrush") as IBrush ?? Brushes.Gray;
        await UpdateAboutTab();
    }

    private async Task UpdateAboutTab()
    {
        IBrush okBrush    = this.FindResource("SkuaSuccessBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#4ADE80"));
        IBrush errorBrush = this.FindResource("SkuaErrorBrush")   as IBrush ?? new SolidColorBrush(Color.Parse("#F87171"));
        IBrush warnBrush  = this.FindResource("SkuaWarnBrush")    as IBrush ?? new SolidColorBrush(Color.Parse("#F59E0B"));

        var v = await Task.Run(() => new LifelineVersionInfo());

        if (AboutPluginVersion != null)
        {
            AboutPluginVersion.Text = $"v{v.PluginFullVersion}";
        }

        if (AboutHelperDisk != null)
        {
            AboutHelperDisk.Text = v.HelperInstalled ? v.HelperFullVersion : "Not installed";
            AboutHelperDisk.Foreground = v.HelperInstalled ? (v.VersionsMatch ? okBrush : errorBrush) : errorBrush;
        }

        if (AboutHelperRunning != null)
        {
            if (v.RunningHelperVersion is null)
            {
                AboutHelperRunning.Text = v.HelperInstalled
                    ? "Not running"
                    : "Not installed";
                AboutHelperRunning.Foreground = warnBrush;
            }
            else
            {
                AboutHelperRunning.Text = $"{v.RunningHelperVersion} (pid {v.RunningHelperPid})";
                AboutHelperRunning.Foreground = v.RunningHelperOutdated ? errorBrush : okBrush;
            }
        }

        if (StatusLabel.Text == "Refreshing...")
        {
            StatusLabel.Text = "Refreshed.";
            StatusLabel.Foreground = okBrush;
            DispatcherTimer.RunOnce(() => StatusLabel.Text = "", TimeSpan.FromSeconds(2));
        }
    }

    private void ViewGithub_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/idragknight/Lifeline") { UseShellExecute = true });
        }
        catch { }
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        IBrush okBrush    = this.FindResource("SkuaSuccessBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#4ADE80"));
        IBrush errorBrush = this.FindResource("SkuaErrorBrush")   as IBrush ?? new SolidColorBrush(Color.Parse("#F87171"));

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var latestInfo = await Task.Run(() => new LifelineVersionInfo());
                await clipboard.SetTextAsync(latestInfo.DiagnosticsSummary);

                StatusLabel.Text = "Diagnostics copied to clipboard.";
                StatusLabel.Foreground = okBrush;
                DispatcherTimer.RunOnce(() => StatusLabel.Text = "", TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Copy failed: {ex.Message}";
            StatusLabel.Foreground = errorBrush;
            DispatcherTimer.RunOnce(() => StatusLabel.Text = "", TimeSpan.FromSeconds(3));
        }
    }

    private string GetSettingString(string name, string fallback)
    {
        try
        {
            var opt = _container.Options.FirstOrDefault(o => o.Name == name);
            if (opt != null) return _container.GetDirect(opt);
        }
        catch { }
        return fallback;
    }

    /// <summary>
    /// Test Ping. Moved to the Network tab and available in both host and
    /// child processes — it depends on nothing this window can't provide.
    /// </summary>
    private OptionRow CreatePingTestRow()
    {
        return new OptionRow
        {
            DisplayName = "Test Ping",
            Description = "Pings the configured host to verify network connectivity.",
            IsTestRow = true,
            Control = CreateActionButton("Run Ping", async () =>
            {
                SetTestStatus("Pinging...", "Wait", "#3b82f6");
                await Task.Run(() =>
                {
                    try
                    {
                        using Ping p = new();
                        string target = GetSettingString("pingTarget", "8.8.8.8");
                        PingReply reply = p.Send(target, 2000);
                        if (reply.Status == IPStatus.Success)
                        {
                            Dispatcher.UIThread.Post(() => SetTestStatus($"Ping {target}: OK ({reply.RoundtripTime}ms)", "OK", "#10b981"));
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() => SetTestStatus($"Ping {target}: Failed ({reply.Status})", "Fail", "#ef4444"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => SetTestStatus($"Ping Error: {ex.Message}", "Fail", "#ef4444"));
                    }
                });
            })
        };
    }

    /// <summary>
    /// Test Grid, Helper Status, Test Restart. Each is added only when its
    /// callback exists — host gets the first two, child gets all three.
    /// Before this change the whole Test & Diagnostics tab was gated on
    /// _forceRestart (child-only), so the host settings window had no
    /// diagnostic tests at all — exactly the wrong way around, because the
    /// tray now guarantees the host wins the settings-window claim.
    /// </summary>
    private void BuildNonPingTestRows()
    {
        var rows = _categorizedOptions["Test & Diagnostics"];

        if (_testGrid != null)
        {
            rows.Add(new OptionRow
            {
                DisplayName = "Test Grid",
                Description = "Toggles VibeSkua's grid view via the host process (grid ↔ tab).",
                IsTestRow = true,
                Control = CreateActionButton("Test Grid", () =>
                {
                    _testGrid.Invoke();
                    SetTestStatus("Grid toggle request sent to host.", "OK", "#10b981");
                })
            });
        }

        if (_startHelper != null)
        {
            rows.Add(new OptionRow
            {
                DisplayName = "Helper Status",
                Description = "Verifies that LifelineHelper.exe is running in the background. If not, starts it immediately.",
                IsTestRow = true,
                Control = CreateActionButton("Check / Start", () =>
                {
                    try
                    {
                        var procs = Process.GetProcessesByName("LifelineHelper");
                        if (procs.Length > 0)
                        {
                            SetTestStatus($"Helper is running (PID {procs[0].Id}) and watching for requests.", "OK", "#10b981");
                            foreach (var p in procs) p.Dispose();
                        }
                        else
                        {
                            SetTestStatus("Helper was NOT running. Attempting to start...", "Wait", "#f59e0b");
                            _startHelper.Invoke();

                            DispatcherTimer.RunOnce(() =>
                            {
                                var newProcs = Process.GetProcessesByName("LifelineHelper");
                                if (newProcs.Length > 0)
                                {
                                    SetTestStatus($"Helper started successfully (PID {newProcs[0].Id}).", "OK", "#10b981");
                                    foreach (var p in newProcs) p.Dispose();
                                }
                                else
                                {
                                    SetTestStatus("Failed to start Helper. Check lifeline.log for details.", "Fail", "#ef4444");
                                }
                            }, TimeSpan.FromSeconds(1));
                        }
                    }
                    catch (Exception ex)
                    {
                        SetTestStatus($"Status Error: {ex.Message}", "Fail", "#ef4444");
                    }
                })
            });
        }

        if (_forceRestart != null)
        {
            rows.Add(new OptionRow
            {
                DisplayName = "Test Restart",
                Description = "Writes the restart request and immediately exits the client. The Helper will relaunch it.",
                IsTestRow = true,
                Control = CreateActionButton("Test Restart", () =>
                {
                    SetTestStatus("Writing request and exiting client...", "Wait", "#3b82f6");
                    DispatcherTimer.RunOnce(() => _forceRestart.Invoke(), TimeSpan.FromMilliseconds(500));
                }, true)
            });
        }
    }

    private Button CreateActionButton(string text, Action onClick, bool isDanger = false)
    {
        var btn = new Button
        {
            Content = text,
            Width = 130,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isDanger) btn.Classes.Add("danger");
        else btn.Classes.Add("skua-primary");

        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateActionButton(string text, Func<Task> onClick)
    {
        var btn = new Button
        {
            Content = text,
            Width = 130,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "skua-primary" }
        };
        btn.Click += async (_, _) => await onClick();
        return btn;
    }

    private void SetTestStatus(string text, string iconText, string colorHex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TestStatusText.Text = text;
            TestStatusIcon.Text = iconText;
            TestStatusIcon.Foreground = new SolidColorBrush(Color.Parse(colorHex));
            TestStatusBox.IsVisible = true;
        });
    }

    private OptionRow CreateRow(IOption option)
    {
        Control input;

        if (option.Type == typeof(bool))
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = _container.Get<bool>(option),
                VerticalAlignment = VerticalAlignment.Center,
                OnContent = "",
                OffContent = ""
            };
            toggle.IsCheckedChanged += (_, _) => SaveOption(option, toggle.IsChecked == true);
            input = toggle;
        }
        else if (option.Type == typeof(int))
        {
            var (min, max) = GetNumericRange(option);
            int current = Math.Clamp(_container.Get<int>(option), (int)min, (int)max);
            input = CreateThemedNumericInput(option, min, max, current);
        }
        else if (option.Name == "pingTarget")
        {
            var targets = new List<string> { "8.8.8.8", "1.1.1.1", "9.9.9.9", "208.67.222.222", "google.com", "cloudflare.com" };
            string current = _container.GetDirect(option);

            if (!string.IsNullOrEmpty(current) && !targets.Contains(current))
            {
                targets.Insert(0, current);
            }

            var combo = new ComboBox
            {
                ItemsSource = targets,
                SelectedItem = string.IsNullOrEmpty(current) ? targets[0] : current,
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };

            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s)
                    SaveOption(option, s);
            };
            input = combo;
        }
        else
        {
            var tb = new TextBox
            {
                Text = _container.GetDirect(option),
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.LostFocus += (_, _) => SaveOption(option, tb.Text ?? "");
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                    SaveOption(option, tb.Text ?? "");
            };
            input = tb;
        }

        return new OptionRow
        {
            DisplayName = option.DisplayName,
            Description = option.Description,
            Control = input
        };
    }

    private Control CreateThemedNumericInput(IOption option, decimal min, decimal max, int initialValue)
    {
        IBrush surfaceBrush = (this.FindResource("SkuaSurfaceBrush") as IBrush) ?? Brushes.Transparent;
        IBrush borderBrush = (this.FindResource("SkuaBorderBrush") as IBrush) ?? Brushes.Transparent;
        IBrush accentBrush = (this.FindResource("SkuaAccentBrush") as IBrush) ?? Brushes.Transparent;

        int lastValid = initialValue;

        var textBox = new TextBox
        {
            Text = initialValue.ToString(),
            Width = 78,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textBox.Classes.Add("lifeline-num-input");

        var upIcon = new PathIcon
        {
            Data = Geometry.Parse("M 0 5 L 4 1 L 8 5 Z"),
            Width = 8,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var upBtn = new Button
        {
            Content = upIcon,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        upBtn.Classes.Add("lifeline-num-arrow");

        var downIcon = new PathIcon
        {
            Data = Geometry.Parse("M 0 1 L 4 5 L 8 1 Z"),
            Width = 8,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var downBtn = new Button
        {
            Content = downIcon,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        downBtn.Classes.Add("lifeline-num-arrow");

        var arrowStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
        };
        arrowStack.Children.Add(upBtn);
        arrowStack.Children.Add(downBtn);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto"),
        };
        grid.Children.Add(textBox);
        Grid.SetColumn(arrowStack, 1);
        grid.Children.Add(arrowStack);

        var container = new Border
        {
            Background = surfaceBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = grid,
        };

        textBox.GotFocus += (_, _) => container.BorderBrush = accentBrush;
        textBox.LostFocus += (_, _) => container.BorderBrush = borderBrush;

        void ApplyValue(int value)
        {
            value = Math.Clamp(value, (int)min, (int)max);
            lastValid = value;
            textBox.Text = value.ToString();
            SaveOption(option, value);
        }

        upBtn.Click += (_, _) =>
        {
            if (int.TryParse(textBox.Text, out int v))
                ApplyValue(v + 1);
        };

        downBtn.Click += (_, _) =>
        {
            if (int.TryParse(textBox.Text, out int v))
                ApplyValue(v - 1);
        };

        textBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(textBox.Text, out int v))
                ApplyValue(v);
            else
                textBox.Text = lastValid.ToString();
        };

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                if (int.TryParse(textBox.Text, out int v))
                    ApplyValue(v);
                else
                    textBox.Text = lastValid.ToString();
                e.Handled = true;
            }
        };

        return container;
    }

    private void SaveOption(IOption option, object value)
    {
        try
        {
            _container.Set(option, value);
            StatusLabel.Text = "Saved";
            StatusLabel.Foreground = this.FindResource("SkuaSuccessBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#4ADE80"));
            DispatcherTimer.RunOnce(() => StatusLabel.Text = "", TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
            StatusLabel.Foreground = this.FindResource("SkuaErrorBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#F87171"));
        }
    }

    private static (decimal Min, decimal Max) GetNumericRange(IOption option) => option.Name switch
    {
        "failureThreshold" => (1, 1000),
        "pingTimeoutMs" => (100, 60000),
        "restoreStableSec" => (3, 3600),
        "graceAfterRestoreSec" => (0, 3600),
        "activityWindowSec" => (5, 3600),
        "maxRestartsPerHour" => (1, 100),
        _ => (0, 1000000),
    };

    private void ViewLog_Click(object? sender, RoutedEventArgs e) => _openLog();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ResetDefaults_Click(object? sender, RoutedEventArgs e)
    {
        _container.SetDefaults();
        _container.Save();

        if (SidebarList.SelectedItem is string category && _categorizedOptions.TryGetValue(category, out var rows))
        {
            foreach (var row in rows)
            {
                if (row.IsTestRow) continue;   // test buttons have no saved state

                var option = _container.Options.FirstOrDefault(x => x.DisplayName == row.DisplayName);
                if (option != null)
                {
                    if (row.Control is ToggleSwitch toggle)
                        toggle.IsChecked = _container.Get<bool>(option);
                    else if (row.Control is ComboBox combo)
                        combo.SelectedItem = _container.GetDirect(option);
                    else if (row.Control is TextBox tb)
                        tb.Text = _container.GetDirect(option);
                    else if (row.Control is Border container && container.Child is Grid g)
                    {
                        var numTb = g.Children.OfType<TextBox>().FirstOrDefault();
                        if (numTb != null)
                            numTb.Text = _container.Get<int>(option).ToString();
                    }
                }
            }
        }

        StatusLabel.Text = "Reset to defaults";
        StatusLabel.Foreground = this.FindResource("SkuaSuccessBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#4ADE80"));
        DispatcherTimer.RunOnce(() => StatusLabel.Text = "", TimeSpan.FromSeconds(2));
    }

    private class OptionRow
    {
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public Control Control { get; set; } = null!;
        public bool IsTestRow { get; set; }
    }
}
