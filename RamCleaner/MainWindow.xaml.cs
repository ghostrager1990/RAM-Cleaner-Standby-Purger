using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RamCleaner.Core;

namespace RamCleaner
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _monitorTimer;
        private DateTime _lastIntervalFlush = DateTime.Now;
        private SettingsData _settings;
        private readonly ObservableCollection<string> _excludedProcesses = new();

        public MainWindow()
        {
            InitializeComponent();

            // Set up tray icon safely from resource pack URI or executable fallback
            try
            {
                using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
                if (iconStream != null)
                {
                    TrayIcon.Icon = new System.Drawing.Icon(iconStream);
                }
            }
            catch
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    TrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
            }

            TrayIcon.Visibility = Visibility.Visible;

            _settings = AppSettings.Load();
            LoadSettingsToUi();

            // Only auto-hide if specifically launched on Windows startup via --autostart flag
            Loaded += (s, e) =>
            {
                string[] args = Environment.GetCommandLineArgs();
                bool isAutoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

                if (isAutoStart)
                {
                    Hide();
                }
                else
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                }
            };

            // Set up background monitoring loop running every 1 second
            _monitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _monitorTimer.Tick += MonitorTimer_Tick;
            _monitorTimer.Start();
        }

        private void LoadSettingsToUi()
        {
            ChkStartWindows.IsChecked = _settings.StartWithWindows;
            ChkStartMinimized.IsChecked = _settings.StartMinimized;
            ChkThreshold.IsChecked = _settings.AutoFlushThresholdEnabled;
            SliderThreshold.Value = _settings.ThresholdPercent;
            ChkInterval.IsChecked = _settings.AutoFlushIntervalEnabled;

            CmbInterval.SelectedIndex = _settings.IntervalMinutes switch
            {
                15 => 0,
                30 => 1,
                60 => 2,
                120 => 3,
                _ => 1
            };

            _excludedProcesses.Clear();
            foreach (var proc in _settings.ExcludedProcesses)
            {
                _excludedProcesses.Add(proc);
            }
            LstExclusions.ItemsSource = _excludedProcesses;
        }

        private void SaveUiToSettings()
        {
            _settings.StartWithWindows = ChkStartWindows.IsChecked == true;
            _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
            _settings.AutoFlushThresholdEnabled = ChkThreshold.IsChecked == true;
            _settings.ThresholdPercent = (int)SliderThreshold.Value;
            _settings.AutoFlushIntervalEnabled = ChkInterval.IsChecked == true;

            _settings.IntervalMinutes = CmbInterval.SelectedIndex switch
            {
                0 => 15,
                1 => 30,
                2 => 60,
                3 => 120,
                _ => 30
            };

            _settings.ExcludedProcesses = _excludedProcesses.ToList();
            AppSettings.Save(_settings);
        }

        private bool IsExcludedProcessRunning(out string runningProcessName)
        {
            runningProcessName = string.Empty;
            if (_excludedProcesses.Count == 0) return false;

            Process[]? runningProcesses = null;
            try
            {
                runningProcesses = Process.GetProcesses();
                foreach (var exclusion in _excludedProcesses)
                {
                    string target = exclusion.Trim();
                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        target = Path.GetFileNameWithoutExtension(target);
                    }

                    for (int i = 0; i < runningProcesses.Length; i++)
                    {
                        try
                        {
                            if (string.Equals(runningProcesses[i].ProcessName, target, StringComparison.OrdinalIgnoreCase))
                            {
                                runningProcessName = exclusion;
                                return true;
                            }
                        }
                        catch
                        {
                            // Safely ignore permission/anti-cheat exceptions on protected processes
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (runningProcesses != null)
                {
                    foreach (var p in runningProcesses)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }

            return false;
        }

        private void MonitorTimer_Tick(object? sender, EventArgs e)
        {
            var memStatus = NativeMethods.MEMORYSTATUSEX.Create();
            if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
            {
                uint load = memStatus.dwMemoryLoad;
                double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                double usedGb = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024.0 * 1024 * 1024);

                PbMemory.Value = load;
                TxtMemoryUsage.Text = $"Memory Load: {load}%";
                TxtMemoryDetails.Text = $"{usedGb:0.0} GB / {totalGb:0.0} GB";

                // Pause auto-flush routines if an excluded game/app is active
                if (IsExcludedProcessRunning(out string runningExclusion))
                {
                    TxtStatus.Text = $"Status: PAUSED (Running: {runningExclusion})";
                    return;
                }

                // Threshold-based auto-flush
                if (ChkThreshold.IsChecked == true && load >= SliderThreshold.Value)
                {
                    MemoryManager.FlushAll();
                    TxtStatus.Text = $"Status: Auto-flushed at {DateTime.Now:HH:mm:ss} (>={SliderThreshold.Value}%)";
                }

                // Interval-based auto-flush
                if (ChkInterval.IsChecked == true)
                {
                    int intervalMinutes = CmbInterval.SelectedIndex switch
                    {
                        0 => 15,
                        1 => 30,
                        2 => 60,
                        3 => 120,
                        _ => 30
                    };

                    if ((DateTime.Now - _lastIntervalFlush).TotalMinutes >= intervalMinutes)
                    {
                        MemoryManager.FlushAll();
                        _lastIntervalFlush = DateTime.Now;
                        TxtStatus.Text = $"Status: Interval auto-flushed at {DateTime.Now:HH:mm:ss}";
                    }
                }
            }
        }

        // Exclusion List Handlers with Windows Explorer File Dialog Fallback
        private void BtnAddExclusion_Click(object sender, RoutedEventArgs e)
        {
            string typedApp = TxtNewProcess.Text?.Trim() ?? string.Empty;

            // 1. If text was entered into the TextBox, add it directly
            if (!string.IsNullOrWhiteSpace(typedApp))
            {
                AddProcessToExclusionList(typedApp);
                TxtNewProcess.Clear();
                return;
            }

            // 2. If TextBox is empty, open Windows Explorer to browse for an executable
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Game or Application Executable",
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string selectedFileName = Path.GetFileName(openFileDialog.FileName);
                AddProcessToExclusionList(selectedFileName);
            }
        }

        private void AddProcessToExclusionList(string processName)
        {
            if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                processName += ".exe";
            }

            if (!_excludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                _excludedProcesses.Add(processName);
                SaveUiToSettings();
            }
        }

        private void BtnRemoveExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (LstExclusions.SelectedItem is string selected)
            {
                _excludedProcesses.Remove(selected);
                SaveUiToSettings();
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveUiToSettings();
            TxtStatus.Text = $"Status: Settings saved at {DateTime.Now:HH:mm:ss}";
        }

        // Manual Flush Button Handlers
        private void BtnWorkingSets_Click(object sender, RoutedEventArgs e)
        {
            MemoryManager.FlushWorkingSets();
            TxtStatus.Text = $"Status: Flushed Working Sets at {DateTime.Now:HH:mm:ss}";
        }

        private void BtnModified_Click(object sender, RoutedEventArgs e)
        {
            MemoryManager.FlushModifiedPageList();
            TxtStatus.Text = $"Status: Flushed Modified Page List at {DateTime.Now:HH:mm:ss}";
        }

        private void BtnP0Standby_Click(object sender, RoutedEventArgs e)
        {
            MemoryManager.FlushPriority0StandbyList();
            TxtStatus.Text = $"Status: Cleared Priority-0 Standby List at {DateTime.Now:HH:mm:ss}";
        }

        private void BtnStandby_Click(object sender, RoutedEventArgs e)
        {
            MemoryManager.FlushStandbyList();
            TxtStatus.Text = $"Status: Cleared Standby List at {DateTime.Now:HH:mm:ss}";
        }

        private void BtnFlushAll_Click(object sender, RoutedEventArgs e)
        {
            MemoryManager.FlushAll();
            TxtStatus.Text = $"Status: Completed Full Flush at {DateTime.Now:HH:mm:ss}";
        }

        // Window Lifecycle & System Tray Routing
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Minimize to tray instead of killing background memory monitoring
            e.Cancel = true;
            Hide();
        }

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            SaveUiToSettings();
            _monitorTimer.Stop();
            Application.Current.Shutdown();
        }
    }
}