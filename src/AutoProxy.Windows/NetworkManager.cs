using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Windows;

public class NativeNetworkMonitor : INetworkMonitor
{
    private static readonly string[] VirtualNameKeywords =
    {
        "loopback", "virtual", "vethernet", "hyper-v", "hyperv", "docker", "wsl",
        "vbox", "virtualbox", "vmware", "vmnet", "isatap", "teredo", "bluetooth",
        "tap", "tun", "zerotier", "tailscale",
    };

    private readonly ILogService _log;
    private readonly WifiSsidReader _wifi = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _enumerationLock = new(1, 1);

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _started;

    private NetworkInterface[] _cachedInterfaces = Array.Empty<NetworkInterface>();
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan EnumerationCacheTtl = TimeSpan.FromSeconds(3);

    public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;
    public event EventHandler? NetworkUnavailable;

    public NativeNetworkMonitor(ILogService log) => _log = log;

    public async Task<CurrentNetwork?> GetCurrentNetworkAsync(CancellationToken cancellationToken)
    {
        var primary = await GetPrimaryInterfaceAsync(cancellationToken);
        if (primary is null) return null;

        return new CurrentNetwork
        {
            Identity = BuildIdentity(primary),
            HasActiveRoute = HasGateway(primary),
        };
    }

    public string GetQuickSignature()
    {
        var sb = new StringBuilder();
        foreach (var iface in GetInterfaces())
        {
            var address = FirstIpv4(iface);
            if (address is not null)
                sb.Append(ClassifyKind(iface)).Append(':').Append(address).Append('|');
        }

        sb.Append("route:").Append(GetPrimaryByRoute() is { } r ? FirstIpv4(r) : (object?)null).Append('|');
        foreach (var dns in DnsServers().Take(4))
            sb.Append("dns:").Append(dns).Append('|');

        return ShortHash(sb.ToString());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        _log.Info("Network", "Network monitor started.",
            "Listening for Wi-Fi/Ethernet address and availability changes.");

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => NegativeWatchLoopAsync(_loopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;

        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;

        lock (_sync) _debounceCts?.Cancel();
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch (OperationCanceledException) { }
        }

        _wifi.Dispose();
    }

    private void OnAddressChanged(object? sender, EventArgs e) => RaiseDelayed(isAvailabilityChange: false);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        RaiseDelayed(isAvailabilityChange: true);

    private void RaiseDelayed(bool isAvailabilityChange)
    {
        if (!_started) return;

        lock (_sync)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            var token = _debounceCts.Token;
            InvalidateCaches();
            _ = Task.Run(() => RaiseAfterDebounceAsync(token, isAvailabilityChange));
        }
    }

    private async Task RaiseAfterDebounceAsync(CancellationToken token, bool isAvailabilityChange)
    {
        try
        {
            await Task.Delay(1200, token);
            if (token.IsCancellationRequested) return;

            var current = await GetCurrentNetworkAsync(token);
            var signature = GetQuickSignature();

            if (current is null)
            {
                _log.Warning("Network", "No active network interface — connection is offline.");
                NetworkUnavailable?.Invoke(this, EventArgs.Empty);
                NetworkChanged?.Invoke(this, new NetworkChangedEventArgs
                {
                    NewSignature = signature,
                    IsAvailabilityChange = isAvailabilityChange,
                });
                return;
            }

            _log.Info("Network", "Network configuration changed.",
                $"Signature: {signature} — now on {current.Identity.DisplayName}.");
            NetworkChanged?.Invoke(this, new NetworkChangedEventArgs
            {
                NewSignature = signature,
                IsAvailabilityChange = isAvailabilityChange,
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warning("Network", "Could not process network change.", ex.Message);
        }
    }

    /// <summary>
    /// Negative-watch loop: confirms no identity drift while no events fire.
    /// Cheap (hash only) and decoupled from expensive probing.
    /// </summary>
    private async Task NegativeWatchLoopAsync(CancellationToken token)
    {
        try
        {
            var previous = GetQuickSignature();
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), token); }
                catch (OperationCanceledException) { break; }

                if (!_started) break;

                var current = GetQuickSignature();
                if (!string.Equals(previous, current))
                {
                    _log.Info("Network", "Network identity drifted without a Windows event.");
                    previous = current;
                    RaiseDelayed(isAvailabilityChange: false);
                }
            }
        }
        catch
        {
        }
    }

    // ---------- interface enumeration ----------

    private IEnumerable<NetworkInterface> GetInterfaces()
    {
        lock (_sync)
        {
            if (DateTime.UtcNow - _cachedAt < EnumerationCacheTtl && _cachedInterfaces.Length > 0)
                return _cachedInterfaces;
        }

        NetworkInterface[] result;
        try
        {
            result = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && IsPhysical(i))
                .ToArray();
        }
        catch
        {
            result = Array.Empty<NetworkInterface>();
        }

        lock (_sync)
        {
            _cachedInterfaces = result;
            _cachedAt = DateTime.UtcNow;
        }

        return result;
    }

    private void InvalidateCaches()
    {
        lock (_sync)
        {
            _cachedInterfaces = Array.Empty<NetworkInterface>();
            _cachedAt = DateTime.MinValue;
        }

        _wifi.Invalidate();
    }

    private async Task<NetworkInterface?> GetPrimaryInterfaceAsync(CancellationToken cancellationToken)
    {
        var all = GetInterfaces().ToList();
        if (all.Count == 0) return null;

        await _enumerationLock.WaitAsync(cancellationToken);
        try
        {
            var byRoute = GetPrimaryByRoute();
            if (byRoute is not null) return byRoute;

            return all.FirstOrDefault(i => IsEthernet(i) && HasGateway(i))
                   ?? all.FirstOrDefault(IsWifi)
                   ?? all.FirstOrDefault();
        }
        finally
        {
            _enumerationLock.Release();
        }
    }

    private NetworkInterface? GetPrimaryByRoute()
    {
        try
        {
            var index = BestInterfaceIndex(0);
            if (index is null) return null;

            foreach (var iface in GetInterfaces())
            {
                if (iface.GetIPProperties().GetIPv4Properties().Index == index.Value)
                    return iface;
            }
        }
        catch
        {
        }

        return null;
    }

    // ---------- identity ----------

    private NetworkIdentity BuildIdentity(NetworkInterface iface)
    {
        var kind = ClassifyKind(iface);
        var gateway = PrimaryGateway(iface);
        var ssid = kind == "wifi" ? _wifi.GetCurrentSsid(iface.Id) : null;
        var address = FirstIpv4(iface)?.ToString() ?? "";
        var subnet = TakeSubnet(address);

        string raw;
        string display;

        if (kind == "wifi")
        {
            if (string.IsNullOrEmpty(ssid))
            {
                // SSID unavailable (transient, hidden network, or unsupported
                // driver): fall back to gateway+subnet so different locations
                // still hash distinctly. When the SSID becomes readable again
                // the signature changes and re-detection produces the full ID.
                raw = $"wifi|{iface.Id}|g={gateway ?? ""}|{subnet}";
                display = string.IsNullOrEmpty(gateway) ? "Wi-Fi" : $"Wi-Fi ({gateway})";
            }
            else
            {
                raw = $"wifi|{iface.Id}|{ssid}";
                display = ssid;
            }
        }
        else
        {
            // Ethernet: the gateway lease can change via DHCP even on the same
            // network, so it must NOT participate in the identity hash. Keep the
            // readable display using the gateway for humans only.
            raw = $"eth|{iface.Id}|{subnet}";
            display = string.IsNullOrEmpty(gateway) ? "Ethernet" : $"Ethernet ({gateway})";
        }

        _log.Verbose("Network", "Network identity built.",
            $"{display} — ID {ShortHash(raw)} (kind={kind}, gateway={gateway ?? "-"}, subnet={subnet})");

        return new NetworkIdentity
        {
            Identifier = ShortHash(raw),
            DisplayName = display,
            Ssid = ssid,
            Gateway = gateway,
            Subnet = subnet,
            InterfaceType = kind,
        };
    }

    private static bool HasGateway(NetworkInterface iface) =>
        PrimaryGateway(iface) is not null;

    private static string? PrimaryGateway(NetworkInterface iface) =>
        iface.GetIPProperties().GatewayAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                 !a.Address.Equals(System.Net.IPAddress.Any))
            ?.Address.ToString();

    private static System.Net.IPAddress? FirstIpv4(NetworkInterface iface) =>
        iface.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address;

    private static string TakeSubnet(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 ? string.Join(".", parts.Take(3)) : ip;
    }

    private static bool IsPhysical(NetworkInterface iface)
    {
        var name = (iface.Name + " " + iface.Description).ToLowerInvariant();
        return !VirtualNameKeywords.Any(k => name.Contains(k)) &&
               iface.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or
                   NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown);
    }

    private static bool IsWifi(NetworkInterface iface) =>
        iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

    private static bool IsEthernet(NetworkInterface iface) =>
        iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet;

    private static string ClassifyKind(NetworkInterface iface) =>
        IsWifi(iface) ? "wifi" : "ethernet";

    private static IEnumerable<string> DnsServers()
    {
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var dns in iface.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetwork)
                        servers.Add(dns.ToString());
                }
            }
        }
        catch
        {
        }

        return servers;
    }

    private static string ShortHash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];

    private static int? BestInterfaceIndex(uint destinationAddress)
    {
        if (GetBestInterface(destinationAddress, out var index) == 0 && index != uint.MaxValue)
            return (int)index;
        return null;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint destAddr, out uint bestIfIndex);
}

/// <summary>Queries the current Wi-Fi SSID of an adapter via the Native Wifi API.</summary>
internal sealed class WifiSsidReader : IDisposable
{
    private static IntPtr? _wlanHandle;
    private static readonly object HandleLock = new();

    private string _cachedSsid = string.Empty;
    private DateTime _cachedAt = DateTime.MinValue;

    public string GetCurrentSsid(string? interfaceGuid)
    {
        if (DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(4))
            return _cachedSsid;

        var ssid = EnumerateSsid(interfaceGuid);
        if (ssid is not null)
        {
            _cachedSsid = ssid;
            _cachedAt = DateTime.UtcNow;
        }

        return ssid ?? _cachedSsid;
    }

    public void Invalidate()
    {
        _cachedSsid = string.Empty;
        _cachedAt = DateTime.MinValue;
    }

    private static string? EnumerateSsid(string? interfaceGuid)
    {
        var handle = GetHandle();
        if (handle == IntPtr.Zero) return null;

        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var listPtr) != 0)
            return null;

        try
        {
            var count = Marshal.ReadInt32(listPtr);
            var matchGuid = Guid.TryParse(interfaceGuid, out var parsed) ? parsed : (Guid?)null;
            var entrySize = Marshal.SizeOf<WlanInterfaceInfo>();

            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WlanInterfaceInfo>(listPtr + 8 + i * entrySize);

                if (matchGuid is not null && entry.InterfaceGuid != matchGuid)
                    continue;

                var ssid = QuerySsid(handle, entry.InterfaceGuid);
                if (ssid is not null) return ssid;
                if (matchGuid is not null) return null;
            }
        }
        finally
        {
            WlanFreeMemory(listPtr);
        }

        return null;
    }

    private static string? QuerySsid(IntPtr handle, Guid guid)
    {
        // wlan_intf_opcode_current_connection = 7
        var dataSize = 0u;
        if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, ref dataSize,
                out var data, out _) != 0 || data == IntPtr.Zero)
            return null;

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES layout:
            //   0:  WLAN_CONNECTION_MODE (int, 4 bytes)
            //   4:  WLAN_CONNECTION_PROFILE wlanProfileName (WCHAR[256])
            //   516: WLAN_ASSOCIATION_ATTRIBUTES with DOT11_SSID at its start.
            const int associationOffset = 4 + 256 * 2;
            var length = Marshal.ReadInt32(data, associationOffset);
            if (length is <= 0 or > 32) return null;

            var bytes = new byte[length];
            Marshal.Copy(data + associationOffset + 4, bytes, 0, length);
            var ssid = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;
        public uint IsState;
    }

    private static IntPtr GetHandle()
    {
        lock (HandleLock)
        {
            if (_wlanHandle is not null) return _wlanHandle.Value;

            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) == 0)
            {
                _wlanHandle = handle;
                return handle;
            }

            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        lock (HandleLock)
        {
            if (_wlanHandle is not null)
            {
                WlanCloseHandle(_wlanHandle.Value, IntPtr.Zero);
                _wlanHandle = null;
            }
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved,
        out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid,
        uint opCode, IntPtr reserved, ref uint dataSize, out IntPtr data,
        out uint wlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
}