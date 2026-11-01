using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeskMon
{
    /// <summary>Dialog for the game-driven Digital Vibrance rules.</summary>
    public partial class VibranceWindow : Window
    {
        private readonly Settings _settings;
        private readonly VibranceService _service;
        private readonly ObservableCollection<VibranceRow> _rows = new ObservableCollection<VibranceRow>();
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer();
        private bool _loading = true;

        public VibranceWindow(Settings settings, VibranceService service)
        {
            InitializeComponent();
            Icon = App.WindowIcon();
            _settings = settings;
            _service = service;

            // Never let a long game list push the dialog off the bottom of the screen.
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 40);

            // Dragging a slider raises a value per pixel; write the file once the
            // user pauses instead of on every tick.
            _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _settings.Save(); };
            Closed += (s, e) => { _saveTimer.Stop(); _settings.Save(); };

            GameList.ItemsSource = _rows;
            LoadProcessList();

            foreach (var app in _settings.VibranceApps)
                AddRow(app.Process, app.Level, false);

            CkEnabled.IsChecked = _settings.VibranceEnabled;
            SliderDesktop.Value = VibranceService.Clamp(_settings.VibranceLevel);
            TxtDesktop.Text = Math.Round(SliderDesktop.Value) + "%";

            UpdateEmptyHint();
            UpdateStatus();

            _loading = false;

            if (_service != null)
                _service.Applied += (s, e) => Dispatcher.Invoke(new Action(UpdateStatus));
        }

        // ---------------------------------------------------------------- rows

        private void AddRow(string process, int level, bool track = true)
        {
            var row = new VibranceRow { Process = process, Level = VibranceService.Clamp(level) };
            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
            if (track) { SyncToSettings(); SaveAndApply(); UpdateEmptyHint(); }
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_loading) return;
            SyncToSettings();
            SaveAndApply();
        }

        private void SyncToSettings()
        {
            _settings.VibranceApps.Clear();
            foreach (var row in _rows)
                _settings.VibranceApps.Add(new VibranceApp { Process = row.Process, Level = row.Level });
        }

        private void SaveAndApply()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
            if (_service != null) _service.Refresh();
        }

        private void UpdateEmptyHint()
        {
            TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateStatus()
        {
            if (_service == null || !_service.Available)
            {
                TxtGpu.Text = "未检测到 NVIDIA 数字震动接口";
                TxtState.Text = _service != null && !string.IsNullOrEmpty(_service.FailureReason)
                    ? _service.FailureReason
                    : "此功能需要 NVIDIA 显卡并安装官方驱动。";
                return;
            }

            TxtGpu.Text = _service.GpuName;
            if (!_settings.VibranceEnabled)
            {
                TxtState.Text = "已关闭，拖动切换开关即可启用。";
                return;
            }

            var current = _service.AppliedLevel;
            if (current < 0)
            {
                TxtState.Text = "等待切换…";
                return;
            }

            TxtState.Text = string.IsNullOrEmpty(_service.AppliedFor)
                ? "桌面状态：" + current + "%"
                : string.Format("已为 {0} 应用 {1}%", _service.AppliedFor, current);
        }

        // ---------------------------------------------------------------- chrome

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ---------------------------------------------------------------- handlers

        private void Enabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.VibranceEnabled = CkEnabled.IsChecked == true;
            _settings.Save();
            // Turning it off while a game is in front must restore the desktop level.
            if (_service != null) _service.Refresh();
            UpdateStatus();
        }

        private void Desktop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.VibranceLevel = (int)Math.Round(SliderDesktop.Value);
            TxtDesktop.Text = _settings.VibranceLevel + "%";
            SaveAndApply();
            UpdateStatus();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var row = (VibranceRow)((System.Windows.Controls.Button)sender).Tag;
            _rows.Remove(row);
            row.PropertyChanged -= Row_PropertyChanged;
            SyncToSettings();
            SaveAndApply();
            UpdateEmptyHint();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = NormalizeProcessName(ComboProcesses.Text);
            if (string.IsNullOrEmpty(name)) return;

            if (_rows.Any(r => string.Equals(r.Process, name, StringComparison.OrdinalIgnoreCase)))
            {
                UpdateStatus();
                TxtState.Text = name + " 已在列表中。";
                return;
            }

            AddRow(name, 65);   // a distinctly stronger level than the neutral 50
            ComboProcesses.Text = string.Empty;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe",
                Title = "选择游戏主程序"
            };
            if (dialog.ShowDialog(this) != true) return;

            var name = NormalizeProcessName(Path.GetFileName(dialog.FileName));
            if (string.IsNullOrEmpty(name)) return;
            if (_rows.Any(r => string.Equals(r.Process, name, StringComparison.OrdinalIgnoreCase)))
            {
                TxtState.Text = name + " 已在列表中。";
                return;
            }
            AddRow(name, 65);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var imported = VibranceImporter.Import(_rows.Select(r => r.Process));
            if (imported.Count == 0)
            {
                TxtState.Text = "没有找到可导入的 vibranceGUI 配置。";
                return;
            }

            var added = 0;
            foreach (var item in imported)
            {
                if (_rows.Any(r => string.Equals(r.Process, item.Process, StringComparison.OrdinalIgnoreCase))) continue;
                AddRow(item.Process, item.Level, false);
                added++;
            }

            SyncToSettings();
            SaveAndApply();
            UpdateEmptyHint();
            TxtState.Text = added == 0
                ? "vibranceGUI 的游戏都已经在列表里了。"
                : string.Format("已导入 {0} 条配置。", added);
        }

        // ---------------------------------------------------------------- helpers

        private void LoadProcessList()
        {
            foreach (var p in VibranceImporter.RunningProcesses())
                ComboProcesses.Items.Add(p);
        }

        internal static string NormalizeProcessName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return Regex.IsMatch(name, @"^[\w\-\. ]+$") ? name.Trim() : string.Empty;
        }
    }

    /// <summary>One row in the dialog: process name bound to a percent level.</summary>
    internal sealed class VibranceRow : INotifyPropertyChanged
    {
        private int _level;

        public string Process { get; set; }

        public int Level
        {
            get { return _level; }
            set
            {
                if (_level == value) return;
                _level = value;
                Notify("Level");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Reads whatever vibranceGUI left behind so existing setups carry over.</summary>
    internal static class VibranceImporter
    {
        /// <summary>Processes with a visible window, name first, most relevant for games.</summary>
        public static List<string> RunningProcesses()
        {
            var names = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (!names.Contains(p.ProcessName)) names.Add(p.ProcessName);
                }
                catch { }
            }
            names.Sort();
            return names;
        }

        /// <summary>Reads %APPDATA%\vibranceGUI\applicationData.xml, converting its 0-63 scale to percent.</summary>
        public static List<VibranceApp> Import(IEnumerable<string> alreadyConfigured)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "vibranceGUI", "applicationData.xml");

            var result = new List<VibranceApp>();
            if (!File.Exists(path)) return result;

            try
            {
                var doc = XDocument.Load(path);
                foreach (var node in doc.Descendants("ApplicationSetting"))
                {
                    var name = VibranceWindow.NormalizeProcessName((string)node.Element("Name"));
                    if (string.IsNullOrEmpty(name)) continue;

                    int legacy;
                    if (!int.TryParse((string)node.Element("IngameLevel"), out legacy)) legacy = 0;

                    result.Add(new VibranceApp { Process = name, Level = NvVibrance.FromLegacyLevel(legacy) });
                }
            }
            catch { return result; }

            return result
                .Where(a => !alreadyConfigured.Any(
                    existing => string.Equals(existing, a.Process, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }
}
