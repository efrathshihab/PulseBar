using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// LibreHardwareMonitor দিয়ে CPU আর GPU temperature পড়ে, প্রতি ২ সেকেন্ডে।
/// CPU এর জন্য admin আর PawnIO driver লাগে; না থাকলে শুরুতে একবার চেষ্টা করে থেমে যায়
/// আর <see cref="Status"/> এ কারণ রাখে। GPU temp (যেমন NVIDIA) অনেক সময় admin ছাড়াও পাওয়া যায়।
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // double? atomic না, তাই bits হিসেবে রাখা; NaN মানে null
    private long _celsiusBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private long _gpuCelsiusBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private volatile TemperatureStatus _status = TemperatureStatus.Starting;

    public double? CurrentCelsius => ReadBits(ref _celsiusBits);

    public double? GpuCelsius => ReadBits(ref _gpuCelsiusBits);

    public TemperatureStatus Status => _status;

    /// <summary>Main GPU এর নাম (GpuMonitor থেকে); একাধিক GPU থাকলে এটার সাথে মিলিয়ে বাছাই হয়।</summary>
    public string? PreferredGpuName { get; set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // Computer.Open() ধীর, তাই পুরোটা background এ
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_loop is null)
            {
                return;
            }

            _cts!.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancel, উপেক্ষা
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        bool isAdmin = IsRunningAsAdmin();
        bool hasDriver = IsPawnIoInstalled();
        Computer? computer = null;

        try
        {
            computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            computer.Open();

            var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var gpu = PickGpu(computer.Hardware, PreferredGpuName);
            ISensor? cpuSensor = null;
            ISensor? gpuSensor = null;

            if (cpu is not null)
            {
                Update(cpu);
                cpuSensor = PickSensor(cpu);
            }

            if (gpu is not null)
            {
                Update(gpu);
                gpuSensor = PickGpuSensor(gpu);
            }

            // CPU
            bool cpuActive;
            if (ValidValue(cpuSensor) is { } firstCpu)
            {
                Publish(firstCpu);
                cpuActive = true;
            }
            else
            {
                SetBits(ref _celsiusBits, null);
                _status = !isAdmin ? TemperatureStatus.NotAdmin
                        : !hasDriver ? TemperatureStatus.NoDriver
                        : TemperatureStatus.NoSensor;

                // Admin বা driver না থাকলে বারবার চেষ্টা করে লাভ নেই
                cpuActive = isAdmin && hasDriver && cpu is not null;
            }

            // GPU: শুরুতে মান না পেলে আর চেষ্টা নয়
            bool gpuActive = ValidValue(gpuSensor) is not null;
            SetBits(ref _gpuCelsiusBits, ValidValue(gpuSensor));

            if (!cpuActive && !gpuActive)
            {
                return;
            }

            using var timer = new PeriodicTimer(UpdateInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (cpuActive)
                {
                    Update(cpu!);
                    if (ValidValue(cpuSensor) is null)
                    {
                        cpuSensor = PickSensor(cpu!);
                    }

                    if (ValidValue(cpuSensor) is { } value)
                    {
                        Publish(value);
                    }
                    else
                    {
                        SetBits(ref _celsiusBits, null);
                        _status = TemperatureStatus.NoSensor;
                    }
                }

                if (gpuActive)
                {
                    Update(gpu!);
                    SetBits(ref _gpuCelsiusBits, ValidValue(gpuSensor));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // স্বাভাবিক থামা
        }
        catch
        {
            SetBits(ref _celsiusBits, null);
            SetBits(ref _gpuCelsiusBits, null);
            _status = !isAdmin ? TemperatureStatus.NotAdmin
                    : !hasDriver ? TemperatureStatus.NoDriver
                    : TemperatureStatus.NoSensor;
        }
        finally
        {
            try
            {
                computer?.Close();
            }
            catch
            {
                // বন্ধ করার সময়ের ভুল উপেক্ষা
            }
        }
    }

    private void Publish(float value)
    {
        SetBits(ref _celsiusBits, value);
        _status = TemperatureStatus.Ok;
    }

    private static double? ReadBits(ref long bits)
    {
        double value = BitConverter.Int64BitsToDouble(Interlocked.Read(ref bits));
        return double.IsNaN(value) ? null : value;
    }

    private static void SetBits(ref long bits, double? value)
    {
        Interlocked.Exchange(ref bits, BitConverter.DoubleToInt64Bits(value ?? double.NaN));
    }

    /// <summary>
    /// Main GPU এর নামের সাথে মেলে এমন GPU; না পেলে আলাদা (NVIDIA/AMD) GPU, তারপর Intel।
    /// </summary>
    private static IHardware? PickGpu(IList<IHardware> hardware, string? preferredName)
    {
        IHardware? discrete = null;
        IHardware? any = null;

        foreach (var h in hardware)
        {
            if (h.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(preferredName)
                && (h.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase)
                    || preferredName.Contains(h.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return h;
            }

            any ??= h;
            if (h.HardwareType != HardwareType.GpuIntel)
            {
                discrete ??= h;
            }
        }

        return discrete ?? any;
    }

    /// <summary>GPU এর "GPU Core" temperature sensor।</summary>
    private static ISensor? PickGpuSensor(IHardware gpu)
    {
        foreach (var sensor in AllSensors(gpu))
        {
            if (sensor.SensorType == SensorType.Temperature
                && sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                return sensor;
            }
        }

        return null;
    }


    private static void Update(IHardware cpu)
    {
        cpu.Update();
        foreach (var sub in cpu.SubHardware)
        {
            sub.Update();
        }
    }

    /// <summary>
    /// আগে "CPU Package" (Intel) বা "Tctl/Tdie" (AMD); না পেলে Core sensor গুলোর মধ্যে যেটার মান সবচেয়ে বেশি।
    /// </summary>
    private static ISensor? PickSensor(IHardware cpu)
    {
        ISensor? hottestCore = null;

        foreach (var sensor in AllSensors(cpu))
        {
            if (sensor.SensorType != SensorType.Temperature || ValidValue(sensor) is null)
            {
                continue;
            }

            string name = sensor.Name;
            if (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
            {
                return sensor;
            }

            bool isCore = name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                          && !name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                          && !name.Contains("TjMax", StringComparison.OrdinalIgnoreCase);

            if (isCore && (hottestCore is null || sensor.Value > hottestCore.Value))
            {
                hottestCore = sensor;
            }
        }

        return hottestCore;
    }

    /// <summary>
    /// Driver ছাড়া কিছু CPU (যেমন AMD Tctl/Tdie) null না দিয়ে 0 দেয়, তাই অবাস্তব মান বাদ।
    /// </summary>
    private static float? ValidValue(ISensor? sensor)
    {
        return sensor?.Value is { } v && v > 0 && v < 150 ? v : null;
    }

    private static IEnumerable<ISensor> AllSensors(IHardware cpu)
    {
        foreach (var sensor in cpu.Sensors)
        {
            yield return sensor;
        }

        foreach (var sub in cpu.SubHardware)
        {
            foreach (var sensor in sub.Sensors)
            {
                yield return sensor;
            }
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPawnIoInstalled()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        }
        catch
        {
            return false;
        }
    }
}
