using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QwenStudio.Core
{
    public sealed class GpuSample
    {
        public string Name = "GPU";
        public double UsedMiB, TotalMiB;
        public double PowerW, LimitW;
        public int TempC, Util;
        public bool Ok;
    }

    /// <summary>Reads GPU 0 through nvml.dll (ships with the NVIDIA driver) — no nvidia-smi process per tick.</summary>
    public sealed class Gpu
    {
        [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong Total, Free, Used; }
        [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int Handle(uint i, out IntPtr dev);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] static extern int GetName(IntPtr dev, byte[] name, uint len);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int GetMem(IntPtr dev, out Mem m);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int GetPower(IntPtr dev, out uint mW);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] static extern int GetLimit(IntPtr dev, out uint mW);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int GetTemp(IntPtr dev, int sensor, out uint c);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int GetUtil(IntPtr dev, out Utilization u);
        [DllImport("nvml.dll", EntryPoint = "nvmlSystemGetDriverVersion")] static extern int GetDriver(byte[] version, uint len);

        /// <summary>NVIDIA driver version ("581.57"), known after the first successful Read.</summary>
        public string Driver { get; private set; }

        IntPtr dev;
        string name = "GPU";
        bool ready, failed;

        public GpuSample Read()
        {
            var s = new GpuSample();
            if (failed) return s;
            try
            {
                if (!ready)
                {
                    if (Init() != 0 || Handle(0, out dev) != 0) { failed = true; return s; }
                    var buf = new byte[96];
                    if (GetName(dev, buf, (uint)buf.Length) == 0) name = Encoding.ASCII.GetString(buf).TrimEnd('\0').Replace("NVIDIA GeForce ", "");
                    var drv = new byte[80];
                    if (GetDriver(drv, (uint)drv.Length) == 0) Driver = Encoding.ASCII.GetString(drv).TrimEnd('\0');
                    ready = true;
                }
                s.Name = name;
                if (GetMem(dev, out var m) == 0) { s.UsedMiB = m.Used / 1048576.0; s.TotalMiB = m.Total / 1048576.0; }
                if (GetPower(dev, out var p) == 0) s.PowerW = p / 1000.0;
                if (GetLimit(dev, out var l) == 0) s.LimitW = l / 1000.0;
                if (GetTemp(dev, 0, out var t) == 0) s.TempC = (int)t;
                if (GetUtil(dev, out var u) == 0) s.Util = (int)u.Gpu;
                s.Ok = s.TotalMiB > 0;
            }
            catch (Exception) { failed = true; }
            return s;
        }
    }
}
