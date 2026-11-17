using System.Reflection;
using DualSenseClient.Controllers.Devices;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Utilities;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Hid;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.DualSense;

public class DualSenseDeviceOutputTests
{
    private static readonly MethodInfo ProcessInputReportMethod = typeof(DualSenseDevice)
        .GetMethod("ProcessInputReport", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private sealed class CapturingHidDevice : IHidDevice
    {
        public byte[]? LastWrite { get; private set; }

        public List<byte[]> Writes { get; } = [];

        public ManualResetEventSlim FirstWriteEntered { get; } = new ManualResetEventSlim();

        public ManualResetEventSlim? FirstWriteRelease { get; set; }

        public List<byte> FeatureReportIds { get; } = [];

        public int LastWriteOffset { get; private set; }

        public int LastWriteCount { get; private set; }

        public int WriteCount { get; private set; }

        public bool FailWrites { get; set; }

        public ushort VendorId
        {
            get
            {
                return 0x054C;
            }
        }

        public ushort ProductId
        {
            get
            {
                return 0x0CE6;
            }
        }

        public string DevicePath
        {
            get
            {
                return "test";
            }
        }

        public bool IsConnected
        {
            get
            {
                return true;
            }
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => 0;

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);

        public int Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites)
            {
                throw new IOException("simulated write failure");
            }

            if (WriteCount == 0 && FirstWriteRelease is { } release)
            {
                FirstWriteEntered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
            }

            LastWrite = (byte[])buffer.Clone();
            Writes.Add(buffer[offset..(offset + count)]);
            LastWriteOffset = offset;
            LastWriteCount = count;
            WriteCount++;
            return count;
        }

        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64)
        {
            FeatureReportIds.Add(reportId);
            return reportId == 0x05 ? [reportId] : [];
        }

        public void SendFeatureReport(byte[] buffer, int offset, int count)
        {
        }

        public string GetProductName() => "Test";

        public void Dispose()
        {
        }
    }

    private sealed class DisposingHidDevice : IHidDevice
    {
        private bool _disposed;

        public ushort VendorId
        {
            get
            {
                return 0x054C;
            }
        }

        public ushort ProductId
        {
            get
            {
                return 0x0CE6;
            }
        }

        public string DevicePath
        {
            get
            {
                return "test";
            }
        }

        public bool IsConnected
        {
            get
            {
                return !_disposed;
            }
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => 0;

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);

        public int Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return count;
        }

        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64) => [];

        public void SendFeatureReport(byte[] buffer, int offset, int count)
        {
        }

        public string GetProductName() => "Test";

        public void Dispose() => _disposed = true;
    }

    private sealed class StubHidDeviceInfo(ConnectionType busType) : IHidDeviceInfo
    {
        public string Path
        {
            get
            {
                return "test";
            }
        }

        public ushort VendorId
        {
            get
            {
                return 0x054C;
            }
        }

        public ushort ProductId
        {
            get
            {
                return 0x0CE6;
            }
        }

        public string ProductName
        {
            get
            {
                return "DualSense Test";
            }
        }

        public string Manufacturer
        {
            get
            {
                return "Sony";
            }
        }

        public int InterfaceNumber
        {
            get
            {
                return 0;
            }
        }

        public ushort UsagePage
        {
            get
            {
                return 1;
            }
        }

        public HidUsageId Usage
        {
            get
            {
                return HidUsageId.GamePad;
            }
        }

        public ConnectionType BusType
        {
            get
            {
                return busType;
            }
        }
    }

    [Test]
    public void Usb_SendOutputState_FramesAsReport02()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Usb));

        device.SendOutputState(new SetStateData
        {
            RumbleRight = 0x77
        });

        Assert.Multiple(() =>
        {
            Assert.That(hid.LastWriteOffset, Is.EqualTo(0));
            Assert.That(hid.LastWriteCount, Is.EqualTo(48));
            Assert.That(hid.LastWrite![0], Is.EqualTo(0x02));
            Assert.That(hid.LastWrite[1 + 2], Is.EqualTo(0x77));
        });
    }

    [Test]
    public void Bluetooth_SendOutputState_IncludesSequenceTagFlagsAndCrc()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        device.SendOutputState(new SetStateData
        {
            RumbleRight = 1
        });
        device.SendOutputState(new SetStateData
        {
            RumbleRight = 2
        });

        uint expected = DualSenseCRC32.Compute(hid.LastWrite!, 0, 74);
        uint actual = (uint)(hid.LastWrite![74]
                             | (hid.LastWrite[75] << 8)
                             | (hid.LastWrite[76] << 16)
                             | (hid.LastWrite[77] << 24));

        Assert.Multiple(() =>
        {
            Assert.That(hid.LastWriteCount, Is.EqualTo(78));
            Assert.That(hid.LastWrite[0], Is.EqualTo(0x31));
            Assert.That(hid.LastWrite[1], Is.EqualTo(0x10)); // second call uses sequence 1
            Assert.That(hid.LastWrite[2], Is.EqualTo(0x10));
            Assert.That(hid.LastWrite[3 + 2], Is.EqualTo(2));
            Assert.That(hid.LastWrite[50..74], Is.All.EqualTo(0));
            Assert.That(actual, Is.EqualTo(expected));
        });
    }

    [Test]
    public void Bluetooth_Connect_PrimesEnhancedInputReport()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        Assert.That(hid.FeatureReportIds, Is.EqualTo(new byte[] { 0x05, 0x20, 0x09 }));
    }

    [Test]
    public void Bluetooth_SimpleInputReport_WakesFullReportMode()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        ProcessInputReportMethod.Invoke(device, [new byte[] { 0x01 }]);

        uint expectedCrc = DualSenseCRC32.Compute(hid.LastWrite!, 0, 74);
        uint actualCrc = (uint)(hid.LastWrite![74]
                                | (hid.LastWrite[75] << 8)
                                | (hid.LastWrite[76] << 16)
                                | (hid.LastWrite[77] << 24));

        Assert.Multiple(() =>
        {
            Assert.That(hid.LastWriteCount, Is.EqualTo(78));
            Assert.That(hid.LastWrite![0..3], Is.EqualTo(new byte[] { 0x31, 0x00, 0x10 }));
            Assert.That(hid.LastWrite[3..74], Is.EqualTo(new byte[71]));
            Assert.That(actualCrc, Is.EqualTo(expectedCrc));
        });
    }

    [Test]
    public void Bluetooth_VirtualTriggerOutput_ReachesPhysicalReport()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        byte[] raw = new byte[48];
        raw[0] = 0x02;
        raw[1] = 0x0C; // allow both adaptive-trigger blocks
        Buffer.BlockCopy(new byte[] { 0x25, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0, raw, 11, 11);
        Buffer.BlockCopy(new byte[] { 0x26, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }, 0, raw, 22, 11);

        DSOutputState output = new DSOutputState
        {
            RawOutputReport = raw,
            BluetoothCombinedOutputReport = new byte[398]
        };
        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(output, vibrationV2: false);

        device.SendOutputState(payload);

        uint expectedCrc = DualSenseCRC32.Compute(hid.LastWrite!, 0, 74);
        uint actualCrc = (uint)(hid.LastWrite![74]
                                | (hid.LastWrite[75] << 8)
                                | (hid.LastWrite[76] << 16)
                                | (hid.LastWrite[77] << 24));

        Assert.Multiple(() =>
        {
            Assert.That(hid.LastWriteCount, Is.EqualTo(78));
            Assert.That(hid.LastWrite[0], Is.EqualTo(0x31));
            Assert.That(hid.LastWrite[3], Is.EqualTo(0x0C));
            Assert.That(hid.LastWrite[13..24], Is.EqualTo(raw[11..22]));
            Assert.That(hid.LastWrite[24..35], Is.EqualTo(raw[22..33]));
            Assert.That(actualCrc, Is.EqualTo(expectedCrc));
        });
    }

    [Test]
    public void Bluetooth_AudioAndOutputWritesShareOnePerDeviceOwner()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        hid.FirstWriteRelease = release;
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        Task audio = Task.Run(() => device.SendBluetoothAudioAndHaptics(
            new SetStateData(), new byte[200], new byte[64], BluetoothAudioRoute.Speaker));
        Assert.That(hid.FirstWriteEntered.Wait(TimeSpan.FromSeconds(1)), Is.True,
            "the audio writer must reach the fake HID write before the contention check");

        Task output = Task.Run(() => device.SendOutputState(new SetStateData
        {
            RumbleRight = 0x55
        }));
        Assert.That(SpinWait.SpinUntil(() => output.IsCompleted, TimeSpan.FromMilliseconds(100)), Is.False,
            "a normal output write must wait for the in-flight audio report on the same device");

        release.Set();
        Assert.DoesNotThrow(() => Task.WaitAll(audio, output));

        Assert.That(hid.Writes, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(hid.Writes[0][0], Is.EqualTo(0x36), "the combined audio report must be written atomically");
            Assert.That(hid.Writes[1][0], Is.EqualTo(0x31), "the normal output report follows the audio report");
        });
    }

    [Test]
    public void Bluetooth_SequenceTag_IncrementsThenWraps()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        for (int i = 0; i < 16; i++)
        {
            // Every payload differs so the dedupe does not suppress the sends.
            device.SendOutputState(new SetStateData
            {
                RumbleRight = (byte)(i + 1)
            });
        }

        Assert.That(hid.LastWrite![1], Is.EqualTo(0xF0)); // 16th send → sequence 15

        device.SendOutputState(new SetStateData
        {
            RumbleRight = 17
        });
        Assert.That(hid.LastWrite[1], Is.EqualTo(0x00)); // wraps back to sequence 0
    }

    [Test]
    public void Bluetooth_DuplicatePayload_IsSkipped()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));
        SetStateData payload = new SetStateData
        {
            RumbleRight = 0x55,
            LedRed = 0x10,
            LedGreen = 0x20,
            LedBlue = 0x30
        };

        device.SendOutputState(payload);
        device.SendOutputState(payload);
        device.SendOutputState(payload);

        Assert.That(hid.WriteCount, Is.EqualTo(1), "byte-identical Bluetooth reports must be suppressed");
    }

    [Test]
    public void Bluetooth_ChangedPayload_IsSent()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        device.SendOutputState(new SetStateData
        {
            RumbleRight = 0x55
        });
        device.SendOutputState(new SetStateData
        {
            RumbleRight = 0x55,
            RumbleLeft = 0x11
        });

        Assert.That(hid.WriteCount, Is.EqualTo(2));
    }

    [Test]
    public void Bluetooth_FailedSendIsNotDedupedAway()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Bluetooth));

        SetStateData payload = new SetStateData
        {
            RumbleRight = 0x55
        };
        hid.FailWrites = true;
        Assert.Throws<IOException>(() => device.SendOutputState(payload));

        hid.FailWrites = false;
        device.SendOutputState(payload);

        Assert.That(hid.WriteCount, Is.EqualTo(1), "a payload whose send failed must be retried instead of deduped");
    }

    [Test]
    public void Usb_DuplicatePayload_IsStillSent()
    {
        CapturingHidDevice hid = new CapturingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Usb));
        SetStateData payload = new SetStateData
        {
            RumbleRight = 0x55
        };

        device.SendOutputState(payload);
        device.SendOutputState(payload);

        Assert.That(hid.WriteCount, Is.EqualTo(2), "the dedupe must only apply to the bandwidth-constrained Bluetooth transport");
    }

    [Test]
    public void SendOutputState_AfterDispose_ThrowsHidException()
    {
        DisposingHidDevice hid = new DisposingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Usb));
        hid.Dispose();

        Assert.Throws<HidException>(() => device.SendOutputState(new SetStateData()));
    }

    [Test]
    public void SetVibration_AfterDispose_DoesNotThrow()
    {
        DisposingHidDevice hid = new DisposingHidDevice();
        using DualSenseDevice device = new DualSenseDevice(hid, new StubHidDeviceInfo(ConnectionType.Usb));
        hid.Dispose();

        Assert.DoesNotThrow(() => device.SetVibration(0, 0));
    }
}
