using AutoProxy.Core.Models;

namespace AutoProxy.Core.Services;

public class AppStateService
{
    private readonly object _sync = new();

    public AppMode Mode { get; private set; } = AppMode.Automatic;
    public ConnectionState State { get; private set; } = ConnectionState.Detecting;
    public DetectionResult? LastResult { get; private set; }
    public DateTime LastCheck { get; private set; } = DateTime.MinValue;

    private ProxyProfile? _manualProxy;

    public event Action<AppStateService>? Changed;
    public event Action<DetectionResult>? DetectionCompleted;

    public ProxyProfile? ActiveProxy
    {
        get
        {
            lock (_sync)
            {
                if (Mode == AppMode.ManualProxy) return _manualProxy;
                if (Mode == AppMode.Automatic) return LastResult?.SelectedProxy;
                return null;
            }
        }
    }

    public string StatusText => ActiveProxy is not null && State == ConnectionState.Proxy
        ? $"Proxy active: {ActiveProxy.Name}"
        : Describe.State(State);

    public void SetMode(AppMode mode, ProxyProfile? manualProxy = null)
    {
        lock (_sync)
        {
            Mode = mode;
            if (mode == AppMode.ManualProxy) _manualProxy = manualProxy;
            State = ConnectionState.Detecting;
        }

        RaiseChanged();
    }

    public void BeginDetecting()
    {
        lock (_sync) State = ConnectionState.Detecting;
        RaiseChanged();
    }

    public void CommitResult(DetectionResult result)
    {
        lock (_sync)
        {
            LastResult = result;
            State = result.State;
            LastCheck = DateTime.Now;
        }

        RaiseChanged();
        DetectionCompleted?.Invoke(result);
    }

    public void ApplyError(string detail)
    {
        lock (_sync)
        {
            State = ConnectionState.Error;
            LastCheck = DateTime.Now;
        }

        RaiseChanged();
    }

    public bool IsAutomatic => Mode == AppMode.Automatic;

    private void RaiseChanged() => Changed?.Invoke(this);
}

public static class Describe
{
    public static string State(ConnectionState state) => state switch
    {
        ConnectionState.Detecting => "Detecting connection…",
        ConnectionState.Direct => "Direct connection active",
        ConnectionState.Proxy => "Proxy active",
        ConnectionState.Offline => "Network unavailable",
        ConnectionState.CaptivePortal => "Captive portal detected",
        ConnectionState.Error => "Error",
        _ => state.ToString(),
    };
}