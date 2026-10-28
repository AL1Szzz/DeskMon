using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace DeskMon
{
    internal sealed class SensorService : IDisposable
    {
        public event Action<Sample> Updated;

        private readonly Computer _computer;
        private readonly Timer _timer;
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<bool> _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly UpdateVisitor _visitor = new UpdateVisitor();
        private ISensor _cpuTotal, _cpuPackage, _gpuCoreTemp, _gpuCoreLoad, _gpuFan, _gpuMemLoad;
        private readonly Dictionary<string, NetworkCounter> _networkCounters = new Dictionary<string, NetworkCounter>();
        private NetworkInterface[] _interfaces = Array.Empty<NetworkInterface>();
        private long _nextNetworkRefresh, _nextHardwareRetry;
        private int _refreshNetwork = 1, _disposed;
        private bool _opened, _resolved;
        private double? _vramTotalMb;
        private int _failures;

        public SensorService()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                // RAM comes from GlobalMemoryStatusEx; the LHM memory scan was unused.
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
            };
            _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
            NetworkChange.NetworkAddressChanged += NetworkChanged;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    _timer.Change(500, Timeout.Infinite);
            }
        }

        private void NetworkChanged(object sender, EventArgs e) => Interlocked.Exchange(ref _refreshNetwork, 1);

        private void ResolveSensors()
        {
            _cpuTotal = _cpuPackage = _gpuCoreTemp = _gpuCoreLoad = _gpuFan = _gpuMemLoad = null;
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    _cpuTotal = Find(hw, SensorType.Load, "CPU Total");
                    _cpuPackage = Find(hw, SensorType.Temperature, "CPU Package");
                }
                else if (hw.HardwareType == HardwareType.GpuNvidia)
                {
                    _gpuCoreTemp = Find(hw, SensorType.Temperature, "GPU Core");
                    _gpuCoreLoad = Find(hw, SensorType.Load, "GPU Core");
                    _gpuFan = Find(hw, SensorType.Fan, "GPU Fan") ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    _gpuMemLoad = Find(hw, SensorType.Load, "GPU Memory");
                }
            }
        }

        private static ISensor Find(IHardware hw, SensorType type, string name)
            => hw.Sensors.FirstOrDefault(s => s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        private static double? QueryVramTotalMb()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0 || !output.Wait(500)) return null;
                    var line = output.Result.Trim();
                    if (double.TryParse(line.Split('\n')[0].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var mb) && mb > 0 && !double.IsInfinity(mb))
                        return mb;
                }
            }
            catch { }
            // Unknown is preferable to displaying a fabricated capacity.
            return null;
        }

        private void SampleHardware(Sample s)
        {
            if (Stopwatch.GetTimestamp() < _nextHardwareRetry) return;
            try
            {
                if (!_opened)
                {
                    _computer.Open();
                    _opened = true;
                    _vramTotalMb = QueryVramTotalMb();
                }
                _computer.Accept(_visitor);
                // Some LHM sensors only appear after their first update.
                if (!_resolved) { ResolveSensors(); _resolved = true; }
                s.CpuLoad = _cpuTotal?.Value;
                s.CpuTemp = _cpuPackage?.Value;
                s.GpuLoad = _gpuCoreLoad?.Value;
                s.GpuTemp = _gpuCoreTemp?.Value;
                s.GpuFanRpm = _gpuFan?.Value;
                s.VramTotalMb = _vramTotalMb;
                s.VramUsedMb = _gpuMemLoad?.Value != null && _vramTotalMb.HasValue
                    ? _gpuMemLoad.Value / 100.0 * _vramTotalMb.Value : (double?)null;
                _failures = 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DeskMon hardware sample: " + ex.Message);
                if (!_opened || ++_failures >= 5)
                {
                    CloseHardware();
                    _nextHardwareRetry = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                }
            }
        }

        private void Tick(object state)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var s = new Sample();
                    SampleHardware(s);
                    NativeMethods.GetMemoryStatus(out var totalMb, out var availMb);
                    s.RamTotalGb = totalMb / 1024.0;
                    s.RamUsedGb = (totalMb - availMb) / 1024.0;
                    // A disconnected adapter must not stop CPU/GPU/RAM updates.
                    try { SampleNetwork(s); }
                    catch (NetworkInformationException) { Interlocked.Exchange(ref _refreshNetwork, 1); }
                    if (Volatile.Read(ref _disposed) == 0) Updated?.Invoke(s);
                }
                catch (Exception ex) { Debug.WriteLine("DeskMon sample: " + ex.Message); }
                finally
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        // One-shot rearm: slow driver calls can never overlap or queue a backlog.
                        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                        var delay = (int)Math.Max(50, 1000 - elapsedMs);
                        try { _timer.Change(delay, Timeout.Infinite); }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        private void SampleNetwork(Sample s)
        {
            var now = Stopwatch.GetTimestamp();
            if (Interlocked.Exchange(ref _refreshNetwork, 0) != 0 || now >= _nextNetworkRefresh)
            {
                try
                {
                    _interfaces = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        .ToArray();
                    var ids = new HashSet<string>(_interfaces.Select(n => n.Id));
                    foreach (var id in _networkCounters.Keys.Where(id => !ids.Contains(id)).ToArray())
                        _networkCounters.Remove(id);
                    _nextNetworkRefresh = now + 15 * Stopwatch.Frequency;
                }
                catch (NetworkInformationException)
                {
                    _nextNetworkRefresh = now + Stopwatch.Frequency;
                }
            }

            foreach (var ni in _interfaces)
            {
                try
                {
                    var st = ni.GetIPv4Statistics();
                    var current = new NetworkCounter { Rx = st.BytesReceived, Tx = st.BytesSent, Timestamp = now };
                    if (_networkCounters.TryGetValue(ni.Id, out var previous))
                    {
                        var dt = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
                        s.NetDownKbps += CounterRate(current.Rx, previous.Rx, dt);
                        s.NetUpKbps += CounterRate(current.Tx, previous.Tx, dt);
                    }
                    _networkCounters[ni.Id] = current;
                }
                catch (NetworkInformationException)
                {
                    _networkCounters.Remove(ni.Id);
                    Interlocked.Exchange(ref _refreshNetwork, 1);
                }
            }
        }

        internal static double CounterRate(long current, long previous, double seconds)
        {
            // A new/reset adapter establishes its own baseline, never a speed spike.
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || current < previous || previous < 0)
                return 0;
            return (current - previous) / seconds / 1024.0;
        }

        private void CloseHardware()
        {
            try { _computer.Close(); } catch { }
            _opened = _resolved = false;
            _vramTotalMb = null;
            _cpuTotal = _cpuPackage = _gpuCoreTemp = _gpuCoreLoad = _gpuFan = _gpuMemLoad = null;
            _failures = 0;
        }

        public Task StopAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                NetworkChange.NetworkAddressChanged -= NetworkChanged;
                _timer.Dispose();
                // Close can itself block in a driver, so even idle cleanup is off-UI.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { lock (_gate) CloseHardware(); }
                    finally { _stopped.TrySetResult(true); }
                });
            }
            return _stopped.Task;
        }

        public void Dispose() => StopAsync();

        private struct NetworkCounter
        {
            public long Rx, Tx, Timestamp;
        }

        private sealed class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
    }
}
