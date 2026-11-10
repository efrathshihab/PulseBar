using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace PulseBar.Services;

/// <summary>
/// আসল internet adapter গুলোর (Up, loopback/tunnel না, default gateway আছে)
/// মোট download/upload speed বের করে। Adapter list শুধু network বদলালে নতুন করে পড়ে।
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly Stopwatch _clock = new();

    private NetworkInterface[] _adapters = [];
    private ulong[] _prevReceived = [];
    private ulong[] _prevSent = [];
    private bool _hasBaseline;
    private volatile bool _adaptersDirty = true;

    public NetworkMonitor()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>
    /// আগের ডাকের পর থেকে গড় speed (bytes/sec)। প্রথম ডাকে বা adapter বদলের পরে 0।
    /// পড়তে না পারলে false।
    /// </summary>
    public bool TryRead(out ulong downloadPerSec, out ulong uploadPerSec)
    {
        downloadPerSec = 0;
        uploadPerSec = 0;

        try
        {
            if (_adaptersDirty)
            {
                _adaptersDirty = false;
                RefreshAdapters();
            }

            double seconds = _clock.Elapsed.TotalSeconds;
            _clock.Restart();

            ulong down = 0;
            ulong up = 0;

            for (int i = 0; i < _adapters.Length; i++)
            {
                ulong received;
                ulong sent;
                try
                {
                    var stats = _adapters[i].GetIPStatistics();
                    received = (ulong)stats.BytesReceived;
                    sent = (ulong)stats.BytesSent;
                }
                catch
                {
                    // Adapter হঠাৎ চলে গেলে পরের network event এ list নতুন হবে
                    continue;
                }

                if (_hasBaseline)
                {
                    // Counter reset হলে নতুন মান ছোট হয়, তখন 0 ধরা হবে
                    down += received >= _prevReceived[i] ? received - _prevReceived[i] : 0;
                    up += sent >= _prevSent[i] ? sent - _prevSent[i] : 0;
                }

                _prevReceived[i] = received;
                _prevSent[i] = sent;
            }

            if (_hasBaseline && seconds > 0)
            {
                downloadPerSec = (ulong)(down / seconds);
                uploadPerSec = (ulong)(up / seconds);
            }

            _hasBaseline = true;
            return true;
        }
        catch
        {
            _hasBaseline = false;
            return false;
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _adaptersDirty = true;
    }

    private void RefreshAdapters()
    {
        var selected = new List<NetworkInterface>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || !HasDefaultGateway(nic))
                {
                    continue;
                }

                selected.Add(nic);
            }
            catch
            {
                // এই adapter বাদ
            }
        }

        _adapters = [.. selected];
        _prevReceived = new ulong[_adapters.Length];
        _prevSent = new ulong[_adapters.Length];
        _hasBaseline = false;
    }

    private static bool HasDefaultGateway(NetworkInterface nic)
    {
        foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
        {
            var address = gateway.Address;
            if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }
        }

        return false;
    }
}
