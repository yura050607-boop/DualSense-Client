using System.Collections.Concurrent;
using DualSenseClient.Controllers;
using DualSenseClient.Controllers.Devices;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Hid;
using DualSenseClient.Tests.Controllers.DualSense.Input;

namespace DualSenseClient.Tests.Controllers;

public sealed class ControllerReaderRecoveryTests
{
    [Test]
    public async Task BlockingReadFailure_RemovesOldScannerOwner_ReopensSamePath_AndRoutesFreshInput()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        FakeHidDevice failedHandle = new FakeHidDevice(info.Path);
        FakeHidDevice recoveredHandle = new FakeHidDevice(info.Path);
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, failedHandle, recoveredHandle);
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        using ControllerScanner scanner = new ControllerScanner(
            enumerator,
            deviceInfo => new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, diagnostics));
        TaskCompletionSource<IControllerDevice> disconnected = new TaskCompletionSource<IControllerDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DualSenseDevice> reconnected = new TaskCompletionSource<DualSenseDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<InputReport> routedInput = new TaskCompletionSource<InputReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        scanner.ControllerDisconnected += (_, args) =>
        {
            if (args.Controller is not null)
            {
                disconnected.TrySetResult(args.Controller);
            }
        };
        scanner.ControllerConnected += (_, args) =>
        {
            if (args.Controller is DualSenseDevice device)
            {
                device.InputReportReceived += (_, report) => routedInput.TrySetResult(report);
                reconnected.TrySetResult(device);
            }
        };

        IReadOnlyList<IControllerDevice> initial = scanner.Scan();
        Assert.That(initial, Has.Count.EqualTo(1));
        DualSenseDevice oldController = (DualSenseDevice)initial[0];
        Assert.That(failedHandle.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
        scanner.StartWatching();

        failedHandle.FailRead(new HidException("simulated blocking HID read failure"));

        IControllerDevice removed = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DualSenseDevice replacement = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(removed, Is.SameAs(oldController));
        Assert.That(replacement, Is.Not.SameAs(oldController));
        Assert.That(replacement.Info.Path, Is.EqualTo(oldController.Info.Path));
        Assert.That(oldController.IsConnected, Is.False);
        Assert.That(failedHandle.DisposeCount, Is.EqualTo(1), "the old reader must be disposed before recovery opens its replacement");
        Assert.That(enumerator.ReplacementOpenedAfterPriorDispose, Is.True,
            "the scanner must retire the failed HID reader before opening the replacement generation");
        Assert.That(enumerator.OpenCount, Is.EqualTo(2), "recovery must open only after retiring the old reader");
        recoveredHandle.Emit(InputReportTestData.UsbReport);
        InputReport report = await routedInput.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(report.Input.L2, Is.EqualTo(InputReportTestData.UsbReport[5]));
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadFailed), Is.EqualTo(1),
                "the failed source should count its final read error while it still owns the scanner path");
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(1),
                "the accepted same-path replacement should contribute its parsed read");
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(1));
        });
        replacement.Dispose();
    }

    [Test]
    public async Task ZeroLengthBlockingRead_IsAnUnexpectedReaderEnd()
    {
        FakeHidDevice hid = new FakeHidDevice("test-path");
        DualSenseDevice device = new DualSenseDevice(hid, new FakeHidDeviceInfo(hid.DevicePath));
        TaskCompletionSource ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        device.InputReaderEndedUnexpectedly += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            ended.TrySetResult();
        };

        Assert.That(hid.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
        hid.EndRead();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(device.IsConnected, Is.False);
            Assert.That(notifications, Is.EqualTo(1));
        });
        device.Dispose();
    }

    [Test]
    public void NormalDispose_DoesNotReportAnUnexpectedReaderEnd()
    {
        FakeHidDevice hid = new FakeHidDevice("test-path");
        DualSenseDevice device = new DualSenseDevice(hid, new FakeHidDeviceInfo(hid.DevicePath));
        int notifications = 0;
        device.InputReaderEndedUnexpectedly += (_, _) => notifications++;

        Assert.That(hid.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
        device.Dispose();

        Assert.That(notifications, Is.Zero);
    }

    [Test]
    public async Task ReaderCanFailBeforeScannerSubscribes_AndStartupRecoveryStillRuns()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        FakeHidDevice failedHandle = new FakeHidDevice(info.Path,
            new HidException("simulated constructor-time read failure"));
        FakeHidDevice recoveredHandle = new FakeHidDevice(info.Path);
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, failedHandle, recoveredHandle);
        int factoryCalls = 0;
        using ControllerScanner scanner = new ControllerScanner(
            enumerator,
            deviceInfo =>
            {
                DualSenseDevice controller = new DualSenseDevice(
                    enumerator.OpenDevice(deviceInfo.Path), deviceInfo);
                if (Interlocked.Increment(ref factoryCalls) == 1)
                {
                    Assert.That(failedHandle.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
                    Assert.That(SpinWait.SpinUntil(() => !controller.IsConnected, TimeSpan.FromSeconds(2)), Is.True,
                        "the read must finish before the scanner installs its lifecycle callback");
                }

                return controller;
            });
        TaskCompletionSource<DualSenseDevice> connected = new TaskCompletionSource<DualSenseDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.ControllerConnected += (_, args) =>
        {
            if (args.Controller is DualSenseDevice device)
            {
                connected.TrySetResult(device);
            }
        };

        IReadOnlyList<IControllerDevice> initial = scanner.Scan();
        scanner.StartWatching();
        DualSenseDevice replacement = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(initial, Is.Empty, "an already-ended reader must not be published as healthy");
            Assert.That(replacement.Info.Path, Is.EqualTo(info.Path));
            Assert.That(enumerator.OpenCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RepeatedReadFailures_ExhaustOnlyTheFiniteRecoveryBudget()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        FakeHidDevice[] devices = Enumerable.Range(0, 4)
            .Select(_ => new FakeHidDevice(info.Path, new HidException("simulated repeated read failure")))
            .ToArray();
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, devices);
        using ControllerScanner scanner = new ControllerScanner(enumerator);

        scanner.Scan();
        scanner.StartWatching();

        Assert.That(SpinWait.SpinUntil(() => enumerator.OpenCount == 4, TimeSpan.FromSeconds(6)), Is.True);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.That(enumerator.OpenCount, Is.EqualTo(4), "initial reader plus at most three recovery opens");
    }

    [Test]
    public async Task ExhaustedRecoveryBudget_DoesNotOpenWhilePriorReaderDisposalIsBlocked()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        using ManualResetEventSlim releaseRetiredDispose = new(false);
        TaskCompletionSource retiredDisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHidDevice[] devices =
        [
            new FakeHidDevice(info.Path),
            new FakeHidDevice(info.Path),
            new FakeHidDevice(info.Path),
            new FakeHidDevice(info.Path, disposeEntered: retiredDisposeEntered, releaseDispose: releaseRetiredDispose),
            new FakeHidDevice(info.Path)
        ];
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, devices);
        ControllerScanner scanner = new ControllerScanner(enumerator);
        TaskCompletionSource<IControllerDevice>[] connected = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<IControllerDevice>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        int publications = 0;
        scanner.ControllerConnected += (_, args) => connected[Math.Min(Interlocked.Increment(ref publications) - 1, connected.Length - 1)]
            .TrySetResult(args.Controller!);

        try
        {
            IReadOnlyList<IControllerDevice> initial = scanner.Scan();
            Assert.That(initial, Has.Count.EqualTo(1));
            scanner.StartWatching();
            Assert.That(devices[0].ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            devices[0].FailRead(new HidException("simulated initial reader failure"));

            _ = await connected[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            devices[1].FailRead(new HidException("simulated first replacement failure"));
            _ = await connected[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            devices[2].FailRead(new HidException("simulated second replacement failure"));
            DualSenseDevice lastRecovery = (DualSenseDevice)await connected[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(enumerator.OpenCount, Is.EqualTo(4), "initial reader plus three finite recovery opens");
            Assert.That(devices[3].ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

            devices[3].FailRead(new HidException("simulated final recovery reader failure"));
            await retiredDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            enumerator.RaiseConnected();

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(4), "a new wrapper must wait for the previous owner's disposal");
                Assert.That(devices[3].DisposeCount, Is.Zero, "the old handle is still in its blocked disposal");
                Assert.That(lastRecovery.IsConnected, Is.False);
            });

            releaseRetiredDispose.Set();
            IControllerDevice replacement = await connected[3].Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(5), "the connection observed during retirement is recovered once afterward");
                Assert.That(devices[3].DisposeCount, Is.EqualTo(1));
                Assert.That(enumerator.ReplacementOpenedAfterPriorDispose, Is.True);
                Assert.That(replacement.Info.Path, Is.EqualTo(info.Path));
                Assert.That(Volatile.Read(ref publications), Is.EqualTo(4));
            });
        }
        finally
        {
            releaseRetiredDispose.Set();
            scanner.Dispose();
            SpinWait.SpinUntil(() => devices[3].DisposeCount == 1, TimeSpan.FromSeconds(2));
            if (connected[3].Task.IsCompletedSuccessfully && connected[3].Task.Result is IDisposable replacement)
            {
                replacement.Dispose();
            }
        }
    }

    [Test]
    public async Task FirstFailureRetirement_SurvivesWatcherRestartAndScanUntilDisposeCompletes()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        using ManualResetEventSlim releaseRetiredDispose = new(false);
        TaskCompletionSource retiredDisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHidDevice retired = new FakeHidDevice(info.Path,
            disposeEntered: retiredDisposeEntered, releaseDispose: releaseRetiredDispose);
        FakeHidDevice replacementHandle = new FakeHidDevice(info.Path);
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, retired, replacementHandle);
        ControllerScanner scanner = new ControllerScanner(enumerator);
        TaskCompletionSource<IControllerDevice> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = 0;
        IControllerDevice? replacement = null;
        scanner.ControllerConnected += (_, args) =>
        {
            Interlocked.Increment(ref publications);
            replacement = args.Controller;
            connected.TrySetResult(args.Controller!);
        };

        try
        {
            Assert.That(scanner.Scan(), Has.Count.EqualTo(1));
            scanner.StartWatching();
            Assert.That(retired.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            retired.EndRead();
            await retiredDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            scanner.StopWatching();
            scanner.StartWatching();
            enumerator.RaiseConnected();
            Assert.That(enumerator.OpenCount, Is.EqualTo(1),
                "a current-generation connect must not reopen the path while first-failure disposal is blocked");
            Assert.That(scanner.Scan(), Is.Empty, "a synchronous current-session scan must not open a second wrapper during retirement");

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(1),
                    "the first-failure retirement must retain endpoint ownership across watcher restart");
                Assert.That(retired.DisposeCount, Is.Zero, "the original handle still owns the endpoint while Dispose is blocked");
                Assert.That(publications, Is.Zero, "no replacement may publish before the old handle is disposed");
            });

            releaseRetiredDispose.Set();
            IControllerDevice recovered = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(2));
                Assert.That(retired.DisposeCount, Is.EqualTo(1));
                Assert.That(enumerator.ReplacementOpenedAfterPriorDispose, Is.True);
                Assert.That(recovered.Info.Path, Is.EqualTo(info.Path));
                Assert.That(publications, Is.EqualTo(1), "the current watcher must publish one healthy replacement");
            });

            enumerator.RaiseConnected();
            Assert.That(enumerator.OpenCount, Is.EqualTo(2), "a duplicate event must not create a second replacement");
            Assert.That(publications, Is.EqualTo(1));
        }
        finally
        {
            releaseRetiredDispose.Set();
            scanner.Dispose();
            SpinWait.SpinUntil(() => retired.DisposeCount == 1, TimeSpan.FromSeconds(2));
            if (replacement is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    [Test]
    public async Task FailedStartupReaderRetirement_SurvivesWatcherRestartAndPublishesAfterDispose()
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        using ManualResetEventSlim releaseRetiredDispose = new(false);
        TaskCompletionSource retiredDisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHidDevice retired = new FakeHidDevice(info.Path, firstReadReturnsZero: true,
            disposeEntered: retiredDisposeEntered, releaseDispose: releaseRetiredDispose);
        FakeHidDevice replacementHandle = new FakeHidDevice(info.Path);
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, retired, replacementHandle);
        int factoryCalls = 0;
        ControllerScanner scanner = new ControllerScanner(enumerator, deviceInfo =>
        {
            DualSenseDevice device = new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo);
            if (Interlocked.Increment(ref factoryCalls) == 1)
            {
                Assert.That(retired.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(SpinWait.SpinUntil(() => !device.IsConnected, TimeSpan.FromSeconds(2)), Is.True,
                    "the first reader must end before scanner registration checks its health");
            }

            return device;
        });
        TaskCompletionSource<IControllerDevice> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = 0;
        IControllerDevice? replacement = null;
        scanner.ControllerConnected += (_, args) =>
        {
            Interlocked.Increment(ref publications);
            replacement = args.Controller;
            connected.TrySetResult(args.Controller!);
        };
        Task? initialConnect = null;

        try
        {
            scanner.StartWatching();
            initialConnect = Task.Run(enumerator.RaiseConnected);
            await retiredDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            scanner.StopWatching();
            scanner.StartWatching();
            enumerator.RaiseConnected();
            Assert.That(enumerator.OpenCount, Is.EqualTo(1),
                "a failed startup wrapper must retain its handle ownership during a watcher restart");

            releaseRetiredDispose.Set();
            await initialConnect.WaitAsync(TimeSpan.FromSeconds(2));
            IControllerDevice recovered = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(2));
                Assert.That(retired.DisposeCount, Is.EqualTo(1));
                Assert.That(enumerator.ReplacementOpenedAfterPriorDispose, Is.True);
                Assert.That(recovered.Info.Path, Is.EqualTo(info.Path));
                Assert.That(publications, Is.EqualTo(1));
            });
        }
        finally
        {
            releaseRetiredDispose.Set();
            if (initialConnect is not null)
            {
                await initialConnect.WaitAsync(TimeSpan.FromSeconds(2));
            }
            scanner.Dispose();
            SpinWait.SpinUntil(() => retired.DisposeCount == 1, TimeSpan.FromSeconds(2));
            if (replacement is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    [TestCase(false, TestName = "StoppedScannerDoesNotOpenWhileFirstFailureRetirementIsBlocked")]
    [TestCase(true, TestName = "DisposedScannerDoesNotOpenAfterFirstFailureRetirement")]
    public async Task StopOrDisposeDuringFirstFailureRetirement_DoesNotReopen(bool disposeScanner)
    {
        FakeHidDeviceInfo info = new FakeHidDeviceInfo("test-path");
        using ManualResetEventSlim releaseRetiredDispose = new(false);
        TaskCompletionSource retiredDisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHidDevice retired = new FakeHidDevice(info.Path,
            disposeEntered: retiredDisposeEntered, releaseDispose: releaseRetiredDispose);
        FakeHidDevice replacement = new FakeHidDevice(info.Path);
        FakeHidDeviceEnumerator enumerator = new FakeHidDeviceEnumerator(info, retired, replacement);
        ControllerScanner scanner = new ControllerScanner(enumerator);
        int publications = 0;
        IControllerDevice? scannedReplacement = null;
        scanner.ControllerConnected += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            Assert.That(scanner.Scan(), Has.Count.EqualTo(1));
            scanner.StartWatching();
            Assert.That(retired.ReadEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            retired.EndRead();
            await retiredDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            scanner.StopWatching();
            if (disposeScanner)
            {
                scanner.Dispose();
            }
            else
            {
                Assert.That(scanner.Scan(), Is.Empty,
                    "a stopped scanner must retain the retiring path until its old handle has released it");
            }

            Assert.That(enumerator.OpenCount, Is.EqualTo(1));
            releaseRetiredDispose.Set();
            Assert.That(SpinWait.SpinUntil(() => retired.DisposeCount == 1, TimeSpan.FromSeconds(2)), Is.True);
            enumerator.RaiseConnected();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.Multiple(() =>
            {
                Assert.That(enumerator.OpenCount, Is.EqualTo(1), "stopped/disposed scanner work must never reopen the retired path");
                Assert.That(publications, Is.Zero);
            });

            if (!disposeScanner)
            {
                IReadOnlyList<IControllerDevice> scanned = scanner.Scan();
                Assert.That(scanned, Has.Count.EqualTo(1), "an explicit scan may reopen only after retirement has completed");
                scannedReplacement = scanned[0];
                Assert.That(enumerator.OpenCount, Is.EqualTo(2));
            }
        }
        finally
        {
            releaseRetiredDispose.Set();
            scanner.Dispose();
            SpinWait.SpinUntil(() => retired.DisposeCount == 1, TimeSpan.FromSeconds(2));
            if (scannedReplacement is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed class FakeHidDevice : IHidDevice
    {
        private readonly BlockingCollection<ReadResponse> _reads = new BlockingCollection<ReadResponse>();
        private readonly string _path;
        private int _disposed;
        private int _disposeCount;

        private readonly TaskCompletionSource? _disposeEntered;
        private readonly ManualResetEventSlim? _releaseDispose;

        public FakeHidDevice(string path, Exception? firstReadError = null, bool firstReadReturnsZero = false,
            TaskCompletionSource? disposeEntered = null, ManualResetEventSlim? releaseDispose = null)
        {
            _path = path;
            _disposeEntered = disposeEntered;
            _releaseDispose = releaseDispose;
            if (firstReadError is not null)
            {
                _reads.Add(new ReadResponse(null, firstReadError));
            }
            else if (firstReadReturnsZero)
            {
                _reads.Add(new ReadResponse(null, null));
            }
        }

        public TaskCompletionSource ReadEntered { get; } = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFailureThrown { get; } = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string DevicePath => _path;
        public bool IsConnected => Volatile.Read(ref _disposed) == 0;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            ReadEntered.TrySetResult();
            ReadResponse response = _reads.Take();
            if (response.Error is not null)
            {
                ReadFailureThrown.TrySetResult();
                throw response.Error;
            }

            if (response.Data is null)
            {
                return 0;
            }

            int length = Math.Min(count, response.Data.Length);
            Buffer.BlockCopy(response.Data, 0, buffer, offset, length);
            return length;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);
        public int Write(byte[] buffer, int offset, int count) => count;
        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64) => [];
        public void SendFeatureReport(byte[] buffer, int offset, int count) { }
        public string GetProductName() => "Test DualSense";

        public void FailRead(Exception exception) => _reads.Add(new ReadResponse(null, exception));

        public void EndRead() => _reads.Add(new ReadResponse(null, null));

        public void Emit(byte[] report) => _reads.Add(new ReadResponse((byte[])report.Clone(), null));

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _disposeEntered?.TrySetResult();
                if (_releaseDispose is not null && !_releaseDispose.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("simulated HID disposal gate");
                }

                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _disposeCount);
                    _reads.Add(new ReadResponse(null, null));
                }
            }
        }

        private sealed record ReadResponse(byte[]? Data, Exception? Error);
    }

    private sealed class FakeHidDeviceInfo(string path) : IHidDeviceInfo
    {
        public string Path => path;
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string ProductName => "DualSense Test";
        public string Manufacturer => "Sony";
        public int InterfaceNumber => 0;
        public ushort UsagePage => 1;
        public HidUsageId Usage => HidUsageId.GamePad;
        public ConnectionType BusType => ConnectionType.Usb;
    }

    private sealed class FakeHidDeviceEnumerator : IHidDeviceEnumerator
    {
        private readonly Queue<FakeHidDevice> _devices;
        private readonly FakeHidDeviceInfo _info;
        private FakeHidDevice? _previouslyOpened;
        private int _openCount;

        public FakeHidDeviceEnumerator(FakeHidDeviceInfo info, params FakeHidDevice[] devices)
        {
            _info = info;
            _devices = new Queue<FakeHidDevice>(devices);
        }

        public event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;
        public event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected;

        public int OpenCount => Volatile.Read(ref _openCount);
        public bool ReplacementOpenedAfterPriorDispose { get; private set; } = true;
        public IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null) => [_info];
        public IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> deviceIds) => [_info];
        public IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null) => [_info];

        public IHidDevice OpenDevice(string path)
        {
            Assert.That(path, Is.EqualTo(_info.Path));
            if (Interlocked.Increment(ref _openCount) > 1 && _previouslyOpened is not null)
            {
                ReplacementOpenedAfterPriorDispose = _previouslyOpened.DisposeCount == 1;
            }

            if (_devices.Count == 0)
            {
                throw new IOException("No fake HID handle remains.");
            }

            _previouslyOpened = _devices.Dequeue();
            return _previouslyOpened;
        }

        public void StartWatching(int intervalMs = 1000) { }
        public void StopWatching() { }
        public void ExcludeDevice(string path) { }
        public void RemoveExcludedDevice(string path) { }
        public void Dispose() { }

        public void RaiseConnected() => DeviceConnected?.Invoke(this,
            new DeviceConnectionEventArgs(DeviceChangeType.Connected, _info));

        public void RaiseDisconnected() => DeviceDisconnected?.Invoke(this,
            new DeviceConnectionEventArgs(DeviceChangeType.Disconnected, _info));
    }
}
