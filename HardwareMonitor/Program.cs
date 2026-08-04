using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices; // 1. 需要引入此命名空间来调用 Windows API
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

class Program
{
    private static volatile bool _isRunning = true;

    // --- Windows API 禁用快速编辑模式的相关定义 ---[cite: 2]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;

    // 禁用控制台快速编辑模式（防止鼠标点击挂起程序）[cite: 2]
    private static void DisableQuickEdit()
    {
        try
        {
            IntPtr consoleHandle = GetStdHandle(STD_INPUT_HANDLE);
            if (GetConsoleMode(consoleHandle, out uint mode))
            {
                // 清除 QuickEdit 和 MouseInput 标志[cite: 2]
                mode &= ~ENABLE_QUICK_EDIT_MODE;
                mode &= ~ENABLE_MOUSE_INPUT;
                SetConsoleMode(consoleHandle, mode);
            }
        }
        catch { }
    }
    // ------------------------------------------------

    static void Main(string[] args)
    {
        // 2. 运行一开始直接禁用快速编辑模式[cite: 2]
        DisableQuickEdit();

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _isRunning = false;
            Console.WriteLine("\n[INFO] Stopping...");
        };

        // 窗口关闭时强制退出
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            _isRunning = false;
        };

        Console.Clear();
        Console.WriteLine("Monitoring is enabled. Keep your Windows PC and Android phone on the same network.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine("By dddd_RC.");
        Console.WriteLine("--------------------------------------------------");

        var monitor = new HardwareMonitor();
        using var udpClient = new UdpClient();
        var endPoint = new IPEndPoint(IPAddress.Broadcast, 8888);

        var jsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        // 获取当前正在运行的本程序进程对象
        using var currentProcess = Process.GetCurrentProcess();

        // 记录内存状态打印所在的控制台行号（避免控制台滚动错位）
        int statusLineTop = Console.CursorTop;

        try
        {
            while (_isRunning)
            {
                var (fullCpuName, cpuUsage, gpuUsage, ramUsage, diskUsage, totalPower) = monitor.GetMetrics();

                double SafeRound(float val)
                {
                    if (float.IsNaN(val) || float.IsInfinity(val)) return 0;
                    return Math.Round(val, 0);
                }

                var data = new
                {
                    PcModel = monitor.SystemModelName ?? "PC",
                    CpuName = fullCpuName ?? "CPU",
                    GpuName = monitor.ActiveGpuName ?? "GPU",
                    RamName = monitor.RamInfoName ?? "RAM",
                    DiskName = monitor.DiskInfoName ?? "DISK",
                    CpuUsage = SafeRound(cpuUsage),
                    GpuUsage = SafeRound(gpuUsage),
                    RamUsage = SafeRound(ramUsage),
                    DiskUsage = SafeRound(diskUsage),
                    TotalPower = SafeRound(totalPower)
                };

                string jsonStr = JsonSerializer.Serialize(data, jsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(jsonStr);
                udpClient.Send(bytes, bytes.Length, endPoint);

                // --- 2. 动态获取并打印本程序的内存占用 ---
                currentProcess.Refresh(); // 刷新进程状态以获取最新数据
                // WorkingSet64: 物理内存占用（工作集，单位：字节）
                double workingSetMb = currentProcess.WorkingSet64 / (1024.0 * 1024.0);
                // PrivateMemorySize64: 专用内存占用（单位：字节）
                double privateMemMb = currentProcess.PrivateMemorySize64 / (1024.0 * 1024.0);

                // 光标回到固定行，覆盖刷新打印，避免一直向下刷屏
                try
                {
                    Console.SetCursorPosition(0, statusLineTop);
                    Console.Write($"[STATUS] Broadcasting... | App Self Memory: {workingSetMb:F1} MB (Private: {privateMemMb:F1} MB)   ");
                }
                catch
                {
                    // 防止控制台窗口太小或被清屏导致光标定位异常
                }

                // 用循环代替 Thread.Sleep，快速响应退出信号
                for (int i = 0; i < 5 && _isRunning; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            monitor.Close();
            Console.WriteLine("\nProgram stopped.");
            Environment.Exit(0);
        }
    }
}