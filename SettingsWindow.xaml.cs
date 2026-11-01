using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskMon
{
    public partial class SettingsWindow : Window
    {
        private readonly Settings _settings;
        private bool _loading = true;

        public SettingsWindow(Settings settings)
        {
            InitializeComponent();
            Icon = App.WindowIcon();   // this window does show in the taskbar
            _settings = settings;

            CkCpuLoad.IsChecked = settings.ShowCpuLoad;
            CkCpuTemp.IsChecked = settings.ShowCpuTemp;
            CkGpuLoad.IsChecked = settings.ShowGpuLoad;
            CkGpuTemp.IsChecked = settings.ShowGpuTemp;
            CkFan.IsChecked = settings.ShowFan;
            CkRam.IsChecked = settings.ShowRam;
            CkVram.IsChecked = settings.ShowVram;
            CkNet.IsChecked = settings.ShowNet;
            CkPill.IsChecked = settings.ShowPill;
            _loading = false;
            UiLog("settings open: CpuLoad=" + settings.ShowCpuLoad + " CpuTemp=" + settings.ShowCpuTemp);

            foreach (var cb in new[] { CkCpuLoad, CkCpuTemp, CkGpuLoad, CkGpuTemp, CkFan, CkRam, CkVram, CkNet, CkPill })
            {
                cb.Checked += Changed;
                cb.Unchecked += Changed;
            }
        }

        private void Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cb = (CheckBox)sender;
            UiLog("click " + cb.Name + " -> " + cb.IsChecked);
            _settings.ShowCpuLoad = CkCpuLoad.IsChecked == true;
            _settings.ShowCpuTemp = CkCpuTemp.IsChecked == true;
            _settings.ShowGpuLoad = CkGpuLoad.IsChecked == true;
            _settings.ShowGpuTemp = CkGpuTemp.IsChecked == true;
            _settings.ShowFan = CkFan.IsChecked == true;
            _settings.ShowRam = CkRam.IsChecked == true;
            _settings.ShowVram = CkVram.IsChecked == true;
            _settings.ShowNet = CkNet.IsChecked == true;
            _settings.ShowPill = CkPill.IsChecked == true;
            try { _settings.Save(); }
            catch (Exception ex) { UiLog("SAVE EX: " + ex.Message); }
            try { ((App)Application.Current).RefreshDisplay(); }
            catch (Exception ex) { UiLog("REFRESH EX: " + ex); }
        }

        // diagnostics: checkbox clicks land here only if the event fires at all;
        // if a click never produces a log line, the problem is hit-test/activation, not logic.
        private static void UiLog(string msg)
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var dir = Path.GetDirectoryName(exe);
                File.AppendAllText(Path.Combine(dir, "deskmon-ui.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
