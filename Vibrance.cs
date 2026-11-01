using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskMon
{
    /// <summary>
    /// NVIDIA Digital Vibrance, driven through NVAPI - the configuration interface
    /// that ships inside every NVIDIA display driver.
    ///
    /// Entry points are resolved at runtime from nvapi64.dll / nvapi.dll, so no
    /// redistributable binary is bundled and the API always matches the driver
    /// actually installed. Levels are 0-100 percent - the same scale the NVIDIA
    /// Control Panel shows, with 50 meaning "no change".
    /// </summary>
    internal sealed class NvVibrance : IDisposable
    {
        // NvAPI interface identifiers: opaque hashes handed to nvapi_QueryInterface.
        private const uint IdInitialize = 0x0150E828;
        private const uint IdUnload = 0xD22BDD7E;
        private const uint IdEnumPhysicalGpus = 0xE5AC921F;
        private const uint IdGpuGetFullName = 0xCEEE8E9F;
        private const uint IdEnumDisplayHandles = 0x9ABDD40D;
        private const uint IdGetDvcInfo = 0x0E45002D;
        private const uint IdSetDvcLevel = 0x4A82C2B1;

        // NV_DISPLAY_DVC_INFO_EX. Version tag 1 is the only form modern drivers
        // accept; it reports the 0-100 percent scale instead of the legacy 0-63 one.
        [StructLayout(LayoutKind.Sequential)]
        private struct DvcInfo
        {
            public uint Version;
            public int CurrentLevel;
            public int MinLevel;
            public int MaxLevel;
            public int DefaultLevel;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr QueryInterface_t(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Initialize_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Unload_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumPhysicalGpus_t([In, Out] IntPtr[] handles, out int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GpuGetFullName_t(IntPtr gpu, [MarshalAs(UnmanagedType.LPStr)] StringBuilder name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumDisplayHandles_t(int index, out IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDvcInfo_t(IntPtr display, uint outputId, ref DvcInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetDvcLevel_t(IntPtr display, uint outputId, ref DvcInfo info);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        private IntPtr _module;
        private QueryInterface_t _query;
        private readonly List<IntPtr> _displays = new List<IntPtr>();

        /// <summary>Top of the usable range as reported by the driver.</summary>
        public int MaxLevel { get; private set; } = 100;

        /// <summary>Name of the first physical GPU, shown in the UI.</summary>
        public string GpuName { get; private set; } = string.Empty;

        /// <summary>Why NVAPI could not be used; null once Initialized.</summary>
        public string FailureReason { get; private set; }

        public bool Initialized { get { return _module != IntPtr.Zero && _displays.Count > 0; } }

        /// <summary>Opens a session, returning null rather than throwing when NVAPI is unusable.</summary>
        public static NvVibrance Open()
        {
            var nv = new NvVibrance();
            return nv.TryInitialize() ? nv : null;
        }

        private bool TryInitialize()
        {
            try
            {
                _module = LoadLibrary(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
                if (_module == IntPtr.Zero)
                {
                    FailureReason = "未找到 NVIDIA 驱动接口（nvapi64.dll）";
                    return false;
                }

                var entry = GetProcAddress(_module, "nvapi_QueryInterface");
                if (entry == IntPtr.Zero)
                {
                    FailureReason = "驱动接口缺少 nvapi_QueryInterface";
                    return false;
                }
                _query = Marshal.GetDelegateForFunctionPointer<QueryInterface_t>(entry);

                if (Bind<Initialize_t>(IdInitialize)() != 0)
                {
                    FailureReason = "NVAPI 初始化失败";
                    return false;
                }

                ReadGpuName();
                EnumerateDisplays();
                if (_displays.Count == 0)
                {
                    FailureReason = "没有找到 NVIDIA 显示器";
                    return false;
                }
                if (!ReadRange())
                {
                    FailureReason = "读不到数字震动范围，显卡或显示器可能不支持";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                FailureReason = ex.Message;
                Release();
                return false;
            }
        }

        private T Bind<T>(uint id)
        {
            var ptr = _query(id);
            if (ptr == IntPtr.Zero) throw new Exception("驱动未提供所需接口 0x" + id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private void ReadGpuName()
        {
            var handles = new IntPtr[64];
            int count;
            if (Bind<EnumPhysicalGpus_t>(IdEnumPhysicalGpus)(handles, out count) != 0 || count <= 0) return;

            var name = new StringBuilder(128);
            Bind<GpuGetFullName_t>(IdGpuGetFullName)(handles[0], name);
            GpuName = name.ToString();
        }

        private void EnumerateDisplays()
        {
            var enumerate = Bind<EnumDisplayHandles_t>(IdEnumDisplayHandles);
            for (int i = 0; i < 32; i++)
            {
                IntPtr handle;
                if (enumerate(i, out handle) != 0 || handle == IntPtr.Zero) break;
                _displays.Add(handle);
            }
        }

        private bool ReadRange()
        {
            var info = NewInfo();
            if (Bind<GetDvcInfo_t>(IdGetDvcInfo)(_displays[0], 0, ref info) != 0) return false;
            MaxLevel = info.MaxLevel > 0 ? info.MaxLevel : 100;
            return true;
        }

        private static DvcInfo NewInfo()
        {
            return new DvcInfo
            {
                Version = (uint)Marshal.SizeOf(typeof(DvcInfo)) | (1u << 16)
            };
        }

        /// <summary>Level currently applied to the first NVIDIA display; -1 when unreadable.</summary>
        public int GetLevel()
        {
            if (!Initialized) return -1;
            try
            {
                var info = NewInfo();
                return Bind<GetDvcInfo_t>(IdGetDvcInfo)(_displays[0], 0, ref info) == 0
                    ? info.CurrentLevel
                    : -1;
            }
            catch { return -1; }
        }

        /// <summary>Applies a level to every NVIDIA display, clamped to the reported range.</summary>
        public bool SetLevel(int level)
        {
            if (!Initialized) return false;
            if (level < 0) level = 0;
            if (level > MaxLevel) level = MaxLevel;
            try
            {
                var setLevel = Bind<SetDvcLevel_t>(IdSetDvcLevel);
                var ok = true;
                foreach (var display in _displays)
                {
                    var info = NewInfo();
                    info.CurrentLevel = level;
                    if (setLevel(display, 0, ref info) != 0) ok = false;
                }
                return ok;
            }
            catch { return false; }
        }

        // vibranceGUI measures intensity on a 0-63 scale covering only the upper half
        // of percent space; this table maps those steps back to percentages.
        private static readonly int[] LegacyLevels =
        {
            0,1,3,4,5,6,8,9,10,11,13,14,15,16,18,19,20,21,23,24,25,26,28,29,30,
            32,33,34,35,37,38,39,40,42,43,44,45,47,48,49,50,52,53,54,55,57,58,59,60,62,63
        };

        /// <summary>Converts a vibranceGUI level (0-63) to a 0-100 percent value.</summary>
        public static int FromLegacyLevel(int legacy)
        {
            for (int i = 0; i < LegacyLevels.Length; i++)
            {
                if (LegacyLevels[i] >= legacy) return 50 + i;
            }
            return 100;
        }

        private void Release()
        {
            try { if (_module != IntPtr.Zero) FreeLibrary(_module); }
            catch { }
            _module = IntPtr.Zero;
            _query = null;
            _displays.Clear();
        }

        public void Dispose()
        {
            if (_module == IntPtr.Zero) return;
            try { Bind<Unload_t>(IdUnload)(); }
            catch { }
            Release();
        }
    }
}
