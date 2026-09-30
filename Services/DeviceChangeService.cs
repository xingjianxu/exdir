using System;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <inheritdoc cref="IDeviceChangeService" />
public sealed class DeviceChangeService : IDeviceChangeService, IDisposable
{
    private readonly VolumeChangeWatcher _watcher = new();

    public DeviceChangeService()
        => _watcher.VolumesChanged += (_, _) => VolumesChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? VolumesChanged;

    public void Attach(IntPtr windowHandle) => _watcher.Attach(windowHandle);

    public void Detach() => _watcher.Dispose();

    public void Dispose() => _watcher.Dispose();
}
