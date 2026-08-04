using System;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;

public class HardwareMonitor
{
    private readonly Computer _computer;

    public string ActiveGpuName { get; private set; } = "GPU";
    public string SystemModelName { get; private set; } = "PC";
    public string RamInfoName { get; private set; } = "RAM";
    public string DiskInfoName { get; private set; } = "DISK";

    private string _cpuBaseName = "";
    private string _cpuCoresThreads = "";
    private string _cpuL3Cache = "";
    private float _cpuBaseClockGhz = 0;
    private int _systemDiskIndex = 0;

    public HardwareMonitor()
    {
        SystemModelName = GetBiosSystemModel();
        RamInfoName = GetPhysicalMemoryDetails();

        // 1. 获取 Windows 安装所在的物理磁盘编号，并读取规格
        _systemDiskIndex = GetSystemDrivePhysicalDiskIndex();
        DiskInfoName = GetAccurateSystemDiskInfo(_systemDiskIndex);

        InitCpuStaticInfo();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true
        };
        _computer.Open();

        // 2. 智能获取当前真正处于活动输出状态的显卡（方法名称已对齐）
        ActiveGpuName = GetRealActiveGpuNameWithVram();
    }

    // 安全传感器数值提取函数（防止 NaN / Infinity 导致 JSON 序列化崩溃）
    private float GetSensorValue(ISensor sensor)
    {
        if (sensor == null || !sensor.Value.HasValue) return 0f;
        float val = sensor.Value.Value;
        return (float.IsNaN(val) || float.IsInfinity(val)) ? 0f : val;
    }

    // 智能识别：真正用核显就显示核显，真正用独显就显示独显（全面支持 12G/24G/32G+ 大显存）
    private string GetRealActiveGpuNameWithVram()
    {
        string selectedGpuName = "";
        double vramGB = 0;

        try
        {
            // 1. 通过 WMI 识别当前活动输出的显卡名称
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Availability, CurrentBitsPerPixel FROM Win32_VideoController"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    var availability = obj["Availability"]?.ToString();
                    var bitsPerPixel = obj["CurrentBitsPerPixel"];

                    if (string.IsNullOrEmpty(name)) continue;

                    bool isActivelyDisplaying = (availability == "3" || availability == null) && (bitsPerPixel != null);

                    if (isActivelyDisplaying)
                    {
                        selectedGpuName = name;
                        break;
                    }
                }
            }

            // 2. 优先使用 LibreHardwareMonitor 读取显存（完美支持 24GB/32GB 大显存）
            var gpuHardware = _computer.Hardware.FirstOrDefault(h =>
                h.HardwareType == HardwareType.GpuNvidia ||
                h.HardwareType == HardwareType.GpuAmd ||
                h.HardwareType == HardwareType.GpuIntel);

            if (gpuHardware != null)
            {
                gpuHardware.Update();

                if (string.IsNullOrEmpty(selectedGpuName))
                {
                    selectedGpuName = gpuHardware.Name;
                }

                // 深度扫描 LibreHardwareMonitor 的显存传感器
                foreach (var sensor in gpuHardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.SmallData || sensor.SensorType == SensorType.Data)
                    {
                        string sName = sensor.Name.ToUpper();
                        // 动态匹配各家驱动对大显存的传感器命名
                        if (sName.Contains("MEMORY TOTAL") ||
                            sName.Contains("VRAM TOTAL") ||
                            sName.Contains("MEMORY DEDICATED") ||
                            sName.Contains("GPU MEMORY DEDICATED") ||
                            sName.Equals("VRAM"))
                        {
                            if (sensor.Value.HasValue && sensor.Value.Value > 0)
                            {
                                float rawVal = sensor.Value.Value;
                                // 智能单位识别：若原始值 > 1,000,000 则为 Bytes，否则为 MB
                                vramGB = rawVal > 1000000f ? rawVal / (1024.0 * 1024.0 * 1024.0) : rawVal / 1024.0;
                                if (vramGB > 0.5) break;
                            }
                        }
                    }
                }
            }

            // 3. 降级方案：从注册表 64 位路径精准读取 (防 24GB/32GB 溢出)
            if (vramGB <= 0.5 && !string.IsNullOrEmpty(selectedGpuName))
            {
                string registryPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        foreach (string subkeyName in key.GetSubKeyNames())
                        {
                            using (RegistryKey subkey = key.OpenSubKey(subkeyName))
                            {
                                if (subkey != null)
                                {
                                    string driverDesc = subkey.GetValue("DriverDesc")?.ToString();
                                    if (!string.IsNullOrEmpty(driverDesc) && driverDesc.Equals(selectedGpuName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        // 优先读取 64 位大显存专用注册表键值
                                        var memVal = subkey.GetValue("HardwareInformation.DedicatedVideoMemory") ??
                                                     subkey.GetValue("HardwareInformation.MemorySize");

                                        if (memVal != null)
                                        {
                                            ulong bytes = 0;
                                            if (memVal is byte[] bytesArr)
                                            {
                                                if (bytesArr.Length >= 8)
                                                    bytes = BitConverter.ToUInt64(bytesArr, 0);
                                                else if (bytesArr.Length >= 4)
                                                    bytes = BitConverter.ToUInt32(bytesArr, 0);
                                            }
                                            else
                                            {
                                                // 强制转换为 64 位无符号长整型，防止 24G 导致负数溢出
                                                long rawLong = Convert.ToInt64(memVal);
                                                bytes = rawLong < 0 ? (ulong)(rawLong + 4294967296L) : (ulong)rawLong;
                                            }

                                            if (bytes > 0)
                                            {
                                                if (bytes < 1000000) // MB 单位
                                                    vramGB = bytes / 1024.0;
                                                else // Bytes 单位
                                                    vramGB = bytes / (1024.0 * 1024.0 * 1024.0);
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 4. 兜底逻辑：保证 GPU 名称存在
        if (string.IsNullOrEmpty(selectedGpuName))
        {
            var firstGpu = _computer.Hardware.FirstOrDefault(h =>
                h.HardwareType == HardwareType.GpuNvidia ||
                h.HardwareType == HardwareType.GpuAmd ||
                h.HardwareType == HardwareType.GpuIntel);
            if (firstGpu != null) selectedGpuName = firstGpu.Name;
        }



        // ==================== 【测试赋值代码】 ====================
        // 直接强行覆盖 vramGB 的值，无需重复写 string vramStr
        
        //vramGB = 80.0;
        
        // =========================================================
        // 格式化显存显示（这里保留原有的声明，不要重复写 string vramStr = "";）




        // 格式化显存显示（24GB 会被完美格式化为 "24GB"）
        string vramStr = "";
        if (vramGB > 0.5)
        {
            int roundedGb = (int)Math.Round(vramGB);
            // 四舍五入修正：23.9GB 或 24.01GB 都会归整为 24GB
            if (Math.Abs(vramGB - roundedGb) < 0.3)
                vramStr = $"{roundedGb}GB";
            else
                vramStr = $"{Math.Round(vramGB, 1)}GB";
        }





        return !string.IsNullOrEmpty(vramStr) ? $"{selectedGpuName} {vramStr}" : selectedGpuName;
    }

    // 精准识别 Windows 安装所在的物理磁盘 Index
    private int GetSystemDrivePhysicalDiskIndex()
    {
        try
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.ToUpper().TrimEnd('\\') ?? "C:";

            using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string dependent = obj["Dependent"]?.ToString() ?? "";
                    string antecedent = obj["Antecedent"]?.ToString() ?? "";

                    if (dependent.ToUpper().Contains(systemDrive))
                    {
                        int diskIdxPos = antecedent.IndexOf("Disk #");
                        if (diskIdxPos != -1)
                        {
                            string subStr = antecedent.Substring(diskIdxPos + 6);
                            int commaPos = subStr.IndexOf(',');
                            if (commaPos != -1 && int.TryParse(subStr.Substring(0, commaPos), out int diskNum))
                            {
                                return diskNum;
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    // 精准识别系统盘型号与 SSD / HDD 介质类型
    private string GetAccurateSystemDiskInfo(int diskIndex)
    {
        try
        {
            string diskModel = "";
            string diskType = "SSD";
            double totalSizeGb = 0;

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT Model, Size, MediaType, BusType, DeviceId FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskIndex}'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        diskModel = obj["Model"]?.ToString().Trim() ?? "";
                        if (obj["Size"] != null && ulong.TryParse(obj["Size"].ToString(), out ulong bytes))
                        {
                            totalSizeGb = bytes / (1024.0 * 1024.0 * 1024.0);
                        }

                        ushort mediaType = Convert.ToUInt16(obj["MediaType"] ?? 0);
                        ushort busType = Convert.ToUInt16(obj["BusType"] ?? 0);

                        if (mediaType == 4 || busType == 17 || diskModel.ToUpper().Contains("NVME") || diskModel.ToUpper().Contains("SSD"))
                            diskType = "SSD";
                        else if (mediaType == 3)
                            diskType = "HDD";

                        break;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(diskModel))
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Size, Index FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        int index = Convert.ToInt32(obj["Index"] ?? -1);
                        if (index == diskIndex || string.IsNullOrEmpty(diskModel))
                        {
                            diskModel = obj["Model"]?.ToString().Trim() ?? "DISK";
                            if (obj["Size"] != null && ulong.TryParse(obj["Size"].ToString(), out ulong bytes))
                            {
                                totalSizeGb = bytes / (1024.0 * 1024.0 * 1024.0);
                            }

                            string upperModel = diskModel.ToUpper();
                            if (upperModel.Contains("SSD") || upperModel.Contains("NVME") || upperModel.Contains("OPTANE"))
                                diskType = "SSD";

                            if (index == diskIndex) break;
                        }
                    }
                }
            }

            int gb = (int)Math.Round(totalSizeGb);
            return $"{diskModel} {gb}GB {diskType}".Trim();
        }
        catch { return "SSD 1024GB"; }
    }

    private void InitCpuStaticInfo()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    _cpuBaseName = obj["Name"]?.ToString().Trim() ?? "CPU";

                    int cores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                    int threads = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                    _cpuCoresThreads = cores > 0 ? $"{cores}C/{threads}T" : "";

                    if (obj["MaxClockSpeed"] != null && float.TryParse(obj["MaxClockSpeed"].ToString(), out float mhz))
                    {
                        _cpuBaseClockGhz = mhz / 1000f;
                    }

                    uint l3Kb = Convert.ToUInt32(obj["L3CacheSize"] ?? 0);
                    if (l3Kb > 0)
                    {
                        string l3Str = l3Kb >= 1024 ? $"{l3Kb / 1024f:0.#}M" : $"{l3Kb}K";
                        _cpuL3Cache = l3Str;
                    }
                    break;
                }
            }
        }
        catch { _cpuBaseName = "CPU"; }
    }

    // 内存信息识别（支持 DDR3/4/5，兼容老旧主板与 Win8.1）
    private string GetPhysicalMemoryDetails()
    {
        try
        {
            ulong totalBytes = 0;
            string memoryType = "RAM";
            string manufacturer = "";
            uint speed = 0;

            using (var searcher = new ManagementObjectSearcher("SELECT Capacity, MemoryType, SMBIOSMemoryType, Manufacturer, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["Capacity"] != null && ulong.TryParse(obj["Capacity"].ToString(), out ulong cap))
                        totalBytes += cap;

                    if (string.IsNullOrEmpty(manufacturer) && obj["Manufacturer"] != null)
                    {
                        string mfg = obj["Manufacturer"].ToString().Trim();
                        if (!string.IsNullOrEmpty(mfg) &&
                            !mfg.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                            !mfg.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                            !mfg.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                        {
                            manufacturer = mfg;
                        }
                    }

                    if (memoryType == "RAM")
                    {
                        int smbiosType = obj["SMBIOSMemoryType"] != null ? Convert.ToInt32(obj["SMBIOSMemoryType"]) : 0;
                        int legacyType = obj["MemoryType"] != null ? Convert.ToInt32(obj["MemoryType"]) : 0;

                        if (smbiosType == 24 || legacyType == 24) memoryType = "DDR3";
                        else if (smbiosType == 26 || legacyType == 26) memoryType = "DDR4";
                        else if (smbiosType == 34) memoryType = "DDR5";
                    }

                    if (speed == 0)
                    {
                        if (obj["ConfiguredClockSpeed"] != null && uint.TryParse(obj["ConfiguredClockSpeed"].ToString(), out uint cfgSpeed) && cfgSpeed > 0)
                            speed = cfgSpeed;
                        else if (obj["Speed"] != null && uint.TryParse(obj["Speed"].ToString(), out uint rawSpeed))
                            speed = rawSpeed;
                    }
                }
            }

            int totalGb = (int)(totalBytes / (1024 * 1024 * 1024));
            string speedStr = speed > 0 ? $"{speed}MHz" : "";

            string result = "";
            if (!string.IsNullOrEmpty(manufacturer)) result += $"{manufacturer.ToUpper()} ";
            if (totalGb > 0) result += $"{totalGb}GB ";
            result += $"{memoryType} ";
            if (!string.IsNullOrEmpty(speedStr)) result += speedStr;

            return result.Trim();
        }
        catch { return "RAM"; }
    }

    private string GetBiosSystemModel()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystemProduct"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name) && name.ToUpper() != "SYSTEM PRODUCT NAME")
                        return name;
                }
            }
        }
        catch { }
        return Environment.MachineName;
    }

    public (string fullCpuName, float cpuUsage, float gpuUsage, float ramUsage, float diskUsage, float totalPower) GetMetrics()
    {
        float cpuUsage = 0, gpuUsage = 0, ramUsage = 0, diskUsage = 0;
        float cpuPower = 0, gpuPower = 0;

        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();

            if (hardware.HardwareType == HardwareType.Cpu)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total"))
                        cpuUsage = GetSensorValue(sensor);

                    if (sensor.SensorType == SensorType.Power && (sensor.Name.Contains("Package") || sensor.Name.Contains("Total")))
                        cpuPower = Math.Max(cpuPower, GetSensorValue(sensor));
                }
            }

            // GPU 动态扫描
            if (hardware.HardwareType == HardwareType.GpuNvidia ||
                hardware.HardwareType == HardwareType.GpuAmd ||
                hardware.HardwareType == HardwareType.GpuIntel)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load && (sensor.Name.Contains("Core") || sensor.Name.Contains("D3D")))
                        gpuUsage = Math.Max(gpuUsage, GetSensorValue(sensor));

                    if (sensor.SensorType == SensorType.Power)
                    {
                        if (sensor.Name.Contains("Board") || sensor.Name.Contains("PPT") || sensor.Name.Contains("Total"))
                            gpuPower = Math.Max(gpuPower, GetSensorValue(sensor));
                        else if (gpuPower == 0 && sensor.Name.Contains("GPU"))
                            gpuPower = GetSensorValue(sensor);
                    }
                }
            }

            if (hardware.HardwareType == HardwareType.Memory)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Memory"))
                        ramUsage = GetSensorValue(sensor);
                }
            }

            if (hardware.HardwareType == HardwareType.Storage)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total"))
                        diskUsage = Math.Max(diskUsage, GetSensorValue(sensor));
                }
            }
        }

        float dynamicBasePower = (cpuUsage < 10 && gpuUsage < 10) ? 22f : 32f;
        float hostTotalPower = cpuPower + gpuPower + dynamicBasePower;

        string baseClockStr = _cpuBaseClockGhz > 0 ? $"{_cpuBaseClockGhz:0.0}GHz" : "";
        string cleanL3Cache = _cpuL3Cache.Replace("L3:", "");
        string dynamicCpuName = $"{_cpuBaseName} {_cpuCoresThreads} {baseClockStr} {cleanL3Cache}".Trim();

        return (dynamicCpuName, cpuUsage, gpuUsage, ramUsage, diskUsage, hostTotalPower);
    }

    public void Close() => _computer.Close();
}