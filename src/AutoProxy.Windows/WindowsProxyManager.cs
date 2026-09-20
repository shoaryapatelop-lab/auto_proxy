using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AutoProxy.Core.Abstractions;
using Microsoft.Win32;

namespace AutoProxy.Windows;

/// <summary>
/// Manages the Windows system proxy.
///
/// Scope of this implementation:
///  - <b>WinINet</b> (HKCU Internet Settings + Connections binary blobs +
///    InternetSetOption / WM_SETTINGCHANGE broadcast): affects most browsers
///    (Edge, Chrome, Firefox in "system" mode) and any app that reads the
///    WinINet API or environment from `Internet Settings`.
///  - <b>WinHTTP</b> (netsh winhttp set/reset): affects WinHTTP-based
///    services and command-line tools (e.g. some .NET / PowerShell system
///    clients). Separate from WinINet; many desktop apps do NOT read it.
///
/// Applications that do <i>not</i> use the system proxy (sandboxed UWP apps,
/// apps with hard-coded proxy settings, SSH, etc.) are not affected.
/// </summary>
public class WindowsProxyManager : IWindowsProxyManager
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const string ConnectionsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private const uint SmtoAbortIfHung = 0x0002;

    private readonly ILogService _log;
    private readonly string _snapshotPath;
    private readonly object _snapshotLock = new();
    private ProxySnapshot? _snapshot;

    public WindowsProxyManager(ILogService log)
    {
        _log = log;
        _snapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoProxy", "proxy_snapshot.json");
        _snapshot = LoadSnapshot();
    }

    public event EventHandler? WindowsProxyChanged;

    public ProxySettings GetCurrentSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            if (key is null) return new ProxySettings();

            var enabledValue = key.GetValue("ProxyEnable");
            var enabled = enabledValue is int i && i != 0;
            var server = key.GetValue("ProxyServer") as string;
            var bypass = key.GetValue("ProxyOverride") as string;
            var autoConfigUrl = key.GetValue("AutoConfigURL") as string;

            return new ProxySettings
            {
                Enabled = enabled,
                Server = string.IsNullOrWhiteSpace(server) ? null : server,
                BypassList = string.IsNullOrWhiteSpace(bypass) ? null : bypass,
                AutoConfigUrl = string.IsNullOrWhiteSpace(autoConfigUrl) ? null : autoConfigUrl,
            };
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy", "Could not read current Windows proxy settings.", ex.Message);
            return new ProxySettings();
        }
    }

    public ProxySettings? GetExpectedSettings()
    {
        lock (_snapshotLock)
        {
            return _snapshot?.Applied?.Clone();
        }
    }

    public bool EnableProxy(string host, int port, string? bypassList = null)
    {
        // Defence in depth: the host/port flows into the registry, netsh
        // arguments and connection blobs. Reject anything that is not a valid
        // hostname/IP (also blocks quote/&amp;/space-based argument injection).
        if (!AutoProxy.Core.Network.ProxyValidator.IsValidHost(host) ||
            !AutoProxy.Core.Network.ProxyValidator.IsValidPort(port))
        {
            _log.Error("Proxy", "Refusing to apply an invalid proxy address.",
                $"host={AutoProxy.Core.Network.ProxyValidator.SanitizeForLog(host ?? string.Empty)}, port={port}");
            return false;
        }

        SaveSnapshot(force: false);
        var server = AutoProxy.Core.Network.ProxyValidator.FormatAddress(host.Trim(), port);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", server, RegistryValueKind.String);
            if (bypassList is not null)
                key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _log.Error("Proxy", "Failed to update the Windows proxy registry.",
                ex.Message);
            return false;
        }

        UpdateConnectionBlobs(enabled: true, server);
        NotifySystem();

        try
        {
            SetWinHttp(server);
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy",
                "Could not update WinHTTP proxy (may require elevation). " +
                "WinINet proxy is still configured.", ex.Message);
        }

        var applied = ProxySettings.FromProxy(host, port);
        applied.BypassList = bypassList;
        SetApplied(applied);
        _log.Info("Proxy", "Windows proxy applied.",
            $"WinINet = {server}; WinHTTP = {server}");
        return true;
    }

    public bool DisableProxy()
    {
        SaveSnapshot(force: false);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            if (key.GetValue("ProxyServer") is not null)
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _log.Error("Proxy", "Failed to disable the Windows proxy registry.", ex.Message);
            return false;
        }

        UpdateConnectionBlobs(enabled: false, null);
        NotifySystem();

        try
        {
            ResetWinHttp();
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy",
                "Could not reset WinHTTP proxy (may require elevation).", ex.Message);
        }

        SetApplied(ProxySettings.Disabled());
        _log.Info("Proxy", "Windows proxy disabled (Direct mode).");
        return true;
    }

    public void SaveSnapshot(bool force)
    {
        lock (_snapshotLock)
        {
            var snapshot = EnsureSnapshot();
            if (force || snapshot.Previous is null)
            {
                snapshot.Previous = GetCurrentSettings();
                PersistSnapshot(snapshot);
                _log.Verbose("Proxy", "Saved previous proxy settings.",
                    $"Previous: {snapshot.Previous}");
            }
        }
    }

    public bool RestorePreviousSettings()
    {
        bool restored;
        lock (_snapshotLock)
        {
            var snapshot = EnsureSnapshot();
            if (snapshot.Previous is null)
            {
                _log.Verbose("Proxy", "No previous proxy settings to restore.");
                return true;
            }

            try
            {
                if (snapshot.Previous.Enabled && !string.IsNullOrEmpty(snapshot.Previous.Server))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", snapshot.Previous.Server, RegistryValueKind.String);
                    if (!string.IsNullOrEmpty(snapshot.Previous.BypassList))
                        key.SetValue("ProxyOverride", snapshot.Previous.BypassList, RegistryValueKind.String);
                    else
                        key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
                    UpdateConnectionBlobs(enabled: true, snapshot.Previous.Server);
                    try { SetWinHttp(snapshot.Previous.Server); } catch { }
                }
                else
                {
                    DisableProxyWithoutSideEffects();
                    using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
                    if (!string.IsNullOrEmpty(snapshot.Previous.BypassList))
                        key.SetValue("ProxyOverride", snapshot.Previous.BypassList, RegistryValueKind.String);
                    else
                        key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
                }

                NotifySystem();
                snapshot.Applied = null;
                PersistSnapshot(snapshot);
                _log.Info("Proxy", "Previous Windows proxy settings restored.",
                    $"Previous: {snapshot.Previous}");
                restored = true;
            }
            catch (Exception ex)
            {
                _log.Error("Proxy", "Failed to restore previous proxy settings.", ex.Message);
                restored = false;
            }
        }

        if (restored)
            WindowsProxyChanged?.Invoke(this, EventArgs.Empty);
        return restored;
    }

    public void RestorePreviousSettingsOnStartup()
    {
        var shouldRestore = false;

        lock (_snapshotLock)
        {
            var snapshot = EnsureSnapshot();
            if (snapshot.Applied is null)
            {
                _log.Verbose("Proxy",
                    "Crash recovery: no applied state found on startup — nothing to restore.");
                return;
            }

            if (snapshot.Previous is null)
            {
                _log.Verbose("Proxy", "Crash recovery: snapshot incomplete — nothing to restore.");
                snapshot.Applied = null;
                PersistSnapshot(snapshot);
                return;
            }

            var current = GetCurrentSettings();
            shouldRestore = SettingsEqual(snapshot.Applied, current) &&
                            !SettingsEqual(snapshot.Applied, snapshot.Previous);

            if (!shouldRestore)
            {
                _log.Info("Proxy",
                    "Crash recovery: previous session did not leave the system proxy in a " +
                    "broken state.");
                snapshot.Applied = null;
                PersistSnapshot(snapshot);
            }
        }

        if (shouldRestore)
        {
            _log.Warning("Proxy",
                "Crash recovery: AutoProxy proxy settings still active after a restart — " +
                "restoring the previous system configuration.");
            RestorePreviousSettings();
        }
    }

    public string DescribeScope() =>
        "Affects: browsers and apps that use the Windows system proxy (WinINet), " +
        "plus the separate WinHTTP configuration used by some services and CLI tools. " +
        "Apps with hard-coded proxy settings or sandboxed UWP apps are NOT affected.";

    private void DisableProxyWithoutSideEffects()
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        if (key.GetValue("ProxyServer") is not null)
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        UpdateConnectionBlobs(enabled: false, null);
    }

    private ProxySnapshot EnsureSnapshot() => _snapshot ??= new ProxySnapshot();

    private void SetApplied(ProxySettings applied)
    {
        lock (_snapshotLock)
        {
            var snapshot = EnsureSnapshot();
            snapshot.Applied = applied;
            PersistSnapshot(snapshot);
        }
    }

    private void PersistSnapshot(ProxySnapshot snapshot)
    {
        _snapshot = snapshot;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_snapshotPath, json);
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy", "Could not persist the proxy snapshot.", ex.Message);
        }
    }

    private ProxySnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return null;
            return JsonSerializer.Deserialize<ProxySnapshot>(File.ReadAllText(_snapshotPath));
        }
        catch
        {
            return null;
        }
    }

    // ---------- WinINet binary connection blobs (Windows 10/11) ----------

    private void UpdateConnectionBlobs(bool enabled, string? server)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConnectionsKey, writable: true);
            if (key is null) return;

            foreach (var name in new[] { "DefaultConnectionSettings", "SavedLegacySettings" })
            {
                try
                {
                    var raw = key.GetValue(name) as byte[];
                    if (raw is null || raw.Length < 16) continue;

                    var data = new byte[raw.Length];
                    Array.Copy(raw, data, raw.Length);

                    data[8] = enabled ? (byte)3 : (byte)1;
                    var oldLength = BitConverter.ToInt32(data, 12);
                    var tailStart = 16 + oldLength;
                    var tail = tailStart <= data.Length
                        ? data[tailStart..]
                        : Array.Empty<byte>();

                    byte[] newValue;
                    if (enabled && server is not null)
                    {
                        var proxyBytes = Encoding.UTF8.GetBytes(server);
                        newValue = new byte[12 + 4 + proxyBytes.Length + tail.Length];
                        Array.Copy(data, 0, newValue, 0, 12);
                        BitConverter.GetBytes(proxyBytes.Length).CopyTo(newValue, 12);
                        proxyBytes.CopyTo(newValue, 16);
                        tail.CopyTo(newValue, 16 + proxyBytes.Length);
                    }
                    else
                    {
                        newValue = new byte[16 + tail.Length];
                        Array.Copy(data, 0, newValue, 0, 12);
                        BitConverter.GetBytes(0).CopyTo(newValue, 12);
                        tail.CopyTo(newValue, 16);
                    }

                    key.SetValue(name, newValue, RegistryValueKind.Binary);
                }
                catch (Exception ex)
                {
                    _log.Verbose("Proxy", $"Could not update {name} connection blob.",
                        ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy", "Could not update WinINet connection blobs.", ex.Message);
        }
    }

    private void NotifySystem()
    {
        try
        {
            var setOption = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);

            SendMessageTimeout(
                HWND_BROADCAST,
                0x001A, // WM_SETTINGCHANGE
                IntPtr.Zero,
                "Internet Settings",
                SmtoAbortIfHung,
                1000,
                out _);
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy", "Could not broadcast proxy change notification.", ex.Message);
        }
    }

    // ---------- WinHTTP (via netsh winhttp) ----------

    private static void SetWinHttp(string server)
    {
        RunNetsh($"winhttp set proxy \"{server}\"");
    }

    private static void ResetWinHttp()
    {
        RunNetsh("winhttp reset proxy");
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh")
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(8000);
    }

    // ---------- P/Invoke ----------

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet, int option, IntPtr buffer, int bufferLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr result);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    private static bool SettingsEqual(ProxySettings a, ProxySettings b) =>
        a.Enabled == b.Enabled &&
        string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase);

    private class ProxySnapshot
    {
        public ProxySettings? Previous { get; set; }
        public ProxySettings? Applied { get; set; }
    }
}