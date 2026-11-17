using DualSenseClient.Controllers;
using DualSenseClient.Hid;

namespace DualSenseClient.Tests.Controllers;

/// <summary>
/// Exercises the scanner's stable-path ownership boundary without opening a physical
/// HID handle. The same path is removed, reconnected, and then checked against the old
/// instance—the exact ordering that used to let delayed UI work target a disposed pad.
/// </summary>
public sealed class ControllerScannerLifecycleTests
{
    [Test]
    public void DisconnectAndReconnect_CarriesTheOldOwner_NotTheNewInstance()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("bt:path");
        FakeControllerDevice first = new FakeControllerDevice(info);
        FakeControllerDevice second = new FakeControllerDevice(info);
        int factoryCalls = 0;
        FakeHidEnumerator enumerator = new FakeHidEnumerator(info);
        using ControllerScanner scanner = new ControllerScanner(
            enumerator,
            _ => Interlocked.Increment(ref factoryCalls) == 1 ? first : second);

        IReadOnlyList<IControllerDevice> initial = scanner.Scan();
        Assert.That(initial, Has.Count.EqualTo(1));

        ControllerConnectionEventArgs? disconnected = null;
        ControllerConnectionEventArgs? connected = null;
        scanner.ControllerDisconnected += (_, args) => disconnected = args;
        scanner.ControllerConnected += (_, args) => connected = args;
        scanner.StartWatching();

        enumerator.RaiseDisconnected(info);
        enumerator.RaiseConnected(info);

        Assert.Multiple(() =>
        {
            Assert.That(disconnected, Is.Not.Null);
            Assert.That(disconnected!.Controller, Is.SameAs(first));
            Assert.That(connected, Is.Not.Null);
            Assert.That(connected!.Controller, Is.SameAs(second));
            Assert.That(connected.Controller, Is.Not.SameAs(disconnected.Controller));
            Assert.That(first.DisposeCount, Is.Zero, "the scanner reports ownership; its consumer disposes the old instance");
        });

        // A delayed callback captured before removal must fail the same reference check
        // used by MainViewModel, even though the HID path is identical after reconnect.
        IControllerDevice? current = connected.Controller;
        Assert.That(ReferenceEquals(current, disconnected.Controller), Is.False);
    }

    [Test]
    public void HealthyDuplicateConnectionForTheSamePath_IsRejectedBeforeOpening()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("bt:path");
        FakeControllerDevice first = new FakeControllerDevice(info);
        FakeControllerDevice duplicate = new FakeControllerDevice(info);
        int factoryCalls = 0;
        FakeHidEnumerator enumerator = new FakeHidEnumerator(info);
        using ControllerScanner scanner = new ControllerScanner(
            enumerator,
            _ => Interlocked.Increment(ref factoryCalls) == 1 ? first : duplicate);
        IReadOnlyList<IControllerDevice> initial = scanner.Scan();
        IReadOnlyList<IControllerDevice> repeatedScan = scanner.Scan();

        int connected = 0;
        scanner.ControllerConnected += (_, _) => connected++;
        scanner.StartWatching();
        enumerator.RaiseConnected(info);

        Assert.Multiple(() =>
        {
            Assert.That(connected, Is.Zero);
            Assert.That(factoryCalls, Is.EqualTo(1), "a healthy duplicate must not open another HID wrapper");
            Assert.That(repeatedScan, Has.Count.EqualTo(1));
            Assert.That(repeatedScan[0], Is.SameAs(initial[0]), "a repeated scan reuses the one live reader for this endpoint");
            Assert.That(duplicate.DisposeCount, Is.Zero);
            Assert.That(first.DisposeCount, Is.Zero);
        });
    }

    [Test]
    public void DelayedDisconnectForAnOlderSamePathGeneration_DoesNotRemoveReplacement()
    {
        FakeHidDeviceInfo oldInfo = new FakeHidDeviceInfo("bt:path");
        FakeHidDeviceInfo newInfo = new FakeHidDeviceInfo("bt:path");
        FakeControllerDevice first = new FakeControllerDevice(oldInfo);
        FakeControllerDevice replacement = new FakeControllerDevice(newInfo);
        int factoryCalls = 0;
        FakeHidEnumerator enumerator = new FakeHidEnumerator(oldInfo);
        using ControllerScanner scanner = new ControllerScanner(
            enumerator,
            _ => Interlocked.Increment(ref factoryCalls) == 1 ? first : replacement);
        scanner.Scan();
        int disconnected = 0;
        IControllerDevice? connected = null;
        scanner.ControllerDisconnected += (_, _) => disconnected++;
        scanner.ControllerConnected += (_, args) => connected = args.Controller;
        scanner.StartWatching();

        enumerator.RaiseDisconnected(oldInfo);
        enumerator.SetCurrentInfo(newInfo);
        enumerator.RaiseConnected(newInfo);
        enumerator.RaiseDisconnected(oldInfo);

        Assert.Multiple(() =>
        {
            Assert.That(connected, Is.SameAs(replacement));
            Assert.That(disconnected, Is.EqualTo(1), "the old endpoint notification must be ignored after replacement");
            Assert.That(replacement.DisposeCount, Is.Zero);
        });
    }

    private sealed class FakeControllerDevice(IHidDeviceInfo info) : IControllerDevice
    {
        public int DisposeCount { get; private set; }

        public IHidDeviceInfo Info => info;
        public ConnectionType ConnectionType => info.BusType;
        public ControllerType ControllerType => ControllerType.DualSense;
        public bool IsConnected => DisposeCount == 0;
        public int MaxOutputReportLength => 78;
        public int PollingRateHz => 0;
        public int ReadInput(byte[] buffer, int offset, int count, int timeoutMs) => 0;
        public void SendOutput(byte[] buffer, int offset, int count) { }
        public Task<int> ReadInputAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);
        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64) => [];
        public void SendFeatureReport(byte[] buffer, int offset, int count) { }
        public string GetProductName() => info.ProductName;
        public bool DisconnectController() => false;
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeHidDeviceInfo(string path) : IHidDeviceInfo
    {
        public string Path => path;
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string ProductName => "DualSense Wireless Controller";
        public string Manufacturer => "Sony";
        public int InterfaceNumber => 0;
        public ushort UsagePage => 1;
        public HidUsageId Usage => HidUsageId.GamePad;
        public ConnectionType BusType => ConnectionType.Bluetooth;
    }

    private sealed class FakeHidEnumerator(FakeHidDeviceInfo info) : IHidDeviceEnumerator
    {
        private IHidDeviceInfo _currentInfo = info;

        public event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;
        public event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected;

        public IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null) => [_currentInfo];
        public IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> deviceIds) => [_currentInfo];
        public IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null) => [_currentInfo];
        public IHidDevice OpenDevice(string path) => throw new NotSupportedException();
        public void StartWatching(int intervalMs = 1000) { }
        public void StopWatching() { }
        public void ExcludeDevice(string path) { }
        public void RemoveExcludedDevice(string path) { }
        public void Dispose() { }

        public void SetCurrentInfo(FakeHidDeviceInfo device) => _currentInfo = device;

        public void RaiseConnected(IHidDeviceInfo device) =>
            DeviceConnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Connected, device));

        public void RaiseDisconnected(IHidDeviceInfo device) =>
            DeviceDisconnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Disconnected, device));
    }
}
