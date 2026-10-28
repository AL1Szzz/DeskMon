using System;
using System.Runtime.InteropServices;

namespace DeskMon
{
    internal static class NativeMethods
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TRANSPARENT = 0x20;
        internal const int WS_EX_LAYERED = 0x80000;

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        internal enum AccentState { Disabled = 0, EnableGradient = 1, EnableTransparent = 2, EnableBlurBehind = 3, EnableAcrylicBlurBehind = 4 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public int Attribute; // 19 = WCA_ACCENT_POLICY
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        internal static void EnableBlurBehind(IntPtr hwnd)
        {
            try
            {
                var accent = new AccentPolicy { AccentState = AccentState.EnableBlurBehind };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = Marshal.SizeOf(accent) };
                SetWindowCompositionAttribute(hwnd, ref data);
                Marshal.FreeHGlobal(ptr);
            }
            catch { /* blur is cosmetic; ignore failures */ }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        // ---- desktop-tier window helpers (see MainWindow.SetPinned for why the bar is
        // never parented into Explorer's WorkerW and never sent to HWND_BOTTOM) ----

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        const uint GA_PARENT = 1;

        // GetParent returns NULL across process boundaries, which made a successful
        // SetParent into Explorer's WorkerW look like a failure (and retried forever).
        // GetWindow has no GW_PARENT command; GetAncestor(GA_PARENT) is the reliable call.
        internal static IntPtr QueryParent(IntPtr hwnd)
        {
            try { return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_PARENT); }
            catch { return IntPtr.Zero; }
        }

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        // 1.25 on a 125% display. GetWindowRect/SetWindowPos are physical pixels for this
        // DPI-aware process while WPF's SystemParameters are DIPs, so the two must be
        // converted before being compared.
        internal static double WindowScale(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return 1.0;
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 96 && dpi <= 480) return dpi / 96.0;
            }
            catch { }
            return 1.0;
        }

        // Move using the window's real device pixels. WPF's Left/Top are DIPs whose scale
        // stops matching the window after it is re-parented into Explorer's desktop layer,
        // so pixel maths here is the only clamp that stays correct in both pinned and
        // floating states; NOZORDER keeps the wallpaper raise logic in charge.
        // Move using the window's real device pixels. A child window's SetWindowPos x/y are
        // relative to its parent's client area, so convert the requested screen point first;
        // this keeps the bar where it was when Explorer recreates the desktop layer.
        internal static bool MoveInPixels(IntPtr hwnd, int screenX, int screenY)
        {
            try
            {
                var parent = QueryParent(hwnd);
                if (parent != IntPtr.Zero)
                {
                    var pt = new POINT { X = screenX, Y = screenY };
                    // A child's SetWindowPos x/y are parent-client coordinates.
                    if (ScreenToClient(parent, ref pt)) { screenX = pt.X; screenY = pt.Y; }
                }
                return SetWindowPos(hwnd, IntPtr.Zero, screenX, screenY, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { internal int X, Y; }

        [DllImport("user32.dll")]
        static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_NOZORDER = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        internal static void GetMemoryStatus(out ulong totalPhysMb, out ulong availPhysMb)
        {
            var st = new MEMORYSTATUSEX { Length = 64 };
            if (GlobalMemoryStatusEx(ref st))
            {
                totalPhysMb = st.TotalPhys / 1024 / 1024;
                availPhysMb = st.AvailPhys / 1024 / 1024;
            }
            else
            {
                totalPhysMb = 0;
                availPhysMb = 0;
            }
        }
    }
}
