using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// LibreHardwareMonitor দিয়ে CPU temperature পড়ে। Admin আর PawnIO driver লাগে;
/// না থাকলে শুরুতে একবার চেষ্টা করে থেমে যায় আর <see cref="Status"/> এ কারণ রাখে।
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // double? atomic না, তাই bits হিসেবে রাখা; NaN মানে null
    private long _celsiusBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private volatile TemperatureStatus _status = TemperatureStatus.Starting;

    public double? CurrentCelsius
    {
        get
        {
            double value = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _celsiusBits));
            return double.IsNaN(value) ? null : value;
        }
    }

    public TemperatureStatus Status => _status;

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
            computer = new Computer { IsCpuEnabled = true };
            computer.Open();

            var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            ISensor? sensor = null;

            if (cpu is not null)
            {
                Update(cpu);
                sensor = PickSensor(cpu);
            }

            if (ValidValue(sensor) is { } first)
            {
                Publish(first);
            }
            else
            {
                SetCelsius(null);
                _status = !isAdmin ? TemperatureStatus.NotAdmin
                        : !hasDriver ? TemperatureStatus.NoDriver
                        : TemperatureStatus.NoSensor;

                // Admin বা driver না থাকলে বারবার চেষ্টা করে লাভ নেই
                if (!isAdmin || !hasDriver || cpu is null)
                {
                    return;
                }
            }

            using var timer = new PeriodicTimer(UpdateInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Update(cpu!);

                if (ValidValue(sensor) is null)
                {
                    sensor = PickSensor(cpu!);
                }

                if (ValidValue(sensor) is { } value)
                {
                    Publish(value);
                }
                else
                {
                    SetCelsius(null);
                    _status = TemperatureStatus.NoSensor;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // স্বাভাবিক থামা
        }
        catch
        {
            SetCelsius(null);
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
        SetCelsius(value);
        _status = TemperatureStatus.Ok;
    }

    private void SetCelsius(double? value)
    {
        Interlocked.Exchange(ref _celsiusBits, BitConverter.DoubleToInt64Bits(value ?? double.NaN));
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
