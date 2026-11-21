using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Reads CPU and GPU temperature through LibreHardwareMonitor, every 2 seconds.
/// The CPU needs admin and the PawnIO driver; without them it tries once at startup, stops,
/// and keeps the reason in <see cref="Status"/>. GPU temp (e.g. NVIDIA) is often available without admin.
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // double? isn't atomic, so it's stored as bits; NaN means null
    private long _celsiusBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private long _gpuCelsiusBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private volatile TemperatureStatus _status = TemperatureStatus.Starting;

    public double? CurrentCelsius => ReadBits(ref _celsiusBits);

    public double? GpuCelsius => ReadBits(ref _gpuCelsiusBits);

    public TemperatureStatus Status => _status;

    /// <summary>Name of the main GPU (from GpuMonitor); with several GPUs, the matching one is picked.</summary>
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
            // Computer.Open() is slow, so all of it runs in the background
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
            // Cancelled, ignore
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

                // Without admin or the driver, retrying is pointless
                cpuActive = isAdmin && hasDriver && cpu is not null;
            }

            // GPU: if there's no value at startup, don't try again
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
            // Normal stop
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
                // Ignore errors while closing
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
    /// The GPU matching the main GPU's name; otherwise a discrete (NVIDIA/AMD) GPU, then Intel.
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

    /// <summary>The GPU's "GPU Core" temperature sensor.</summary>
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
    /// "CPU Package" (Intel) or "Tctl/Tdie" (AMD) first; otherwise the Core sensor with the highest value.
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
    /// Without the driver some CPUs (e.g. AMD Tctl/Tdie) report 0 instead of null, so unrealistic values are dropped.
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
