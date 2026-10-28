using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskMon
{
    public partial class MainWindow : Window
    {
        private Settings _settings;
        private Sample _last;
        private readonly Dictionary<string, MonitorCell> _cells = new Dictionary<string, MonitorCell>();
        private bool _pinnedMode;
        private bool _dragging;
        private bool _reasserting;
        // Single source of truth for placement, in device pixels. WPF's DIP Left/Top are
        // never assigned: after SetParent into Explorer's desktop layer WPF's own perceived
        // scale changes asynchronously and re-applying its cached DIP silently shifts the bar.
        private int _leftPx = 60, _topPx = 900;
        private int _widestPx, _tallestPx;   // see ReassertPosition
        private bool _positionRestored;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                EnsurePinned();
                ReassertPosition();
            };
            SizeChanged += (s, e) => ReassertPosition();
        }

        internal Rect PixelRect
        {
            get
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.RECT r;
                if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out r) && r.Right > r.Left)
                    return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return new Rect(double.NaN, double.NaN, 0, 0);
            }
        }

        // Bounds the bar must stay inside, in physical device pixels (same space as
        // GetWindowRect). SystemParameters are DIPs and are converted with the window's
        // DPI; WorkArea excludes the taskbar (verified to match Shell_TrayWnd).
        bool TryBounds(out int wl, out int wt, out int wr, out int wb)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            double s = NativeMethods.WindowScale(hwnd);
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var area = SystemParameters.WorkArea;
            var use = area;
            var r = PixelRect;
            if (double.IsNaN(r.X) || !area.IntersectsWith(r)) use = vs;   // off the primary monitor
            wl = (int)Math.Round(use.Left * s); wt = (int)Math.Round(use.Top * s);
            wr = (int)Math.Round(use.Right * s); wb = (int)Math.Round(use.Bottom * s);
            return wr > wl && wb > wt;
        }

        // Place the bar where the user wants it, keeping it inside the work area.
        // _leftPx/_topPx are the user's *desired* position and are only changed by an
        // explicit restore or drag; the clamped target is recomputed here every time, so a
        // transient pre-layout window size can never corrupt the stored position.
        public void ReassertPosition()
        {
            if (_reasserting || _dragging) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!IsLoaded) return;                       // size is meaningless before layout
            var size = PixelRect;
            if (double.IsNaN(size.Width) || size.Width <= 0 || size.Height <= 0) return;
            int wl, wt, wr, wb;
            if (!TryBounds(out wl, out wt, out wr, out wb)) return;

            // Clamp against a monotonic maximum, never the live size: the readouts change
            // width as digits appear (9% -> 10%, 999 -> 1000 RPM), and clamping with the
            // live width made a bar parked at the screen edge jitter a few pixels every 2s.
            var target = ClampMeasured(ref _widestPx, ref _tallestPx, _leftPx, _topPx,
                (int)size.Width, (int)size.Height, wl, wt, wr, wb);
            NativeMethods.RECT r;
            if (NativeMethods.GetWindowRect(hwnd, out r) && r.Left == target.X && r.Top == target.Y) return;
            _reasserting = true;
            try
            {
                int beforeL = r.Left, beforeT = r.Top;
                NativeMethods.MoveInPixels(hwnd, target.X, target.Y);
                NativeMethods.RECT after;
                bool got = NativeMethods.GetWindowRect(hwnd, out after);
                PositionLog("want=" + _leftPx + "," + _topPx + " target=" + target.X + "," + target.Y
                    + " before=" + beforeL + "," + beforeT
                    + " after=" + (got ? after.Left + "," + after.Top : "?")
                    + " size=" + (int)size.Width + "x" + (int)size.Height
                    + " clampSize=" + _widestPx + "x" + _tallestPx
                    + " bounds=" + wl + "," + wt + "-" + wr + "," + wb);
            }
            finally { _reasserting = false; }
        }

        int _positionLogLines;

        // Small trace so a placement problem can be diagnosed from the file alone.
        internal void PositionLog(string message)
        {
            if (_positionLogLines > 40) return;
            _positionLogLines++;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deskmon-position.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        // Called after a drag: adopt where the user dropped it.
        public void AdoptCurrentPosition()
        {
            var r = PixelRect;
            if (double.IsNaN(r.X) || double.IsNaN(r.Y)) return;
            _leftPx = (int)r.X; _topPx = (int)r.Y;
        }

        public void PersistPosition()
        {
            // Never write before the stored position has been read into _leftPx/_topPx,
            // otherwise a mid-startup persist would overwrite the user's saved position.
            if (_settings == null || !_positionRestored) return;
            if (Math.Abs(_settings.Left - _leftPx) > 2 || Math.Abs(_settings.Top - _topPx) > 2)
            {
                _settings.Left = _leftPx;
                _settings.Top = _topPx;
                _settings.Save();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SetClickThrough(ClickThrough);
        }

        public void BindSettings(Settings settings)
        {
            _settings = settings;
        }

        public bool ClickThrough { get; private set; }

        // "Pinned" = the desktop mode: a top-level window held at the bottom of the Z-order.
        public bool Pinned => _pinnedMode;

        public void SetClickThrough(bool on)
        {
            ClickThrough = on;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if (on) style |= NativeMethods.WS_EX_TRANSPARENT;
            else style &= ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
        }

        // pin = layered into the desktop (above wallpaper, behind icons, below all windows).
        // unpin = ordinary floating window so it can be dragged/configured.
        //
        // "Pinned" is deliberately a plain top-level window that is never topmost and never
        // activated. Two tempting alternatives were tried and both make the bar disappear:
        //   * parenting into Explorer's WorkerW - that window is WS_EX_LAYERED, and a
        //     layered parent does not composite child windows (the HWND keeps a correct
        //     rect, IsWindowVisible stays true and PrintWindow still renders it, so the
        //     failure is invisible to every check except looking at the screen);
        //   * SetWindowPos(HWND_BOTTOM) - Progman is itself in the top-level chain, so the
        //     bar ends up *under* the wallpaper.
        // A non-topmost, never-activated window already sits above the wallpaper and below
        // every application window, which is the tier the user asked for.
        public void SetPinned(bool pinned)
        {
            Topmost = false;
            _pinnedMode = pinned;                      // intent, recorded even before the HWND exists
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetParent(hwnd, IntPtr.Zero); // never leave it parented to another process
            ReassertPosition();
        }

        // Re-assert on a timer: an Explorer restart or a wallpaper tool raising itself must
        // not leave the bar in the wrong tier or in the wrong place.
        public void EnsurePinned()
        {
            Topmost = false;
            if (_settings == null || !_settings.Pinned) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!_pinnedMode) { SetPinned(true); return; }
            ReassertPosition();
            PersistPosition();
        }

        public void RestorePosition(Settings st)
        {
            // Settings hold device pixels (see ReassertPosition); the clamp repairs a
            // stale value after a resolution or monitor change.
            if (IsFinite(st.Left)) _leftPx = (int)Math.Round(st.Left);
            if (IsFinite(st.Top)) _topPx = (int)Math.Round(st.Top);
            _positionRestored = true;
            ReassertPosition();
        }

        // ---- dynamic cell layout driven by settings ----

        public void RebuildCells()
        {
            Panel.Children.Clear();
            _cells.Clear();
            var needSep = false;

            void Sep()
            {
                if (!needSep) return;
                Panel.Children.Add(new Border
                {
                    Width = 1,
                    Height = 24,
                    CornerRadius = new CornerRadius(0.5),
                    Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            // ValueMinWidth stops the bar from breathing as digit counts change
            // (9% -> 10%); sized from the offscreen render of a typical reading.
            void Cell(string key, string label, Geometry icon, double valueMinWidth)
            {
                Sep();
                var c = new MonitorCell { Margin = new Thickness(12, 0, 12, 0) };
                c.Label = label;
                c.Icon = icon;
                c.ValueMinWidth = valueMinWidth;
                Panel.Children.Add(c);
                _cells[key] = c;
                needSep = true;
            }

            Pill.Visibility = _settings != null && _settings.ShowPill ? Visibility.Visible : Visibility.Collapsed;
            if (_settings == null) return;

            if (_settings.ShowCpuLoad || _settings.ShowCpuTemp) Cell("cpu", "CPU", Icons.Cpu, 66);
            if (_settings.ShowGpuLoad || _settings.ShowGpuTemp) Cell("gpu", "显卡", Icons.Gpu, 66);
            if (_settings.ShowFan) Cell("fan", "显卡转速", Icons.Fan, 56);
            if (_settings.ShowRam) Cell("ram", "内存", Icons.Ram, 92);
            if (_settings.ShowVram) Cell("vram", "显存", Icons.Vram, 102);
            if (_settings.ShowNet) Cell("net", "网络", Icons.Net, 148);

            if (_last != null) UpdateValues(_last);
        }

        public void ApplySample(Sample s)
        {
            _last = s;
            UpdateValues(s);
        }

        private void UpdateValues(Sample s)
        {
            if (_settings == null) return;

            if (_cells.TryGetValue("cpu", out var cpu))
                cpu.Value = Join(
                    _settings.ShowCpuLoad ? Pct(s.CpuLoad) : null,
                    _settings.ShowCpuTemp ? Deg(s.CpuTemp) : null);

            if (_cells.TryGetValue("gpu", out var gpu))
                gpu.Value = Join(
                    _settings.ShowGpuLoad ? Pct(s.GpuLoad) : null,
                    _settings.ShowGpuTemp ? Deg(s.GpuTemp) : null);

            if (_cells.TryGetValue("fan", out var fan))
                fan.Value = s.GpuFanRpm.HasValue
                    ? Math.Round(s.GpuFanRpm.Value).ToString("0") + Thin + "RPM"
                    : "--" + Thin + "RPM";

            if (_cells.TryGetValue("ram", out var ram))
                ram.Value = $"{Pct(s.RamLoad)}  {Whole(s.RamUsedGb)}/{Whole(s.RamTotalGb)}GB";

            if (_cells.TryGetValue("vram", out var vram))
                vram.Value = s.VramLoad.HasValue && s.VramTotalMb.HasValue
                    ? $"{Pct(s.VramLoad)}  {OneDecimal(s.VramUsedMb.Value / 1024.0)}/{OneDecimal(s.VramTotalMb.Value / 1024.0)}GB"
                    : "--  --/--GB";

            if (_cells.TryGetValue("net", out var net))
                net.Value = $"↓{Rate(s.NetDownKbps)}  ↑{Rate(s.NetUpKbps)}";
        }

        private static string Join(params string[] parts)
        {
            var list = new List<string>();
            foreach (var p in parts) if (!string.IsNullOrEmpty(p)) list.Add(p);
            return string.Join("  ", list);
        }

        private static string Pct(double? v) => v.HasValue ? Math.Round(v.Value).ToString("0") + "%" : "--%";
        private static string Deg(double? v) => v.HasValue ? Math.Round(v.Value).ToString("0") + "℃" : "--℃";
        private static string Whole(double v) => Math.Round(v).ToString("0");
        private static string OneDecimal(double v) => v.ToString("0.#");

        // U+2009 keeps "12 MB/s" from looking glued together without adding a full space.
        const string Thin = "\u2009";

        // Fewer significant digits than before: a HUD that rewrites "17.65 -> 3.20 -> 0.00"
        // every second reads as noise, so small values drop to one decimal and mid values
        // to integers.
        private static string Rate(double kbps)
        {
            if (kbps < 0 || double.IsNaN(kbps) || double.IsInfinity(kbps)) kbps = 0;
            if (kbps >= 1024.0) return (kbps / 1024.0).ToString("0.0") + Thin + "MB/s";
            if (kbps >= 10.0) return Math.Round(kbps).ToString("0") + Thin + "KB/s";
            return kbps.ToString("0.0") + Thin + "KB/s";
        }

        // ---- drag: only when floating; DragMove is DPI-correct (manual PointToScreen
        // math mixed physical pixels with DIP and flung the window off-screen) ----

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ClickThrough) return;
            if (_settings != null && _settings.Pinned) return; // pinned bar is held at the desktop layer
            if (Pinned) return;
            _dragging = true;
            try { DragMove(); }
            catch (InvalidOperationException) { /* DragMove throws if button not down */ }
            finally
            {
                // DragMove runs a native message loop, so MouseLeftButtonUp may never reach us.
                _dragging = false;
                AdoptCurrentPosition();
                PersistPosition();
            }
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal struct PxPoint { internal int X, Y; internal PxPoint(int x, int y) { X = x; Y = y; } }

        internal static PxPoint ClampPoint(int left, int top, int w, int h, int wl, int wt, int wr, int wb)
        {
            int x = Math.Min(left, Math.Max(wl, wr - w));
            int y = Math.Min(top, Math.Max(wt, wb - h));
            if (x < wl) x = wl;
            if (y < wt) y = wt;
            return new PxPoint(x, y);
        }

        // Clamp with a monotonic size history so a bar parked against an edge settles
        // instead of chasing every pixel of readout width change.
        internal static PxPoint ClampMeasured(ref int widest, ref int tallest, int left, int top,
            int w, int h, int wl, int wt, int wr, int wb)
        {
            if (w > widest) widest = w;
            if (h > tallest) tallest = h;
            return ClampPoint(left, top, widest, tallest, wl, wt, wr, wb);
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ClickThrough) return;
            var app = (App)Application.Current;
            app.ToggleClickThroughFromUI();
        }

        // ---- diagnostic: what coordinate space does WPF actually see in this process? ----
        public string MetricsReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("time=" + DateTime.Now.ToString("O"));
            sb.AppendLine("SystemParameters.WorkArea=" + SystemParameters.WorkArea);
            sb.AppendLine("SystemParameters.VirtualScreen=" + new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
            sb.AppendLine("SystemParameters.PrimaryScreen=" + SystemParameters.PrimaryScreenWidth + "x" + SystemParameters.PrimaryScreenHeight);
            Show();
            UpdateLayout();
            sb.AppendLine("window Left/Top=" + Left + "," + Top + " Actual=" + ActualWidth + "x" + ActualHeight);
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                var t = src.CompositionTarget.TransformToDevice;
                sb.AppendLine("TransformToDevice=" + t.M11 + "," + t.M22);
                var p = t.Transform(new Point(Left, Top));
                sb.AppendLine("Left/Top in device px=" + p.X + "," + p.Y + " (compare with GetWindowRect)");
            }
            else sb.AppendLine("no PresentationSource");
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.GetWindowRect(hwnd, out var wr);
            sb.AppendLine("GetWindowRect=" + wr.Left + "," + wr.Top + "-" + wr.Right + "," + wr.Bottom
                + " size=" + (wr.Right - wr.Left) + "x" + (wr.Bottom - wr.Top));
            int bl, bt, br, bb;
            sb.AppendLine("app bounds=" + (TryBounds(out bl, out bt, out br, out bb) ? bl + "," + bt + "-" + br + "," + bb : "unavailable"));
            Hide();
            return sb.ToString();
        }

        // ---- offscreen render test (no window shown) ----
        public void RenderTestPng(string outputPath, double scale)
        {
            RebuildCells();
            ApplySample(new Sample
            {
                CpuLoad = 71, CpuTemp = 54,
                GpuLoad = 16, GpuTemp = 35,
                GpuFanRpm = 799,
                RamUsedGb = 18.0, RamTotalGb = 48.0,
                VramUsedMb = 3.3 * 1024, VramTotalMb = 7.9 * 1024,
                NetDownKbps = 5.0, NetUpKbps = 19.0,
            });

            var root = (FrameworkElement)Content;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height));
            root.UpdateLayout();

            int w = (int)Math.Ceiling(root.DesiredSize.Width * scale);
            int h = (int)Math.Ceiling(root.DesiredSize.Height * scale);
            var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(outputPath))
                encoder.Save(fs);
        }
    }
}
