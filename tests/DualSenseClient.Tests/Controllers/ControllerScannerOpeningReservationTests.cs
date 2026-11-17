using DualSenseClient.Controllers;
using DualSenseClient.Hid;

namespace DualSenseClient.Tests.Controllers;

public sealed class ControllerScannerOpeningReservationTests
{
    [Test]
    public async Task OldScanFailureCannotReleaseCurrentWatchReservation()
    {
        FakeHidDeviceInfo info = new("parent:synthetic-path");
        FakeControllerDevice currentDevice = new(info);
        using ManualResetEventSlim scanEntered = new(false);
        using ManualResetEventSlim watchEntered = new(false);
        using ManualResetEventSlim releaseScan = new(false);
        using ManualResetEventSlim releaseWatch = new(false);
        int factoryCalls = 0;
        int connectedCount = 0;
        IControllerDevice? connectedController = null;
        FakeHidEnumerator enumerator = new(info);
        using ControllerScanner scanner = new(enumerator, _ =>
        {
            int call = Interlocked.Increment(ref factoryCalls);
            if (call == 1)
            {
                scanEntered.Set();
                if (!releaseScan.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("scan factory gate");
                }

                return null;
            }

            if (call == 2)
            {
                watchEntered.Set();
                if (!releaseWatch.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("watch factory gate");
                }
            }

            return currentDevice;
        });
        scanner.ControllerConnected += (_, args) =>
        {
            Interlocked.Increment(ref connectedCount);
            connectedController = args.Controller;
        };

        Task<IReadOnlyList<IControllerDevice>>? scan = null;
        Task? currentConnect = null;
        try
        {
            scan = Task.Run(scanner.Scan);
            Assert.That(scanEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "the one-shot scan should be in flight");

            scanner.StartWatching();
            currentConnect = Task.Run(() => enumerator.RaiseConnected());
            Assert.That(watchEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "the current watcher open should be in flight");

            releaseScan.Set();
            Assert.That(await scan.WaitAsync(TimeSpan.FromSeconds(2)), Is.Empty);
            enumerator.RaiseConnected();

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2), "a stale Scan completion must not release the current watcher reservation");
                Assert.That(currentDevice.DisposeCount, Is.Zero);
                Assert.That(connectedCount, Is.Zero);
            });

            releaseWatch.Set();
            await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2));
                Assert.That(connectedCount, Is.EqualTo(1));
                Assert.That(connectedController, Is.SameAs(currentDevice));
                Assert.That(currentDevice.DisposeCount, Is.Zero);
            });
        }
        finally
        {
            releaseScan.Set();
            releaseWatch.Set();
            if (scan is not null)
            {
                await scan.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (currentConnect is not null)
            {
                await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));
            }

            scanner.Dispose();
            if (currentDevice.DisposeCount == 0)
            {
                currentDevice.Dispose();
            }
        }
    }

    [Test]
    public async Task OldSuccessfulOpenCannotReleaseNewWatchGenerationReservation()
    {
        FakeHidDeviceInfo info = new("parent:synthetic-path");
        FakeControllerDevice[] devices = Enumerable.Range(0, 3)
            .Select(_ => new FakeControllerDevice(info))
            .ToArray();
        using ManualResetEventSlim firstEntered = new(false);
        using ManualResetEventSlim secondEntered = new(false);
        using ManualResetEventSlim releaseFirst = new(false);
        using ManualResetEventSlim releaseSecond = new(false);
        int factoryCalls = 0;
        int connectedCount = 0;
        IControllerDevice? connectedController = null;
        FakeHidEnumerator enumerator = new(info);
        using ControllerScanner scanner = new(enumerator, _ =>
        {
            int call = Interlocked.Increment(ref factoryCalls);
            if (call == 1)
            {
                firstEntered.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("old factory gate");
                }
            }
            else if (call == 2)
            {
                secondEntered.Set();
                if (!releaseSecond.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("current factory gate");
                }
            }

            return devices[Math.Min(call - 1, devices.Length - 1)];
        });
        scanner.ControllerConnected += (_, args) =>
        {
            Interlocked.Increment(ref connectedCount);
            connectedController = args.Controller;
        };

        Task? oldConnect = null;
        Task? currentConnect = null;
        try
        {
            scanner.StartWatching();
            oldConnect = Task.Run(() => enumerator.RaiseConnected());
            Assert.That(firstEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "old open should be in flight");

            scanner.StopWatching();
            scanner.StartWatching();
            currentConnect = Task.Run(() => enumerator.RaiseConnected());
            Assert.That(secondEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "new-generation open should be in flight");

            releaseFirst.Set();
            await oldConnect.WaitAsync(TimeSpan.FromSeconds(2));
            enumerator.RaiseConnected();

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2), "stale completion must not release the current path reservation");
                Assert.That(devices[0].DisposeCount, Is.EqualTo(1), "the stale successful wrapper must be disposed");
                Assert.That(devices[1].DisposeCount, Is.Zero, "the current wrapper remains owned by its open operation");
                Assert.That(connectedCount, Is.Zero, "a stale wrapper must never be published");
            });

            releaseSecond.Set();
            await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2));
                Assert.That(connectedCount, Is.EqualTo(1), "the current generation publishes once");
                Assert.That(connectedController, Is.SameAs(devices[1]));
                Assert.That(devices[1].DisposeCount, Is.Zero);
            });
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await AwaitIfStarted(oldConnect);
            await AwaitIfStarted(currentConnect);
            scanner.Dispose();
            foreach (FakeControllerDevice device in devices)
            {
                if (device.DisposeCount == 0)
                {
                    device.Dispose();
                }
            }
        }
    }

    [TestCase(false, TestName = "OldNullFactoryResultCannotReleaseNewWatchGenerationReservation")]
    [TestCase(true, TestName = "OldFactoryFailureCannotReleaseNewWatchGenerationReservation")]
    public async Task OldFailedOpenCannotReleaseNewWatchGenerationReservation(bool throwFromOldFactory)
    {
        FakeHidDeviceInfo info = new("parent:synthetic-path");
        FakeControllerDevice[] devices = Enumerable.Range(0, 3)
            .Select(_ => new FakeControllerDevice(info))
            .ToArray();
        using ManualResetEventSlim firstEntered = new(false);
        using ManualResetEventSlim secondEntered = new(false);
        using ManualResetEventSlim releaseFirst = new(false);
        using ManualResetEventSlim releaseSecond = new(false);
        int factoryCalls = 0;
        int connectedCount = 0;
        IControllerDevice? connectedController = null;
        FakeHidEnumerator enumerator = new(info);
        using ControllerScanner scanner = new(enumerator, _ =>
        {
            int call = Interlocked.Increment(ref factoryCalls);
            if (call == 1)
            {
                firstEntered.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("old factory gate");
                }

                if (throwFromOldFactory)
                {
                    throw new InvalidOperationException("simulated old factory failure");
                }

                return null;
            }

            if (call == 2)
            {
                secondEntered.Set();
                if (!releaseSecond.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("current factory gate");
                }
            }

            return devices[Math.Min(call - 2, devices.Length - 1)];
        });
        scanner.ControllerConnected += (_, args) =>
        {
            Interlocked.Increment(ref connectedCount);
            connectedController = args.Controller;
        };

        Task? oldConnect = null;
        Task? currentConnect = null;
        try
        {
            scanner.StartWatching();
            oldConnect = Task.Run(() => enumerator.RaiseConnected());
            Assert.That(firstEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "old open should be in flight");

            scanner.StopWatching();
            scanner.StartWatching();
            currentConnect = Task.Run(() => enumerator.RaiseConnected());
            Assert.That(secondEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "new-generation open should be in flight");

            releaseFirst.Set();
            await oldConnect.WaitAsync(TimeSpan.FromSeconds(2));
            enumerator.RaiseConnected();

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2), "null or failing stale work must not release a newer reservation");
                Assert.That(connectedCount, Is.Zero, "no wrapper can publish while the current open is blocked");
                Assert.That(devices[1].DisposeCount, Is.Zero);
            });

            releaseSecond.Set();
            await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2));
                Assert.That(connectedCount, Is.EqualTo(1));
                Assert.That(connectedController, Is.SameAs(devices[0]));
                Assert.That(devices[0].DisposeCount, Is.Zero);
            });
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await AwaitIfStarted(oldConnect);
            await AwaitIfStarted(currentConnect);
            scanner.Dispose();
            foreach (FakeControllerDevice device in devices)
            {
                if (device.DisposeCount == 0)
                {
                    device.Dispose();
                }
            }
        }
    }

    private static async Task AwaitIfStarted(Task? task)
    {
        if (task is not null)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class FakeControllerDevice(IHidDeviceInfo info) : IControllerDevice
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
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
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FakeHidDeviceInfo(string path) : IHidDeviceInfo
    {
        public string Path => path;
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string ProductName => "Synthetic DualSense";
        public string Manufacturer => "Synthetic";
        public int InterfaceNumber => 0;
        public ushort UsagePage => 1;
        public HidUsageId Usage => HidUsageId.GamePad;
        public ConnectionType BusType => ConnectionType.Bluetooth;
    }

    private sealed class FakeHidEnumerator(IHidDeviceInfo info) : IHidDeviceEnumerator
    {
        public event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;
        public event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected { add { } remove { } }
        public IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null) => [info];
        public IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> ids) => [info];
        public IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null) => [info];
        public IHidDevice OpenDevice(string path) => throw new NotSupportedException();
        public void StartWatching(int intervalMs = 1000) { }
        public void StopWatching() { }
        public void ExcludeDevice(string path) { }
        public void RemoveExcludedDevice(string path) { }
        public void Dispose() { }

        public void RaiseConnected() => DeviceConnected?.Invoke(this,
            new DeviceConnectionEventArgs(DeviceChangeType.Connected, info));
    }
}
