using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskMon
{
    /// <summary>
    /// Watches which window owns the foreground and keeps Digital Vibrance in step
    /// with the running program: a configured level while a listed process is in
    /// front, the desktop level the moment anything else takes over.
    ///
    /// The hook is registered out of context, so callbacks arrive on whichever
    /// thread registered it - start this from the UI thread.
    /// </summary>
    public sealed class VibranceService : IDisposable
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint WineventOutofcontext = 0x0000;
        private const uint WineventSkipownprocess = 0x0002;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private readonly Settings _settings;
        private NvVibrance _nv;
        private IntPtr _hook;
        private WinEventDelegate _callback;   // must stay referenced for the hook's lifetime
        private int _appliedLevel = -1;
        private string _appliedFor = string.Empty;

        /// <summary>Level currently written to the driver, or -1 when nothing has been applied.</summary>
        public int AppliedLevel { get { return _appliedLevel; } }

        /// <summary>Process the current level belongs to; empty for the desktop level.</summary>
        public string AppliedFor { get { return _appliedFor; } }

        public bool Available { get { return _nv != null; } }

        /// <summary>Null when the driver was reached, otherwise why not.</summary>
        public string FailureReason { get; private set; }

        public string GpuName { get { return _nv != null ? _nv.GpuName : string.Empty; } }

        /// <summary>Raised after the applied level changes, so the UI can follow along.</summary>
        public event EventHandler Applied;

        public VibranceService(Settings settings)
        {
            _settings = settings;
            _nv = NvVibrance.Open();
            if (_nv == null)
            {
                FailureReason = "未检测到可用的 NVIDIA 数字震动接口";
                return;
            }
            FailureReason = null;
        }

        public void Start()
        {
            if (_nv == null) return;
            if (!_settings.VibranceEnabled) return;

            // Matches vibranceGUI's approach: one out-of-context hook for foreground
            // changes, no polling, negligible cost while idle.
            _callback = OnForegroundChanged;
            _hook = SetWinEventHook(
                EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback,
                0, 0, WineventOutofcontext | WineventSkipownprocess);

            ApplyForActiveWindow();
        }

        public void Stop()
        {
            RemoveHook();
            RestoreDefault();
        }

        /// <summary>Re-applies the level that belongs to whatever is in front right now.</summary>
        public void Refresh()
        {
            if (_nv == null) return;
            if (_settings.VibranceEnabled) ApplyForActiveWindow();
            else RestoreDefault();
        }

        /// <summary>Applies a level once, without touching the rule state - used for live previews.</summary>
        public bool Preview(int level)
        {
            return _nv != null && _nv.SetLevel(Clamp(level));
        }

        public static int Clamp(int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            return level;
        }

        private void ApplyForActiveWindow()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            Apply(ResolveLevel(hwnd));
        }

        private string ResolveProcessName(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                    return process.ProcessName;
            }
            catch (ArgumentException) { return null; }   // process already gone
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>Picks the configured level for the foreground process; falls back to the desktop level.</summary>
        private LevelChoice ResolveLevel(IntPtr hwnd)
        {
            var name = ResolveProcessName(hwnd);
            if (!string.IsNullOrEmpty(name) && _settings.VibranceApps != null)
            {
                foreach (var app in _settings.VibranceApps)
                {
                    if (app == null || string.IsNullOrEmpty(app.Process)) continue;
                    if (string.Equals(app.Process, name, StringComparison.OrdinalIgnoreCase))
                        return new LevelChoice(Clamp(app.Level), name);
                }
            }
            return new LevelChoice(Clamp(_settings.VibranceLevel), string.Empty);
        }

        private void OnForegroundChanged(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || !_settings.VibranceEnabled) return;
            try { Apply(ResolveLevel(hwnd)); }
            catch { }
        }

        private void Apply(LevelChoice choice)
        {
            if (_nv == null) return;
            if (choice.Level == _appliedLevel && choice.Process == _appliedFor) return;

            if (_nv.SetLevel(choice.Level))
            {
                _appliedLevel = choice.Level;
                _appliedFor = choice.Process;
                Notify();
            }
        }

        private void RestoreDefault()
        {
            if (_nv == null || _appliedLevel < 0) return;
            if (_nv.SetLevel(Clamp(_settings.VibranceLevel)))
            {
                _appliedLevel = Clamp(_settings.VibranceLevel);
                _appliedFor = string.Empty;
                Notify();
            }
        }

        private void Notify()
        {
            var handler = Applied;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RemoveHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWinEvent(_hook); }
            catch { }
            _hook = IntPtr.Zero;
            _callback = null;
        }

        public void Dispose()
        {
            RemoveHook();
            if (_nv == null) return;
            try { _nv.SetLevel(Clamp(_settings.VibranceLevel)); } catch { }
            _nv.Dispose();
            _nv = null;
        }

        private struct LevelChoice
        {
            public LevelChoice(int level, string process)
            {
                Level = level;
                Process = process;
            }

            public int Level;
            public string Process;
        }
    }
}
