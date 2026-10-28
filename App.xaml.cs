using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DeskMon
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private NotifyIcon _tray;
        private MainWindow _window;
        private SensorService _sensors;
        private Settings _settings;
        private ToolStripMenuItem _miLock;
        private ToolStripMenuItem _miAutoStart;
        private ToolStripMenuItem _miPin;
        private System.Windows.Threading.DispatcherTimer _pinTimer;
        private Icon _trayIcon;
        private Sample _pendingSample;
        private Action _applyPendingSample;
        private int _sampleQueued;
        private volatile bool _exiting;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // render-test / metrics diagnostics: no tray, no sensors, no window shown.
            if (e.Args.Length > 0 && (e.Args[0] == "--render-test" || e.Args[0] == "--metrics"))
            {
                var png = e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "render-test.png");
                int exitCode = 0;
                try
                {
                    var w = new MainWindow();
                    w.BindSettings(new Settings());
                    if (e.Args[0] == "--render-test") w.RenderTestPng(png, 2.0);
                    else File.AppendAllText(png, w.MetricsReport());
                    w.Close();
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    try { File.WriteAllText(png + ".error.txt", ex.ToString()); } catch { }
                }
                Shutdown(exitCode);
                return;
            }

            bool created;
            _singleInstance = new Mutex(true, "DeskMon_SingleInstance", out created);
            _ownsMutex = created;
            if (!created)
            {
                Shutdown();
                return;
            }

            _settings = Settings.Load();
            // A passive desktop widget should not die silently; keep a small trace
            // next to the exe so a recurring failure can be diagnosed after a restart.
            DispatcherUnhandledException += (s, ev) =>
            {
                LogError("dispatcher", ev.Exception);
                ev.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogError("appdomain", ev.ExceptionObject as Exception);

            _sensors = new SensorService();

            _window = new MainWindow();
            _window.BindSettings(_settings);
            _window.PositionLog("startup base=" + AppDomain.CurrentDomain.BaseDirectory
                + " loaded Left=" + _settings.Left + " Top=" + _settings.Top
                + " Pinned=" + _settings.Pinned + " args=" + (e.Args.Length == 0 ? "none" : string.Join(",", e.Args)));
            _window.SetClickThrough(_settings.ClickThrough);
            _window.RebuildCells();
            // Must happen before Show(): Show raises Loaded synchronously for a
            // SizeToContent window, and that first layout pass would otherwise persist
            // the not-yet-restored default position over the loaded one.
            _window.RestorePosition(_settings);   // device pixels, clamped on apply
            _window.Show();
            _window.SetPinned(_settings.Pinned);
            _window.ReassertPosition();           // the re-parent can shift it; correct at once
            _window.PersistPosition();

            _pinTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _pinTimer.Tick += (s, ev) => _window.EnsurePinned();
            _pinTimer.Start();

            _applyPendingSample = ApplyPendingSample;
            _sensors.Updated += QueueSample;
            BuildTray();
            _sensors.Start(); // Hardware discovery runs on the worker, not the UI thread.
        }

        private void QueueSample(Sample sample)
        {
            if (_exiting) return;
            Interlocked.Exchange(ref _pendingSample, sample);
            if (Interlocked.CompareExchange(ref _sampleQueued, 1, 0) != 0) return;
            try
            {
                _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, _applyPendingSample);
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _sampleQueued, 0); }
        }

        private void ApplyPendingSample()
        {
            Interlocked.Exchange(ref _sampleQueued, 0);
            var sample = Interlocked.Exchange(ref _pendingSample, null);
            if (!_exiting && sample != null) _window.ApplySample(sample);
        }

        public void RefreshDisplay() => _window.RebuildCells();

        private static void LogError(string where, Exception ex)
        {
            try
            {
                if (ex == null) return;
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + where + ": " + ex + Environment.NewLine;
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deskmon-error.log"), line);
            }
            catch { }
        }

        private SettingsWindow _settingsWindow;

        private void OpenSettingsWindow()
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_settings);
                _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        }

        private void BuildTray()
        {
            _trayIcon = BuildIcon();
            _tray = new NotifyIcon
            {
                Icon = _trayIcon,
                Visible = true,
                Text = "DeskMon",
            };

            _miLock = new ToolStripMenuItem("锁定（点击穿透）") { Checked = _settings.ClickThrough };
            _miLock.Click += (s, e) => ToggleClickThrough();

            _miPin = new ToolStripMenuItem("固定到桌面") { Checked = _settings.Pinned };
            _miPin.Click += (s, e) => TogglePin();

            _miAutoStart = new ToolStripMenuItem("开机启动") { Checked = AutoStartIsSet() };
            _miAutoStart.Click += (s, e) => ToggleAutoStart();

            var miOptions = new ToolStripMenuItem("显示选项…");
            miOptions.Click += (s, e) => OpenSettingsWindow();

            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += (s, e) => ExitApp();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_miPin);
            menu.Items.Add(_miLock);
            menu.Items.Add(_miAutoStart);
            menu.Items.Add(miOptions);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ToggleClickThrough();
        }

        // The tray glyph and any window icon are read back out of this exe's own icon
        // resource, so the shell icon and the tray icon can never drift apart and the .ico
        // is not embedded a second time as a WPF resource.
        private static Icon _trayIconCache;

        internal static Icon TrayIcon()
        {
            if (_trayIconCache != null) return _trayIconCache;
            try
            {
                var associated = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
                if (associated != null) { _trayIconCache = associated; return associated; }
            }
            catch (Exception ex) { LogError("tray icon", ex); }

            // last resort: a simple bar glyph drawn on the fly
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(31, 36, 46));
                using (var brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, 3, 9, 2, 4);
                    g.FillRectangle(brush, 7, 6, 2, 7);
                    g.FillRectangle(brush, 11, 3, 2, 10);
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var borrowed = Icon.FromHandle(hIcon))
                    {
                        _trayIconCache = (Icon)borrowed.Clone();
                        return _trayIconCache;
                    }
                }
                finally { NativeMethods.DestroyIcon(hIcon); }
            }
        }

        // WPF windows (the settings dialog shows in the taskbar) need an ImageSource.
        // Fully qualified: this file also uses System.Drawing.Color.
        internal static System.Windows.Media.ImageSource WindowIcon()
        {
            try
            {
                var icon = TrayIcon();
                if (icon == null) return null;
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception ex) { LogError("window icon", ex); return null; }
        }

        private static Icon BuildIcon() => TrayIcon();

        public void ToggleClickThroughFromUI() => ToggleClickThrough();

        private void ToggleClickThrough()
        {
            _settings.ClickThrough = !_settings.ClickThrough;
            _window.SetClickThrough(_settings.ClickThrough);
            _miLock.Checked = _settings.ClickThrough;
            _settings.Save();
        }

        private void TogglePin()
        {
            _settings.Pinned = !_settings.Pinned;
            _window.SetPinned(_settings.Pinned);
            if (_settings.Pinned)
            {
                // remember where it was left before fixing it to the desktop
                _window.AdoptCurrentPosition();
            }
            _miPin.Checked = _settings.Pinned;
            _settings.Save();
        }

        private static bool AutoStartIsSet()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && key.GetValue("DeskMon") != null;
            }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (AutoStartIsSet()) key.DeleteValue("DeskMon", false);
                    else key.SetValue("DeskMon", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("开机启动设置失败：" + ex.Message);
            }
            _miAutoStart.Checked = AutoStartIsSet();
        }

        private async void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _pinTimer?.Stop();
            try
            {
                _window.PersistPosition(); // writes the real pixel position
            }
            catch { }
            if (_sensors != null)
            {
                _sensors.Updated -= QueueSample;
                // Let normal shutdown release the shared sensor driver; a stuck
                // driver must not make the tray unresponsive indefinitely.
                await System.Threading.Tasks.Task.WhenAny(_sensors.StopAsync(), System.Threading.Tasks.Task.Delay(3000));
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exiting = true;
            _pinTimer?.Stop();
            if (_sensors != null) _sensors.Updated -= QueueSample;
            try { _sensors?.Dispose(); } catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.ContextMenuStrip?.Dispose();
                    _tray.Dispose();
                }
                _trayIcon?.Dispose();
            }
            catch { }
            try { if (_ownsMutex) _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
