using System.Net.NetworkInformation;
using DlutTimetable.Application;
using Microsoft.Win32;

namespace DlutTimetable.Windows;

/// <summary>Owns static Windows event subscriptions; disposal prevents retaining an exited scheduler.</summary>
public sealed class WindowsSyncEvents : IDisposable
{
    private readonly DailySyncScheduler scheduler;
    private bool disposed;
    public WindowsSyncEvents(DailySyncScheduler scheduler)
    {
        this.scheduler = scheduler;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        try
        {
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.TimeChanged += OnClock;
        }
        catch
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
            SystemEvents.PowerModeChanged -= OnPower;
            throw;
        }
    }
    private void OnNetwork(object? sender, NetworkAvailabilityEventArgs e)
    { if (e.IsAvailable) _ = scheduler.SignalAsync(SyncTrigger.NetworkRestored); }
    private void OnPower(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) _ = scheduler.SignalAsync(SyncTrigger.Resumed); }
    private void OnClock(object? sender, EventArgs e) => _ = scheduler.SignalAsync(SyncTrigger.ClockChanged);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.TimeChanged -= OnClock;
    }
}
