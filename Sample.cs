namespace DeskMon
{
    public sealed class Sample
    {
        public double? CpuLoad;        // %
        public double? CpuTemp;        // C
        public double? GpuLoad;        // %
        public double? GpuTemp;        // C
        public double? GpuFanRpm;
        public double? VramUsedMb;
        public double? VramTotalMb;
        public double RamUsedGb;
        public double RamTotalGb;
        public double NetDownKbps;
        public double NetUpKbps;

        public double? VramLoad
        {
            get
            {
                if (VramUsedMb.HasValue && VramTotalMb.GetValueOrDefault() > 0)
                    return VramUsedMb.Value / VramTotalMb.Value * 100.0;
                return null;
            }
        }

        public double? RamLoad
        {
            get
            {
                if (RamTotalGb > 0) return RamUsedGb / RamTotalGb * 100.0;
                return null;
            }
        }
    }
}
