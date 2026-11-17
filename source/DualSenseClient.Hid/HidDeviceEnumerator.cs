using System.Runtime.InteropServices;
using System.Text;
using DualSenseClient.Logging;
using SDL;

namespace DualSenseClient.Hid;

/// <summary>
/// Enumerates HID devices and opens them via SDL3.
/// </summary>
public interface IHidDeviceEnumerator : IDisposable
{
    /// <summary>
    /// Enumerates all HID devices, optionally filtered by VID/PID.
    /// </summary>
    IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null);

    /// <summary>
    /// Enumerates HID devices matching any of the given vendor/product ID pairs.
    /// </summary>
    IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> deviceIds);

    /// <summary>
    /// Enumerates all HID devices matching the given VID/PID, including devices hidden
    /// by <see cref="ExcludeDevice"/>. Used to rediscover paths that were excluded by a
    /// previous virtual controller teardown: USB/IP reattaches at the same bus/port, so
    /// a re-created virtual device can reuse the exact devnode path of its removed
    /// predecessor.
    /// </summary>
    IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null);

    /// <summary>
    /// Opens a HID device by its platform path.
    /// </summary>
    IHidDevice OpenDevice(string path);

    /// <summary>
    /// Starts a background watcher that polls <c>SDL_hid_device_change_count</c>
    /// at the given interval and raises <see cref="DeviceConnected"/> /
    /// <see cref="DeviceDisconnected"/> when the device list changes.
    /// </summary>
    void StartWatching(int intervalMs = 1000);

    /// <summary>
    /// Stops the background watcher.
    /// </summary>
    void StopWatching();

    /// <summary>
    /// Excludes the device at the given path from all future enumeration results,
    /// so devices created by this application (e.g. virtual controllers) are not
    /// seen as real hardware.
    /// </summary>
    /// <param name="path">The device path to exclude.</param>
    void ExcludeDevice(string path);

    /// <summary>
    /// Removes a previously excluded device path, making the device appear in
    /// enumeration results again.
    /// </summary>
    /// <param name="path">The device path to unexclude.</param>
    void RemoveExcludedDevice(string path);

    /// <summary>
    /// Raised when a HID device connects.
    /// </summary>
    event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;

    /// <summary>
    /// Raised when a HID device disconnects.
    /// </summary>
    event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected;
}

/// <summary>
/// SDL3-backed HID device enumerator.
/// </summary>
public class HidDeviceEnumerator : IHidDeviceEnumerator
{
    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("HidDeviceEnumerator");

    /// <summary>
    /// Check to see if SDL Hid is initialized
    /// </summary>
    private int _initialized;

    /// <inheritdoc/>
    public event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;

    /// <inheritdoc/>
    public event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected;

    /// <summary>
    /// The background polling timer, or <c>null</c> while not watching.
    /// </summary>
    private Timer? _watcher;

    /// <summary>
    /// The last observed set of devices, used to detect connections and disconnections.
    /// </summary>
    private List<IHidDeviceInfo>? _previousSnapshot;

    /// <summary>
    /// Devices seen on a poll but not yet reported as connected, keyed by path.
    /// A device is only reported after it is observed on a second consecutive poll,
    /// so devices the application itself created and then excluded (virtual
    /// controllers) are never surfaced as phantom connections.
    /// </summary>
    private readonly Dictionary<string, IHidDeviceInfo> _pendingConnected = new Dictionary<string, IHidDeviceInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronizes access to the device watcher state.
    /// </summary>
    private readonly Lock _watchLock = new Lock();

    /// <summary>
    /// Keeps one immutable device-info object for each continuously present endpoint.
    /// Scanner disconnect callbacks use that reference as the endpoint generation so a
    /// delayed notification from an earlier same-path connection cannot retire a new
    /// owner.
    /// </summary>
    private readonly Lock _connectionIdentityLock = new Lock();
    private readonly Dictionary<string, IHidDeviceInfo> _currentInfoByPath =
        new Dictionary<string, IHidDeviceInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronizes access to <see cref="_excludedPaths"/>.
    /// </summary>
    private readonly Lock _exclusionLock = new Lock();

    /// <summary>
    /// Device paths hidden from enumeration results.
    /// </summary>
    private readonly HashSet<string> _excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void ExcludeDevice(string path)
    {
        lock (_exclusionLock)
        {
            if (_excludedPaths.Add(path))
            {
                _log.Debug($"Excluding device from enumeration: {path}");
            }
        }
    }

    /// <inheritdoc/>
    public void RemoveExcludedDevice(string path)
    {
        lock (_exclusionLock)
        {
            if (_excludedPaths.Remove(path))
            {
                _log.Debug($"Device no longer excluded from enumeration: {path}");
            }
        }
    }

    /// <summary>
    /// Checks whether the given device path has been excluded from enumeration.
    /// </summary>
    private bool IsExcluded(string path)
    {
        lock (_exclusionLock)
        {
            return _excludedPaths.Contains(path);
        }
    }

    /// <inheritdoc/>
    public void StartWatching(int intervalMs = 1000)
    {
        lock (_watchLock)
        {
            if (_watcher != null)
            {
                return;
            }

            _log.Debug($"Starting device watcher (interval={intervalMs}ms)");
            _previousSnapshot = [.. Enumerate()];
            _watcher = new Timer(WatchTick, null, intervalMs, intervalMs);
        }
    }

    /// <inheritdoc/>
    public void StopWatching()
    {
        lock (_watchLock)
        {
            if (_watcher != null)
            {
                _log.Debug("Stopping device watcher");
                _watcher.Dispose();
                _watcher = null;
                _previousSnapshot = null;
                _pendingConnected.Clear();
            }
        }

        lock (_connectionIdentityLock)
        {
            _currentInfoByPath.Clear();
        }
    }

    /// <summary>
    /// Timer callback. Re-enumerates active devices and fires <see cref="DeviceConnected"/>
    /// or <see cref="DeviceDisconnected"/> when the set of reachable devices changes.
    /// Device paths are trusted at face value; a disconnect is reported only when the
    /// native enumeration removes the path. Newly seen devices are only reported as
    /// connected once they have been observed on two consecutive polls, so devices the
    /// app itself created and excluded (virtual controllers) never fire
    /// <see cref="DeviceConnected"/>.
    /// </summary>
    private void WatchTick(object? state)
    {
        List<IHidDeviceInfo> current = [.. Enumerate()];

        lock (_watchLock)
        {
            if (_previousSnapshot == null)
            {
                _previousSnapshot = current;
                return;
            }

            HashSet<string> prevPaths = new HashSet<string>(_previousSnapshot.Select(d => d.Path));
            HashSet<string> currPaths = new HashSet<string>(current.Select(d => d.Path));

            // Confirm devices pending from the previous poll that are still present;
            // devices that vanished are left for the disconnect pass below.
            foreach (KeyValuePair<string, IHidDeviceInfo> pair in _pendingConnected.ToList())
            {
                if (!currPaths.Contains(pair.Key))
                {
                    continue;
                }

                _pendingConnected.Remove(pair.Key);
                _log.Info($"Device connected: {pair.Value.ProductName} (VID=0x{pair.Value.VendorId:X4}, PID=0x{pair.Value.ProductId:X4})");
                try
                {
                    DeviceConnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Connected, pair.Value));
                }
                catch (Exception ex)
                {
                    _log.Warning($"DeviceConnected handler threw: {ex.Message}");
                }
            }

            // First observation of a device: remember it, but only report it after it
            // is seen again on the next poll, so devices the app itself created and
            // then excluded (virtual controllers) never surface as connections.
            foreach (IHidDeviceInfo device in current.Where(device => !prevPaths.Contains(device.Path)))
            {
                _log.Debug(
                    $"Device connected (awaiting confirmation): {device.ProductName} (VID=0x{device.VendorId:X4}, PID=0x{device.ProductId:X4}, path={device.Path})");
                _pendingConnected[device.Path] = device;
            }

            foreach (IHidDeviceInfo device in _previousSnapshot.Where(device => !currPaths.Contains(device.Path)))
            {
                if (_pendingConnected.Remove(device.Path))
                {
                    // Never confirmed, so no connection event was raised for it.
                    continue;
                }

                _log.Info($"Device disconnected: {device.ProductName} (VID=0x{device.VendorId:X4}, PID=0x{device.ProductId:X4})");
                try
                {
                    DeviceDisconnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Disconnected, device));
                }
                catch (Exception ex)
                {
                    _log.Warning($"DeviceDisconnected handler threw: {ex.Message}");
                }
            }

            _previousSnapshot = current;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null)
    {
        _log.Debug($"Enumerating devices (vendorId=0x{vendorId:X4}, productId=0x{productId:X4})");
        List<IHidDeviceInfo> all = NativeEnumerate(vendorId, productId);
        all.RemoveAll(device => IsExcluded(device.Path));
        List<IHidDeviceInfo> filtered = FilterConnected(all);
        return RetainConnectionIdentity(filtered, vendorId is null && productId is null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null)
    {
        _log.Debug($"Enumerating devices including excluded (vendorId=0x{vendorId:X4}, productId=0x{productId:X4})");
        return FilterConnected(NativeEnumerate(vendorId, productId));
    }

    /// <summary>
    /// Returns the devices currently present in SDL's HID enumeration.
    /// </summary>
    /// <remarks>
    /// HID enumeration is the ownership-safe liveness source for Bluetooth devices.
    /// Opening a second handle and calling <c>SDL_hid_read_timeout</c> here is not a
    /// harmless probe: it races the controller's single input reader and a timed read
    /// returning zero means "no report before the timeout", not "the devnode vanished".
    /// The watcher therefore reports a disconnect only after the OS enumeration removes
    /// the path, while the controller's own blocking read handles link/read failures.
    /// </remarks>
    private List<IHidDeviceInfo> FilterConnected(List<IHidDeviceInfo> all)
    {
        _log.Debug($"Enumerate returned {all.Count} device(s) (liveness uses native path presence)");
        return all;
    }

    /// <summary>
    /// Reuses metadata objects while an endpoint remains continuously present and
    /// creates a new object after an observed disappearance or identity change.
    /// </summary>
    private IReadOnlyList<IHidDeviceInfo> RetainConnectionIdentity(
        List<IHidDeviceInfo> current,
        bool pruneMissingPaths)
    {
        lock (_connectionIdentityLock)
        {
            if (pruneMissingPaths)
            {
                HashSet<string> presentPaths = new HashSet<string>(
                    current.Select(device => device.Path), StringComparer.OrdinalIgnoreCase);
                foreach (string oldPath in _currentInfoByPath.Keys.Where(path => !presentPaths.Contains(path)).ToArray())
                {
                    _currentInfoByPath.Remove(oldPath);
                }
            }

            List<IHidDeviceInfo> stable = new List<IHidDeviceInfo>(current.Count);
            foreach (IHidDeviceInfo device in current)
            {
                if (_currentInfoByPath.TryGetValue(device.Path, out IHidDeviceInfo? existing)
                    && existing.VendorId == device.VendorId
                    && existing.ProductId == device.ProductId
                    && existing.InterfaceNumber == device.InterfaceNumber
                    && existing.BusType == device.BusType)
                {
                    stable.Add(existing);
                    continue;
                }

                _currentInfoByPath[device.Path] = device;
                stable.Add(device);
            }

            return stable;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> deviceIds)
    {
        HashSet<(ushort, ushort)> filter = new HashSet<(ushort, ushort)>(deviceIds);
        _log.Debug($"Enumerating with {filter.Count} VID/PID filter(s)");

        if (filter.Count == 0)
        {
            _log.Debug("Filter list is empty, returning empty result");
            return [];
        }

        List<IHidDeviceInfo> result = Enumerate().Where(d => filter.Contains((d.VendorId, d.ProductId))).ToList();
        _log.Debug($"Filtered enumerate returned {result.Count} device(s)");
        return result;
    }

    /// <summary>
    /// Calls into SDL3 to enumerate HID devices matching the given VID/PID filter.
    /// </summary>
    /// <param name="vendorId">Optional USB vendor ID to filter by.</param>
    /// <param name="productId">Optional USB product ID to filter by.</param>
    /// <returns>A list of matching <see cref="IHidDeviceInfo"/> instances.</returns>
    private List<IHidDeviceInfo> NativeEnumerate(ushort? vendorId, ushort? productId)
    {
        EnsureInitialized();

        List<IHidDeviceInfo> result = new List<IHidDeviceInfo>();

        unsafe
        {
            SDL_hid_device_info* devices = SDL3.SDL_hid_enumerate(vendorId ?? 0, productId ?? 0);

            int count = 0;
            for (SDL_hid_device_info* cur = devices; cur != null; cur = cur->next)
            {
                string path = cur->path != null ? Utf8ToString(cur->path) : string.Empty;
                string name = cur->product_string != IntPtr.Zero
                    ? Marshal.PtrToStringUni(cur->product_string) ?? string.Empty
                    : string.Empty;

                HidUsageId usage = (HidUsageId)cur->usage;
                if (usage == HidUsageId.Unknown)
                {
                    _log.Trace(
                        $"  Skipped {name} (VID=0x{cur->vendor_id:X4}, PID=0x{cur->product_id:X4}, usage=0x{cur->usage:X4}) — not a gamepad or joystick");
                    continue;
                }

                count++;
                _log.Debug(
                    $"  [{count}] {name} (VID=0x{cur->vendor_id:X4}, PID=0x{cur->product_id:X4}, bus={cur->bus_type}, usagePage=0x{cur->usage_page:X4}, usage=0x{cur->usage:X4}, path={path})");

                result.Add(new HidDeviceInfo
                {
                    Path = path,
                    VendorId = cur->vendor_id,
                    ProductId = cur->product_id,
                    ProductName = name,
                    Manufacturer = cur->manufacturer_string != IntPtr.Zero
                        ? Marshal.PtrToStringUni(cur->manufacturer_string) ?? string.Empty
                        : string.Empty,
                    InterfaceNumber = cur->interface_number,
                    UsagePage = cur->usage_page,
                    Usage = usage,
                    BusType = cur->bus_type switch
                    {
                        SDL_hid_bus_type.SDL_HID_API_BUS_USB => ConnectionType.Usb,
                        SDL_hid_bus_type.SDL_HID_API_BUS_BLUETOOTH => ConnectionType.Bluetooth,
                        _ => ConnectionType.Unknown
                    }
                });
            }

            _log.Debug($"SDL_hid_enumerate returned {count} device(s)");
            SDL3.SDL_hid_free_enumeration(devices);
        }

        return result;
    }

    /// <summary>
    /// Ensures the SDL HID subsystem has been initialized.
    /// Safe to call multiple times; only the first call performs initialization.
    /// </summary>
    /// <exception cref="HidException">Thrown when SDL_hid_init fails.</exception>
    private void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _log.Debug("Initializing SDL HID subsystem");
            if (SDL3.SDL_hid_init() != 0)
            {
                _log.Error("SDL_hid_init failed");
                throw new HidException("SDL_hid_init failed");
            }
        }
    }

    /// <summary>
    /// Converts a null-terminated UTF-8 byte pointer into a managed <see cref="string"/>.
    /// </summary>
    /// <param name="ptr">Pointer to a null-terminated UTF-8 string, or <c>null</c>.</param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> if the pointer is <c>null</c>.</returns>
    private static unsafe string Utf8ToString(byte* ptr)
    {
        if (ptr == null)
        {
            return string.Empty;
        }

        int len = 0;
        while (ptr[len] != 0)
        {
            len++;
        }

        return len > 0 ? Encoding.UTF8.GetString(ptr, len) : string.Empty;
    }

    /// <inheritdoc/>
    public IHidDevice OpenDevice(string path)
    {
        EnsureInitialized();
        _log.Debug($"Opening HID device '{path}'");
        return new HidDevice(path);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        StopWatching();

        if (Interlocked.Exchange(ref _initialized, 0) == 1)
        {
            _log.Debug("Shutting down SDL HID subsystem");
            SDL3.SDL_hid_exit();
        }
    }
}
