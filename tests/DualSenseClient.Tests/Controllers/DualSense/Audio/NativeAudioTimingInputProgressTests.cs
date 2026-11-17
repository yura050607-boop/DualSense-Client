using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DualSenseClient.Controllers;
using DualSenseClient.Controllers.Devices;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Hid;
using DualSenseClient.Tests.Controllers.DualSense.Input;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

[NonParallelizable]
public sealed class NativeAudioTimingInputProgressTests
{
    private sealed record ReadResult(byte[]? Report, Exception? Error, bool EndOfStream = false);

    private sealed class ScriptedHidDevice : IHidDevice
    {
        private readonly BlockingCollection<ReadResult> _reads = new();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _devicePath;
        private readonly byte[] _pairingReport;

        public ScriptedHidDevice(string devicePath = "diagnostics-test", byte[]? pairingReport = null)
        {
            _devicePath = devicePath;
            _pairingReport = pairingReport ?? [];
        }

        public Task ReadStarted => _readStarted.Task;
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string DevicePath => _devicePath;
        public bool IsConnected => true;

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            _readStarted.TrySetResult();
            ReadResult result;
            try
            {
                result = _reads.Take();
            }
            catch (InvalidOperationException)
            {
                return 0;
            }

            if (result.Error is { } error)
            {
                throw error;
            }

            if (result.EndOfStream)
            {
                return 0;
            }

            byte[] report = result.Report!;
            if (report.Length > count)
            {
                throw new InvalidOperationException("The scripted report exceeds the requested read buffer.");
            }

            Buffer.BlockCopy(report, 0, buffer, offset, report.Length);
            return report.Length;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);
        public int Write(byte[] buffer, int offset, int count) => count;
        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64) => reportId == 0x09 ? _pairingReport : [];
        public void SendFeatureReport(byte[] buffer, int offset, int count)
        {
        }

        public string GetProductName() => "Diagnostics test controller";
        public void Dispose() => _reads.CompleteAdding();

        public void Enqueue(byte[] report) => _reads.Add(new ReadResult(report, null));
        public void Fail(Exception error) => _reads.Add(new ReadResult(null, error));
        public void End() => _reads.Add(new ReadResult(null, null, EndOfStream: true));
    }

    private sealed class StubHidDeviceInfo(string path, ConnectionType connectionType) : IHidDeviceInfo
    {
        public string Path => path;
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string ProductName => "DualSense diagnostics test";
        public string Manufacturer => "Sony";
        public int InterfaceNumber => 0;
        public ushort UsagePage => 1;
        public HidUsageId Usage => HidUsageId.GamePad;
        public ConnectionType BusType => connectionType;
    }

    private sealed class ScriptedHidEnumerator : IHidDeviceEnumerator
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, Queue<IHidDevice>> _pendingOpens = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<IHidDeviceInfo> _currentDevices;

        public ScriptedHidEnumerator(params (IHidDeviceInfo Info, ScriptedHidDevice Hid)[] devices)
        {
            _currentDevices = devices.Select(device => device.Info).ToArray();
            foreach ((IHidDeviceInfo info, ScriptedHidDevice hid) in devices)
            {
                QueueOpen(info.Path, hid);
            }
        }

        public event EventHandler<DeviceConnectionEventArgs>? DeviceConnected;
        public event EventHandler<DeviceConnectionEventArgs>? DeviceDisconnected;

        public IReadOnlyList<IHidDeviceInfo> Enumerate(ushort? vendorId = null, ushort? productId = null)
        {
            lock (_sync)
            {
                return _currentDevices
                    .Where(info => vendorId is null || info.VendorId == vendorId)
                    .Where(info => productId is null || info.ProductId == productId)
                    .ToArray();
            }
        }

        public IReadOnlyList<IHidDeviceInfo> Enumerate(IEnumerable<(ushort VendorId, ushort ProductId)> deviceIds)
        {
            HashSet<(ushort VendorId, ushort ProductId)> accepted = deviceIds.ToHashSet();
            lock (_sync)
            {
                return _currentDevices
                    .Where(info => accepted.Contains((info.VendorId, info.ProductId)))
                    .ToArray();
            }
        }

        public IReadOnlyList<IHidDeviceInfo> EnumerateIncludingExcluded(ushort? vendorId = null, ushort? productId = null) =>
            Enumerate(vendorId, productId);

        public IHidDevice OpenDevice(string path)
        {
            lock (_sync)
            {
                if (!_pendingOpens.TryGetValue(path, out Queue<IHidDevice>? devices) || devices.Count == 0)
                {
                    throw new InvalidOperationException("No scripted HID device is queued for this path.");
                }

                return devices.Dequeue();
            }
        }

        public void StartWatching(int intervalMs = 1000) { }
        public void StopWatching() { }
        public void ExcludeDevice(string path) { }
        public void RemoveExcludedDevice(string path) { }
        public void Dispose() { }

        public void QueueOpen(string path, ScriptedHidDevice device)
        {
            lock (_sync)
            {
                if (!_pendingOpens.TryGetValue(path, out Queue<IHidDevice>? devices))
                {
                    devices = new Queue<IHidDevice>();
                    _pendingOpens.Add(path, devices);
                }

                devices.Enqueue(device);
            }
        }

        public void SetCurrentDevices(params IHidDeviceInfo[] devices)
        {
            lock (_sync)
            {
                _currentDevices = devices.ToArray();
            }
        }

        public void RaiseConnected(IHidDeviceInfo info) =>
            DeviceConnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Connected, info));

        public void RaiseDisconnected(IHidDeviceInfo info) =>
            DeviceDisconnected?.Invoke(this, new DeviceConnectionEventArgs(DeviceChangeType.Disconnected, info));
    }

    private sealed class NoOpOutputs : IDualSenseOutputs
    {
        public void SetVibration(byte left, byte right)
        {
        }

        public void SendOutputState(SetStateData payload)
        {
        }
    }

    private static readonly MethodInfo ProcessInputReportCoreMethod = typeof(DualSenseDevice)
        .GetMethod("ProcessInputReportCore", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Test]
    public async Task UnacceptedReaderDoesNotCountPhysicalInputProgress()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        ScriptedHidDevice hid = new();
        using DualSenseDevice candidate = new(hid, new StubHidDeviceInfo(hid.DevicePath, ConnectionType.Bluetooth), diagnostics);
        TaskCompletionSource parsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.InputReportReceived += (_, _) => parsed.TrySetResult();

        await hid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        hid.Enqueue(BluetoothReport(crossPressed: true));
        await parsed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.Zero,
            "A reader that has not passed scanner ownership acceptance must not count as physical input progress.");
    }

    [Test]
    public void ScannerFilteredVirtualCandidateDoesNotCountPhysicalInputProgress()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        string path = "diagnostics-filtered-virtual";
        ScriptedHidDevice hid = new(path, PairingReport("A5:FA:9C:CF:92:00"));
        StubHidDeviceInfo info = new(path, ConnectionType.Bluetooth);
        ScriptedHidEnumerator enumerator = new((info, hid));
        TaskCompletionSource parsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DualSenseDevice? candidate = null;

        using ControllerScanner scanner = new(enumerator, deviceInfo =>
        {
            candidate = new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, diagnostics);
            candidate.InputReportReceived += (_, _) => parsed.TrySetResult();
            hid.Enqueue(BluetoothReport(crossPressed: true));
            Assert.That(parsed.Task.Wait(TimeSpan.FromSeconds(2)), Is.True,
                "the candidate should parse through its production reader before the scanner applies the virtual-device filter");
            return candidate;
        });

        Assert.That(scanner.Scan(), Is.Empty);
        Assert.That(candidate, Is.Not.Null);
        Assert.That(candidate!.IsConnected, Is.False, "the scanner should retire a known virtual wrapper");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.Zero);
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.Zero);
        });
    }

    [Test]
    public async Task StaleConnectionCandidateCannotCountAfterScannerSessionStops()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        using ManualResetEventSlim releaseFactory = new(false);
        string path = "diagnostics-stale-candidate";
        StubHidDeviceInfo info = new(path, ConnectionType.Bluetooth);
        ScriptedHidDevice hid = new(path);
        ScriptedHidEnumerator enumerator = new();
        TaskCompletionSource<DualSenseDevice> candidateCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource parsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DualSenseDevice? candidate = null;

        using ControllerScanner scanner = new(enumerator, deviceInfo =>
        {
            DualSenseDevice opened = new(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, diagnostics);
            candidate = opened;
            opened.InputReportReceived += (_, _) => parsed.TrySetResult();
            candidateCreated.TrySetResult(opened);
            Assert.That(releaseFactory.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the test must stop the scanner before this opened candidate returns for registration");
            return opened;
        });

        scanner.StartWatching();
        enumerator.QueueOpen(path, hid);
        Task connection = Task.Run(() => enumerator.RaiseConnected(info));
        try
        {
            candidate = await candidateCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await hid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
            hid.Enqueue(BluetoothReport(crossPressed: true));
            await parsed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            scanner.StopWatching();
            releaseFactory.Set();
            await connection.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(candidate.IsConnected, Is.False, "a candidate from the stopped watcher generation must be disposed");
            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.Zero);
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.Zero);
            });
        }
        finally
        {
            releaseFactory.Set();
            scanner.Dispose();
            await connection.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task ScannerCountsCurrentOwnerAcrossSamePathReplacementAndStopsCountingAfterStop()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        string path = "diagnostics-owner-transfer";
        StubHidDeviceInfo firstInfo = new(path, ConnectionType.Bluetooth);
        ScriptedHidDevice firstHid = new(path);
        ScriptedHidEnumerator enumerator = new((firstInfo, firstHid));
        using ControllerScanner scanner = new(
            enumerator,
            deviceInfo => new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, diagnostics));
        DualSenseDevice first = (DualSenseDevice)scanner.Scan().Single();
        using SemaphoreSlim firstReports = new(0);
        first.InputReportReceived += (_, _) => firstReports.Release();
        DualSenseDevice? replacement = null;
        using SemaphoreSlim replacementReports = new(0);
        TaskCompletionSource<DualSenseDevice> replacementConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.ControllerConnected += (_, args) =>
        {
            if (args.Controller is DualSenseDevice device)
            {
                replacement = device;
                device.InputReportReceived += (_, _) => replacementReports.Release();
                replacementConnected.TrySetResult(device);
            }
        };

        try
        {
            await firstHid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
            firstHid.Enqueue(BluetoothReport(crossPressed: false));
            await firstReports.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(1));

            scanner.StartWatching();
            enumerator.RaiseDisconnected(firstInfo);
            StubHidDeviceInfo replacementInfo = new(path, ConnectionType.Bluetooth);
            ScriptedHidDevice replacementHid = new(path);
            enumerator.QueueOpen(path, replacementHid);
            enumerator.SetCurrentDevices(replacementInfo);
            enumerator.RaiseConnected(replacementInfo);
            replacement = await replacementConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await replacementHid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

            firstHid.Enqueue(BluetoothReport(crossPressed: true));
            replacementHid.Enqueue(BluetoothReport(crossPressed: true));
            await Task.WhenAll(firstReports.WaitAsync(TimeSpan.FromSeconds(2)), replacementReports.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(2),
                    "the retired reader must not add to the current owner's read count");
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(2),
                    "the same-path replacement should contribute after scanner acceptance");
            });

            scanner.StopWatching();
            replacementHid.Enqueue(BluetoothReport(crossPressed: false));
            await replacementReports.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(2));
                Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(2));
            });
        }
        finally
        {
            scanner.StopWatching();
            first.Dispose();
            replacement?.Dispose();
        }
    }

    [Test]
    public async Task ScannerDisposeRetiresPhysicalProgressForTheCurrentAcceptedSource()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        string path = "diagnostics-disposed-scanner";
        ScriptedHidDevice hid = new(path);
        StubHidDeviceInfo info = new(path, ConnectionType.Bluetooth);
        ScriptedHidEnumerator enumerator = new((info, hid));
        using ControllerScanner scanner = new(
            enumerator,
            deviceInfo => new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, diagnostics));
        using DualSenseDevice physical = (DualSenseDevice)scanner.Scan().Single();
        using SemaphoreSlim reports = new(0);
        physical.InputReportReceived += (_, _) => reports.Release();

        await hid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        hid.Enqueue(BluetoothReport(crossPressed: false));
        await reports.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(1));

        scanner.Dispose();
        hid.Enqueue(BluetoothReport(crossPressed: true));
        await reports.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(1));
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalFullReportParsed), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ManualSession_ObservesExistingReaderManagedRouteAndVirtualSubmitThenUsesNewestSession()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            ScriptedHidDevice hid = new();
            StubHidDeviceInfo info = new(hid.DevicePath, ConnectionType.Bluetooth);
            ScriptedHidEnumerator enumerator = new((info, hid));
            using ControllerScanner scanner = new(
                enumerator,
                deviceInfo => new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, router));
            using DualSenseDevice physical = (DualSenseDevice)scanner.Scan().Single();
            ConcurrentQueue<InputReport> receivedReports = new();
            using SemaphoreSlim reportReceived = new(0);
            ConcurrentQueue<bool> submitResults = new(new[] { true, true, false, true });
            using VirtualDualSenseController virtualController = new(
                new NoOpOutputs(),
                router,
                (_, state) =>
                {
                    submitResults.TryDequeue(out bool succeeded);
                    return succeeded;
                });

            physical.InputReportReceived += (_, report) =>
            {
                EmulationService.DispatchInputReport(router, virtualController, report);
                receivedReports.Enqueue(report);
                reportReceived.Release();
            };

            await hid.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
            hid.Enqueue(BluetoothReport(crossPressed: true));
            await WaitForReportAsync(receivedReports, reportReceived);
            Assert.That(Directory.Exists(Path.Combine(root, "Logs")), Is.False,
                "a scanner-accepted device receiving input while diagnostics are off must not create output");

            NativeAudioTimingDiagnosticsStatus firstStart = await control.StartAsync(3);
            Assert.That(firstStart.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));

            hid.Enqueue([0x01]);
            hid.Enqueue(BluetoothReport(crossPressed: true));
            hid.Enqueue(BluetoothReport(crossPressed: false));
            await WaitForReportAsync(receivedReports, reportReceived);
            await WaitForReportAsync(receivedReports, reportReceived);
            EmulationService.DispatchInputReport(router, null, InputReportTestData.CreateReport());

            await control.StopAsync();
            string firstLog = File.ReadAllText(firstStart.LogPath!);
            Assert.Multiple(() =>
            {
                Assert.That(firstLog, Does.Contain("physical_read_ok=3"));
                Assert.That(firstLog, Does.Contain("physical_compact_reports=1"));
                Assert.That(firstLog, Does.Contain("physical_full_parsed=2"));
                Assert.That(firstLog, Does.Contain("physical_control_changes=1"));
                Assert.That(firstLog, Does.Contain("physical_parse_error=0"));
                Assert.That(firstLog, Does.Contain("emulation_callback=3"));
                Assert.That(firstLog, Does.Contain("emulation_no_target=1"));
                Assert.That(firstLog, Does.Contain("emulation_forwarded=2"));
                Assert.That(firstLog, Does.Contain("virtual_push_calls=2"));
                Assert.That(firstLog, Does.Contain("virtual_submit_attempts=2"));
                Assert.That(firstLog, Does.Contain("virtual_submit_success=1"));
                Assert.That(firstLog, Does.Contain("virtual_submit_error=1"));
                Assert.That(firstLog, Does.Not.Contain("hid="));
                Assert.That(firstLog, Does.Not.Contain("button="));
                Assert.That(firstLog, Does.Not.Contain("pcm="));
            });

            NativeAudioTimingDiagnosticsStatus secondStart = await control.StartAsync(3);
            Assert.That(secondStart.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
            hid.Enqueue(BluetoothReport(crossPressed: true));
            await WaitForReportAsync(receivedReports, reportReceived);
            await control.StopAsync();

            string secondLog = File.ReadAllText(secondStart.LogPath!);
            Assert.Multiple(() =>
            {
                Assert.That(secondLog, Does.Contain("physical_read_ok=1"));
                Assert.That(secondLog, Does.Contain("physical_full_parsed=1"));
                Assert.That(secondLog, Does.Contain("physical_control_changes=1"));
                Assert.That(secondLog, Does.Contain("emulation_callback=1"));
                Assert.That(secondLog, Does.Contain("emulation_forwarded=1"));
                Assert.That(secondLog, Does.Contain("virtual_push_calls=1"));
                Assert.That(secondLog, Does.Contain("virtual_submit_success=1"));
                Assert.That(secondLog, Does.Contain("virtual_submit_error=0"));
                Assert.That(secondLog, Does.Not.Contain("physical_compact_reports=1"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReadFailureEndAndParserFailureAreCountedAtTheirOwnBoundaries()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            ScriptedHidDevice failedReadHid = new("diagnostics-failed-read");
            ScriptedHidDevice endedReadHid = new("diagnostics-ended-read");
            ScriptedHidDevice blockedReadHid = new("diagnostics-parser");
            StubHidDeviceInfo failedInfo = new(failedReadHid.DevicePath, ConnectionType.Usb);
            StubHidDeviceInfo endedInfo = new(endedReadHid.DevicePath, ConnectionType.Usb);
            StubHidDeviceInfo parserInfo = new(blockedReadHid.DevicePath, ConnectionType.Usb);
            ScriptedHidEnumerator enumerator = new((failedInfo, failedReadHid), (endedInfo, endedReadHid), (parserInfo, blockedReadHid));
            using ControllerScanner scanner = new(
                enumerator,
                deviceInfo => new DualSenseDevice(enumerator.OpenDevice(deviceInfo.Path), deviceInfo, router));
            IReadOnlyList<IControllerDevice> accepted = scanner.Scan();
            using DualSenseDevice failedRead = (DualSenseDevice)accepted.Single(device => device.Info.Path == failedInfo.Path);
            using DualSenseDevice endedRead = (DualSenseDevice)accepted.Single(device => device.Info.Path == endedInfo.Path);
            using DualSenseDevice parser = (DualSenseDevice)accepted.Single(device => device.Info.Path == parserInfo.Path);
            TaskCompletionSource failedReadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource endedReadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            failedRead.InputReaderEndedUnexpectedly += (_, _) => failedReadEnded.TrySetResult();
            endedRead.InputReaderEndedUnexpectedly += (_, _) => endedReadEnded.TrySetResult();

            await Task.WhenAll(failedReadHid.ReadStarted, endedReadHid.ReadStarted, blockedReadHid.ReadStarted)
                .WaitAsync(TimeSpan.FromSeconds(2));
            NativeAudioTimingDiagnosticsStatus started = await control.StartAsync(3);
            Assert.That(started.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));

            failedReadHid.Fail(new HidException("synthetic read failure"));
            endedReadHid.End();
            TargetInvocationException? parseFailure = null;
            try
            {
                ProcessInputReportCoreMethod.Invoke(parser, [new byte[] { 0x01 }, 1]);
            }
            catch (TargetInvocationException ex)
            {
                parseFailure = ex;
            }

            Assert.That(parseFailure?.InnerException, Is.TypeOf<IndexOutOfRangeException>());
            await Task.WhenAll(failedReadEnded.Task, endedReadEnded.Task).WaitAsync(TimeSpan.FromSeconds(2));
            await control.StopAsync();

            string log = File.ReadAllText(started.LogPath!);
            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain("physical_read_error=1"));
                Assert.That(log, Does.Contain("physical_read_end=1"));
                Assert.That(log, Does.Contain("physical_parse_error=1"));
                Assert.That(log, Does.Contain("physical_read_ok=0"));
                Assert.That(log, Does.Contain("physical_full_parsed=0"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static NativeAudioTimingDiagnosticsManager CreateManager(string root) =>
        NativeAudioTimingDiagnosticsManager.CreateForTesting(
            root,
            Stopwatch.GetTimestamp,
            () => "attempt56-test",
            () => Guid.NewGuid().ToString("N"),
            testClock: false,
            startPublisher: false);

    private static async Task WaitForReportAsync(
        ConcurrentQueue<InputReport> receivedReports,
        SemaphoreSlim reportReceived)
    {
        Assert.That(await reportReceived.WaitAsync(TimeSpan.FromSeconds(2)), Is.True,
            "the physical reader should publish the queued complete report");
        Assert.That(receivedReports.TryDequeue(out _), Is.True);
    }

    private static byte[] BluetoothReport(bool crossPressed)
    {
        byte[] usbReport = (byte[])InputReportTestData.UsbReport.Clone();
        usbReport[8] = crossPressed ? (byte)0xF2 : (byte)0xD2;
        byte[] bluetoothReport = new byte[78];
        bluetoothReport[0] = 0x31;
        bluetoothReport[1] = 0x00;
        Buffer.BlockCopy(usbReport, 1, bluetoothReport, 2, usbReport.Length - 1);
        return bluetoothReport;
    }

    private static byte[] PairingReport(string clientMac)
    {
        byte[] report = new byte[20];
        report[0] = 0x09;
        string[] octets = clientMac.Split(':');
        for (int index = 0; index < 6; index++)
        {
            report[index + 1] = Convert.ToByte(octets[5 - index], 16);
        }

        return report;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "attempt56-input-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
