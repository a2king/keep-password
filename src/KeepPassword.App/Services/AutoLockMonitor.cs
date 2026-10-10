using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using KeepPassword.Core.Autofill;
using Microsoft.Win32;

namespace KeepPassword.App.Services;

public sealed class AutoLockMonitor : IDisposable
{
    private readonly Action _lock;
    private readonly DispatcherTimer _timer;
    private bool _started;
    private bool _locked;

    public AutoLockMonitor(Action @lock)
    {
        _lock = @lock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => CheckIdle();
    }

    public void Start()
    {
        if (_started || !OperatingSystem.IsWindows())
        {
            return;
        }

        _started = true;
        _locked = false;
        SubscribeWindowsEvents();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _timer.Stop();
        if (OperatingSystem.IsWindows())
        {
            UnsubscribeWindowsEvents();
        }
    }

    [SupportedOSPlatform("windows")]
    private void SubscribeWindowsEvents()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerMode;
    }

    [SupportedOSPlatform("windows")]
    private void UnsubscribeWindowsEvents()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerMode;
    }

    public void Dispose() => Stop();

    private void CheckIdle()
    {
        if (!_started)
        {
            return;
        }

        var idle = WindowsIdle.Read();
        if (SessionLockPolicy.ShouldLock(idle, workstationLocked: false, suspending: false))
        {
            DispatchLock();
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
        {
            DispatchLock();
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            DispatchLock();
        }
    }

    private void DispatchLock()
    {
        if (_locked)
        {
            return;
        }

        _locked = true;
        Dispatcher.UIThread.Post(_lock);
    }

    private static class WindowsIdle
    {
        public static TimeSpan Read()
        {
            if (!OperatingSystem.IsWindows())
            {
                return TimeSpan.Zero;
            }

            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var idle = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idle);
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
    }
}
