using System;
using Microsoft.Win32;

namespace Nyx.Services;

/// <summary>
/// Listens for OS sleep/hibernation wake events and notifies subscribers
/// so they can restart sing-box (its TUN interface usually breaks on resume).
/// </summary>
public static class PowerService
{
    public static event EventHandler? SystemResumed;

    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public static void Stop()
    {
        if (!_started) return;
        _started = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Fires when the system wakes from sleep OR hibernation.
        if (e.Mode == PowerModes.Resume)
            SystemResumed?.Invoke(null, EventArgs.Empty);
    }

    private static void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Unlocking the workstation after lock — also a good moment to refresh.
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            SystemResumed?.Invoke(null, EventArgs.Empty);
    }
}
