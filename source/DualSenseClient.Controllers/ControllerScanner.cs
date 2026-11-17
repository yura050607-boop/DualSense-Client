using DualSenseClient.Controllers.Devices;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Hid;
using DualSenseClient.Logging;

namespace DualSenseClient.Controllers;

/// <summary>
/// Discovers and watches for known controllers (DualSense, etc.) using the HID device enumerator.
/// Provides both a one-shot <see cref="Scan"/> and a push-based event model via <see cref="StartWatching"/>.
/// </summary>
public interface IControllerScanner : IDisposable
{
    /// <summary>
    /// Performs a one-shot enumeration of currently connected controllers.
    /// Each unique endpoint is opened at most once; a healthy endpoint already tracked
    /// by this scanner reuses its existing <see cref="IControllerDevice"/> instance.
    /// Returned devices are owned by the caller.
    /// </summary>
    IReadOnlyList<IControllerDevice> Scan();

    /// <summary>
    /// Raised when a known controller connects.
    /// The event args carry a live, opened <see cref="IControllerDevice"/> the subscriber owns.
    /// </summary>
    event EventHandler<ControllerConnectionEventArgs>? ControllerConnected;

    /// <summary>
    /// Raised when a known controller disconnects.
    /// The event args carry the device info, type, and the previously opened controller
    /// when this scanner owns one for the same stable path.
    /// </summary>
    event EventHandler<ControllerConnectionEventArgs>? ControllerDisconnected;

    /// <summary>
    /// Starts a background timer that polls for controller connection changes.
    /// </summary>
    /// <param name="intervalMs">Polling interval in milliseconds (default 1000).</param>
    void StartWatching(int intervalMs = 1000);

    /// <summary>
    /// Stops the background watcher timer and unsubscribes from enumerator events.
    /// </summary>
    void StopWatching();
}

/// <summary>
/// Default implementation of <see cref="IControllerScanner"/>.
/// Filters devices through <see cref="ControllerFactory.KnownDeviceIds"/> so that
/// only recognized controllers are surfaced to consumers.
/// </summary>
public sealed class ControllerScanner : IControllerScanner
{
    private const int MaxAutomaticRecoveryAttempts = 3;
    private static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];

    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("ControllerScanner");

    /// <summary>
    /// The HID enumerator used to discover and open controller devices.
    /// </summary>
    private readonly IHidDeviceEnumerator _enumerator;

    /// <summary>
    /// Whether the scanner is currently watching for connection changes.
    /// </summary>
    private bool _watching;
    private bool _disposed;
    private int _watchGeneration;

    /// <summary>
    /// The opened controller currently owned for each stable HID path. Keeping the
    /// instance identity lets UI consumers reject a late disconnect for an older
    /// connection after the same path has already reconnected.
    /// </summary>
    private readonly Lock _controllerLock = new Lock();
    private readonly Dictionary<string, ControllerEntry> _controllersByPath =
        new Dictionary<string, ControllerEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An opening reservation belongs to one open operation and watch generation.
    /// Only that operation may release it, so late work cannot clear a newer path
    /// reservation after a watcher restart.
    /// </summary>
    private readonly Dictionary<string, OpeningReservation> _openingPaths =
        new Dictionary<string, OpeningReservation>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounded recovery work awaiting a healthy reader for its path.
    /// </summary>
    private readonly Dictionary<string, RecoveryState> _recoveriesByPath =
        new Dictionary<string, RecoveryState>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional factory seam used by lifecycle tests; production continues to use the
    /// canonical <see cref="ControllerFactory"/> mapping.
    /// </summary>
    private readonly Func<IHidDeviceInfo, IControllerDevice?>? _controllerFactory;

    private sealed class ControllerEntry(IControllerDevice controller, IHidDeviceInfo info, RecoveryState? recovery)
    {
        public IControllerDevice Controller { get; } = controller;
        public IHidDeviceInfo Info { get; } = info;
        public RecoveryState? Recovery { get; set; } = recovery;
        public bool Published { get; set; }
        public bool ReaderEndedBeforePublication { get; set; }
    }

    private sealed class OpeningReservation(int watchGeneration)
    {
        public int WatchGeneration { get; } = watchGeneration;
    }

    private sealed class RecoveryState(IHidDeviceInfo info)
    {
        public IHidDeviceInfo Info { get; set; } = info;
        public int Attempts { get; set; }
        public bool Scheduled { get; set; }
        // Identifies the watcher session whose retry task owns Scheduled.
        public int? ScheduledGeneration { get; set; }
        // True only while a failed wrapper's Dispose still owns the HID path.
        public bool Retiring { get; set; }
        // A failed one-shot open remains available for the next watcher startup.
        public bool KeepForWatcherStart { get; set; }
        public IHidDeviceInfo? PendingConnection { get; set; }
    }

    /// <summary>
    /// Creates a new <see cref="ControllerScanner"/> backed by the given HID enumerator.
    /// </summary>
    public ControllerScanner(IHidDeviceEnumerator enumerator,
        Func<IHidDeviceInfo, IControllerDevice?>? controllerFactory = null)
    {
        _enumerator = enumerator;
        _controllerFactory = controllerFactory;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IControllerDevice> Scan()
    {
        List<IControllerDevice> controllers = new List<IControllerDevice>();
        foreach (IHidDeviceInfo info in _enumerator.Enumerate(ControllerFactory.KnownDeviceIds))
        {
            ControllerEntry? existing = null;
            bool reserved;
            OpeningReservation? reservation = null;
            lock (_controllerLock)
            {
                if (_disposed)
                {
                    break;
                }

                if (_controllersByPath.TryGetValue(info.Path, out existing))
                {
                    reserved = false;
                    if (existing.Controller.IsConnected)
                    {
                        controllers.Add(existing.Controller);
                        continue;
                    }
                }
                else if (_recoveriesByPath.TryGetValue(info.Path, out RecoveryState? recovery))
                {
                    if (recovery.Retiring)
                    {
                        recovery.Info = info;
                        recovery.PendingConnection = info;
                    }

                    reserved = false;
                }
                else
                {
                    reservation = ReserveOpeningPathLocked(info.Path);
                    reserved = reservation is not null;
                }
            }

            if (existing is not null)
            {
                BeginReaderRecovery(existing);
                continue;
            }

            if (!reserved)
            {
                continue;
            }

            IControllerDevice? controller = TryCreate(info);
            if (controller is null)
            {
                ReleaseOpeningPath(info.Path, reservation!);
                continue;
            }

            if (TryRegisterOpenedController(info, controller, null, null, reservation!, out ControllerEntry? entry))
            {
                controllers.Add(entry!.Controller);
            }
        }

        _log.Info($"Scan found {controllers.Count} connected controller(s)");
        return controllers;
    }

    /// <inheritdoc/>
    public event EventHandler<ControllerConnectionEventArgs>? ControllerConnected;

    /// <inheritdoc/>
    public event EventHandler<ControllerConnectionEventArgs>? ControllerDisconnected;

    /// <inheritdoc/>
    public void StartWatching(int intervalMs = 1000)
    {
        int generation;
        lock (_controllerLock)
        {
            if (_watching || _disposed)
            {
                return;
            }

            _watching = true;
            generation = ++_watchGeneration;
            _openingPaths.Clear();
            _enumerator.DeviceConnected += OnDeviceConnected;
            _enumerator.DeviceDisconnected += OnDeviceDisconnected;
        }

        _log.Debug($"Starting controller watcher (interval={intervalMs}ms)");
        try
        {
            _enumerator.StartWatching(intervalMs);
        }
        catch
        {
            StopWatching();
            throw;
        }

        List<ControllerEntry> endedEntries = new List<ControllerEntry>();
        List<RecoveryState> pendingRecoveries;
        lock (_controllerLock)
        {
            foreach (ControllerEntry entry in _controllersByPath.Values)
            {
                entry.Published = true;
                if (entry.ReaderEndedBeforePublication || !entry.Controller.IsConnected)
                {
                    endedEntries.Add(entry);
                }
            }

            pendingRecoveries = _recoveriesByPath.Values.ToList();
            foreach (RecoveryState recovery in pendingRecoveries)
            {
                recovery.KeepForWatcherStart = false;
            }
        }

        foreach (ControllerEntry entry in endedEntries)
        {
            BeginReaderRecovery(entry);
        }

        foreach (RecoveryState recovery in pendingRecoveries)
        {
            ScheduleRecovery(recovery, generation);
        }
    }

    /// <inheritdoc/>
    public void StopWatching()
    {
        bool wasWatching;
        lock (_controllerLock)
        {
            wasWatching = _watching;
            _watching = false;
            _watchGeneration++;
            if (wasWatching)
            {
                _enumerator.DeviceConnected -= OnDeviceConnected;
                _enumerator.DeviceDisconnected -= OnDeviceDisconnected;
            }

            foreach (ControllerEntry entry in _controllersByPath.Values)
            {
                SetPhysicalDiagnosticsOwnership(entry.Controller, false);
                DetachReaderLifecycle(entry.Controller);
            }

            _controllersByPath.Clear();
            _openingPaths.Clear();
            foreach (RecoveryState recovery in _recoveriesByPath.Values)
            {
                if (recovery.Retiring)
                {
                    recovery.PendingConnection = null;
                }
            }

            foreach (string path in _recoveriesByPath
                .Where(pair => !pair.Value.Retiring)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _recoveriesByPath.Remove(path);
            }
        }

        if (wasWatching)
        {
            _log.Debug("Stopping controller watcher");
            _enumerator.StopWatching();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_controllerLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopWatching();
    }

    /// <summary>
    /// Tries to open the device and create a controller wrapper for it.
    /// A device that is enumerated but cannot be opened (e.g. it is still being
    /// re-enumerated by the OS, or momentarily grabbed by another process) is
    /// skipped instead of crashing the caller.
    /// Virtual controllers created by libVIIPER are also rejected: they present the
    /// same VID/PID as real hardware, so they are recognized by their pairing-info MAC
    /// (see <see cref="VirtualDeviceFilter"/>).
    /// </summary>
    private IControllerDevice? TryCreate(IHidDeviceInfo info)
    {
        try
        {
            IControllerDevice? controller = _controllerFactory is not null
                ? _controllerFactory(info)
                : ControllerFactory.Create(_enumerator, info);
            if (controller is DualSenseDevice device && VirtualDeviceFilter.IsKnownVirtualMac(device.PairingInfo?.ClientMac))
            {
                _log.Warning(
                    $"Ignoring virtual controller: {info.ProductName} (MAC={device.PairingInfo?.ClientMac}, path={info.Path})");
                device.Dispose();
                return null;
            }

            return controller;
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"Skipping device that could not be opened: {info.ProductName} (VID=0x{info.VendorId:X4}, PID=0x{info.ProductId:X4}, bus={info.BusType}) — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Handles <see cref="IHidDeviceEnumerator.DeviceConnected"/>.
    /// Tries to create a controller wrapper for the newly connected device;
    /// fires <see cref="ControllerConnected"/> only for recognized controllers.
    /// </summary>
    private void OnDeviceConnected(object? sender, DeviceConnectionEventArgs e)
    {
        if (ControllerFactory.GetType(e.Device) == ControllerType.Unknown)
        {
            return;
        }

        ControllerEntry? existing = null;
        int generation;
        OpeningReservation? reservation = null;
        lock (_controllerLock)
        {
            if (_disposed || !_watching)
            {
                return;
            }

            generation = _watchGeneration;
            if (_controllersByPath.TryGetValue(e.Device.Path, out existing))
            {
                if (existing.Controller.IsConnected)
                {
                    _log.Debug("Ignoring duplicate connection for an already healthy controller");
                    return;
                }
            }
            else if (_recoveriesByPath.TryGetValue(e.Device.Path, out RecoveryState? recovery))
            {
                recovery.Info = e.Device;
                if (recovery.Retiring)
                {
                    recovery.PendingConnection = e.Device;
                }

                return;
            }
            else if ((reservation = ReserveOpeningPathLocked(e.Device.Path)) is null)
            {
                return;
            }
        }

        if (existing is not null)
        {
            BeginReaderRecovery(existing);
            return;
        }

        IControllerDevice? controller = TryCreate(e.Device);
        if (controller is null)
        {
            ReleaseOpeningPath(e.Device.Path, reservation!);
            return;
        }

        if (TryRegisterOpenedController(e.Device, controller, null, generation, reservation!, out ControllerEntry? entry))
        {
            PublishConnected(entry!);
        }
    }

    /// <summary>
    /// Handles <see cref="IHidDeviceEnumerator.DeviceDisconnected"/>.
    /// Fires <see cref="ControllerDisconnected"/> only for recognized controllers;
    /// if this scanner tracked an opened wrapper for the endpoint, the event carries
    /// that exact retired instance so consumers can remove only its connection generation.
    /// </summary>
    private void OnDeviceDisconnected(object? sender, DeviceConnectionEventArgs e)
    {
        ControllerType type = ControllerFactory.GetType(e.Device);
        if (type == ControllerType.Unknown)
        {
            return;
        }

        ControllerEntry? entry;
        lock (_controllerLock)
        {
            if (_recoveriesByPath.ContainsKey(e.Device.Path)
                || !_controllersByPath.TryGetValue(e.Device.Path, out entry))
            {
                return;
            }

            if (!ReferenceEquals(entry.Info, e.Device))
            {
                _log.Debug("Ignoring a delayed disconnect for an older endpoint generation");
                return;
            }

            _controllersByPath.Remove(e.Device.Path);
            SetPhysicalDiagnosticsOwnership(entry.Controller, false);
            DetachReaderLifecycle(entry.Controller);
        }

        _log.Info($"Controller disconnected: {e.Device.ProductName} ({type})");
        RaiseDisconnected(entry);
    }

    private bool TryRegisterOpenedController(
        IHidDeviceInfo info,
        IControllerDevice controller,
        RecoveryState? recovery,
        int? expectedWatchGeneration,
        OpeningReservation reservation,
        out ControllerEntry? entry)
    {
        entry = null;
        AttachReaderLifecycle(controller);
        RecoveryState? failedStartRecovery = null;
        bool rejected;

        lock (_controllerLock)
        {
            bool ownsReservation = _openingPaths.TryGetValue(info.Path, out OpeningReservation? currentReservation)
                && ReferenceEquals(currentReservation, reservation);
            if (ownsReservation)
            {
                _openingPaths.Remove(info.Path);
            }

            bool sessionIsCurrent = reservation.WatchGeneration == _watchGeneration
                && (expectedWatchGeneration is null
                    || (_watching && _watchGeneration == expectedWatchGeneration.Value));
            rejected = _disposed || !ownsReservation || !sessionIsCurrent || _controllersByPath.ContainsKey(info.Path);
            if (!rejected && !controller.IsConnected)
            {
                failedStartRecovery = recovery
                    ?? (_recoveriesByPath.TryGetValue(info.Path, out RecoveryState? currentRecovery)
                        ? currentRecovery
                        : new RecoveryState(info));
                failedStartRecovery.Info = info;
                failedStartRecovery.Retiring = true;
                failedStartRecovery.KeepForWatcherStart |= recovery is null && !_watching;
                _recoveriesByPath[info.Path] = failedStartRecovery;
                rejected = true;
            }

            if (!rejected)
            {
                entry = new ControllerEntry(controller, info, recovery)
                {
                    // Scan() returns ownership to its caller before watching begins.
                    // StartWatching promotes those entries only after that initial
                    // result has been consumed by the application.
                    Published = expectedWatchGeneration is null && _watching
                };
                _controllersByPath.Add(info.Path, entry);
                SetPhysicalDiagnosticsOwnership(controller, true);
                if (recovery is not null)
                {
                    _recoveriesByPath.Remove(info.Path);
                    recovery.Scheduled = false;
                    recovery.ScheduledGeneration = null;
                }
            }
        }

        if (rejected)
        {
            SetPhysicalDiagnosticsOwnership(controller, false);
            DetachReaderLifecycle(controller);
            DisposeController(controller);
            if (failedStartRecovery is not null)
            {
                CompleteReaderRetirement(failedStartRecovery);
            }

            return false;
        }

        return true;
    }

    private void PublishConnected(ControllerEntry entry)
    {
        _log.Info($"Controller connected: {entry.Info.ProductName} ({entry.Controller.ControllerType}, bus={entry.Controller.ConnectionType})");
        try
        {
            ControllerConnected?.Invoke(this, new ControllerConnectionEventArgs(
                DeviceChangeType.Connected, entry.Info, entry.Controller.ControllerType, entry.Controller));
        }
        catch
        {
            _log.Warning("A controller-connected subscriber failed");
        }

        bool endedDuringPublication;
        lock (_controllerLock)
        {
            if (!_controllersByPath.TryGetValue(entry.Info.Path, out ControllerEntry? current)
                || !ReferenceEquals(current, entry))
            {
                return;
            }

            entry.Published = true;
            endedDuringPublication = entry.ReaderEndedBeforePublication || !entry.Controller.IsConnected;
        }

        if (endedDuringPublication)
        {
            BeginReaderRecovery(entry);
        }
    }

    private void OnInputReaderEndedUnexpectedly(object? sender, EventArgs e)
    {
        if (sender is not DualSenseDevice device)
        {
            return;
        }

        ControllerEntry? entry;
        lock (_controllerLock)
        {
            if (!_controllersByPath.TryGetValue(device.Info.Path, out entry)
                || !ReferenceEquals(entry.Controller, device))
            {
                return;
            }

            if (!entry.Published)
            {
                entry.ReaderEndedBeforePublication = true;
                return;
            }
        }

        BeginReaderRecovery(entry);
    }

    private void OnInputReportReceived(object? sender, InputReport report)
    {
        if (sender is not DualSenseDevice device)
        {
            return;
        }

        lock (_controllerLock)
        {
            if (_controllersByPath.TryGetValue(device.Info.Path, out ControllerEntry? entry)
                && ReferenceEquals(entry.Controller, device))
            {
                // A validated production report proves the replacement reader has
                // become healthy and starts a fresh bounded-recovery budget.
                entry.Recovery = null;
            }
        }
    }

    private void BeginReaderRecovery(ControllerEntry entry)
    {
        RecoveryState? recovery;
        bool notifyDisconnect;
        lock (_controllerLock)
        {
            if (!_controllersByPath.TryGetValue(entry.Info.Path, out ControllerEntry? current)
                || !ReferenceEquals(current, entry))
            {
                return;
            }

            _controllersByPath.Remove(entry.Info.Path);
            SetPhysicalDiagnosticsOwnership(entry.Controller, false);
            DetachReaderLifecycle(entry.Controller);
            notifyDisconnect = entry.Published;
            recovery = entry.Recovery ?? new RecoveryState(entry.Info);
            recovery.Info = entry.Info;
            recovery.Retiring = true;
            _recoveriesByPath[entry.Info.Path] = recovery;
        }

        _log.Warning("Controller input reader ended; scanner ownership is being retired");
        _ = Task.Run(async () =>
        {
            if (notifyDisconnect)
            {
                RaiseDisconnected(entry);
            }

            DisposeController(entry.Controller);
            CompleteReaderRetirement(recovery);
        });
    }

    private void CompleteReaderRetirement(RecoveryState recovery)
    {
        IHidDeviceInfo? pendingConnection = null;
        bool retryCurrentWatcher = false;
        bool reopenPendingConnection = false;
        int generation = 0;

        lock (_controllerLock)
        {
            string path = recovery.Info.Path;
            if (!_recoveriesByPath.TryGetValue(path, out RecoveryState? currentRecovery)
                || !ReferenceEquals(currentRecovery, recovery))
            {
                return;
            }

            recovery.Retiring = false;
            pendingConnection = recovery.PendingConnection;
            recovery.PendingConnection = null;

            if (_disposed)
            {
                _recoveriesByPath.Remove(path);
                recovery.Scheduled = false;
                recovery.ScheduledGeneration = null;
            }
            else if (_watching)
            {
                if (recovery.Attempts >= MaxAutomaticRecoveryAttempts)
                {
                    _recoveriesByPath.Remove(path);
                    recovery.Scheduled = false;
                    recovery.ScheduledGeneration = null;
                    reopenPendingConnection = pendingConnection is not null;
                }
                else
                {
                    if (pendingConnection is not null)
                    {
                        recovery.Info = pendingConnection;
                    }

                    generation = _watchGeneration;
                    if (!recovery.Scheduled || recovery.ScheduledGeneration != generation)
                    {
                        recovery.Scheduled = false;
                        recovery.ScheduledGeneration = null;
                        retryCurrentWatcher = true;
                    }
                }
            }
            else if (recovery.KeepForWatcherStart
                && recovery.Attempts < MaxAutomaticRecoveryAttempts)
            {
                if (pendingConnection is not null)
                {
                    recovery.Info = pendingConnection;
                }

                recovery.Scheduled = false;
                recovery.ScheduledGeneration = null;
            }
            else
            {
                _recoveriesByPath.Remove(path);
                recovery.Scheduled = false;
                recovery.ScheduledGeneration = null;
            }
        }

        if (reopenPendingConnection && pendingConnection is not null)
        {
            OnDeviceConnected(this, new DeviceConnectionEventArgs(
                DeviceChangeType.Connected, pendingConnection));
        }
        else if (retryCurrentWatcher)
        {
            ScheduleRecovery(recovery, generation);
        }
    }

    private void ScheduleRecovery(RecoveryState recovery, int generation)
    {
        lock (_controllerLock)
        {
            if (_disposed
                || !_watching
                || _watchGeneration != generation
                || recovery.Retiring
                || recovery.Scheduled
                || recovery.Attempts >= MaxAutomaticRecoveryAttempts
                || !_recoveriesByPath.TryGetValue(recovery.Info.Path, out RecoveryState? current)
                || !ReferenceEquals(current, recovery))
            {
                return;
            }

            recovery.Scheduled = true;
            recovery.ScheduledGeneration = generation;
        }

        _ = Task.Run(() => RecoverSamePathAsync(recovery, generation));
    }

    private async Task RecoverSamePathAsync(RecoveryState recovery, int generation)
    {
        string path = recovery.Info.Path;
        while (true)
        {
            int attempt;
            lock (_controllerLock)
            {
                if (!IsCurrentRecoveryLocked(recovery, generation))
                {
                    ClearRecoveryScheduleIfOwnedLocked(recovery, generation);
                    return;
                }

                if (_controllersByPath.ContainsKey(path))
                {
                    _recoveriesByPath.Remove(path);
                    ClearRecoveryScheduleIfOwnedLocked(recovery, generation);
                    return;
                }

                if (recovery.Attempts >= MaxAutomaticRecoveryAttempts)
                {
                    _recoveriesByPath.Remove(path);
                    ClearRecoveryScheduleIfOwnedLocked(recovery, generation);
                    _log.Warning("Controller input recovery stopped after its bounded retry budget");
                    return;
                }

                attempt = recovery.Attempts + 1;
            }

            await Task.Delay(RecoveryDelays[attempt - 1]).ConfigureAwait(false);
            if (!IsCurrentRecovery(recovery, generation))
            {
                return;
            }

            IHidDeviceInfo? info = null;
            OpeningReservation? reservation = null;
            try
            {
                info = _enumerator.Enumerate(ControllerFactory.KnownDeviceIds)
                    .FirstOrDefault(device => string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // A transient enumeration error consumes one finite attempt. The
                // scanner's normal watcher remains available for later OS changes.
            }

            lock (_controllerLock)
            {
                if (!IsCurrentRecoveryLocked(recovery, generation))
                {
                    ClearRecoveryScheduleIfOwnedLocked(recovery, generation);
                    return;
                }

                recovery.Attempts = attempt;
                if (info is null || (reservation = ReserveOpeningPathLocked(path)) is null)
                {
                    continue;
                }

                recovery.Info = info;
            }

            _log.Info($"Retrying controller input reader (attempt {attempt}/{MaxAutomaticRecoveryAttempts})");
            IControllerDevice? controller = TryCreate(info);
            if (controller is null)
            {
                ReleaseOpeningPath(path, reservation!);
                continue;
            }

            if (TryRegisterOpenedController(info, controller, recovery, generation, reservation!, out ControllerEntry? entry))
            {
                PublishConnected(entry!);
                return;
            }
        }
    }

    private bool IsCurrentRecovery(RecoveryState recovery, int generation)
    {
        lock (_controllerLock)
        {
            return IsCurrentRecoveryLocked(recovery, generation);
        }
    }

    private static void ClearRecoveryScheduleIfOwnedLocked(RecoveryState recovery, int generation)
    {
        if (recovery.Scheduled && recovery.ScheduledGeneration == generation)
        {
            recovery.Scheduled = false;
            recovery.ScheduledGeneration = null;
        }
    }

    private bool IsCurrentRecoveryLocked(RecoveryState recovery, int generation)
    {
        return !_disposed
            && _watching
            && _watchGeneration == generation
            && _recoveriesByPath.TryGetValue(recovery.Info.Path, out RecoveryState? current)
            && ReferenceEquals(current, recovery);
    }

    private OpeningReservation? ReserveOpeningPathLocked(string path)
    {
        if (_openingPaths.ContainsKey(path))
        {
            return null;
        }

        OpeningReservation reservation = new OpeningReservation(_watchGeneration);
        _openingPaths.Add(path, reservation);
        return reservation;
    }

    private void ReleaseOpeningPath(string path, OpeningReservation reservation)
    {
        lock (_controllerLock)
        {
            if (_openingPaths.TryGetValue(path, out OpeningReservation? currentReservation)
                && ReferenceEquals(currentReservation, reservation))
            {
                _openingPaths.Remove(path);
            }
        }
    }

    private void AttachReaderLifecycle(IControllerDevice controller)
    {
        if (controller is DualSenseDevice device)
        {
            device.InputReaderEndedUnexpectedly += OnInputReaderEndedUnexpectedly;
            device.InputReportReceived += OnInputReportReceived;
        }
    }

    private void DetachReaderLifecycle(IControllerDevice controller)
    {
        if (controller is DualSenseDevice device)
        {
            device.InputReaderEndedUnexpectedly -= OnInputReaderEndedUnexpectedly;
            device.InputReportReceived -= OnInputReportReceived;
        }
    }

    private static void SetPhysicalDiagnosticsOwnership(IControllerDevice controller, bool owned)
    {
        if (controller is DualSenseDevice device)
        {
            device.SetScannerPhysicalDiagnosticsOwnership(owned);
        }
    }

    private void RaiseDisconnected(ControllerEntry entry)
    {
        _log.Info($"Controller disconnected: {entry.Info.ProductName} ({entry.Controller.ControllerType})");
        try
        {
            ControllerDisconnected?.Invoke(this, new ControllerConnectionEventArgs(
                DeviceChangeType.Disconnected, entry.Info, entry.Controller.ControllerType, entry.Controller));
        }
        catch
        {
            _log.Warning("A controller-disconnected subscriber failed");
        }
    }

    private static void DisposeController(IControllerDevice controller)
    {
        try
        {
            controller.Dispose();
        }
        catch
        {
            _log.Warning("A retired controller did not dispose cleanly");
        }
    }
}
