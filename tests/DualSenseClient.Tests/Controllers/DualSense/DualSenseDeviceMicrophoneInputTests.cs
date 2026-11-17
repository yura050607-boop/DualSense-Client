using System.Buffers.Binary;
using System.Reflection;
using Concentus;
using Concentus.Enums;
using DualSenseClient.Controllers.Devices;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Hid;

namespace DualSenseClient.Tests.Controllers.DualSense;

public sealed class DualSenseDeviceMicrophoneInputTests
{
    private sealed class StubHidDevice : IHidDevice
    {
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string DevicePath => "mic-test";
        public bool IsConnected => true;
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => 0;
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);
        public int Write(byte[] buffer, int offset, int count) => count;
        public byte[] GetFeatureReport(byte reportId, int bufferSize = 64) => [];
        public void SendFeatureReport(byte[] buffer, int offset, int count)
        {
        }

        public string GetProductName() => "DualSense microphone test";
        public void Dispose()
        {
        }
    }

    private sealed class StubHidDeviceInfo : IHidDeviceInfo
    {
        public string Path => "mic-test";
        public ushort VendorId => 0x054C;
        public ushort ProductId => 0x0CE6;
        public string ProductName => "DualSense microphone test";
        public string Manufacturer => "Sony";
        public int InterfaceNumber => 0;
        public ushort UsagePage => 1;
        public HidUsageId Usage => HidUsageId.GamePad;
        public ConnectionType BusType => ConnectionType.Bluetooth;
    }

    private static readonly MethodInfo ProcessInputReport = typeof(DualSenseDevice)
        .GetMethod("ProcessInputReport", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Test]
    public void ValidMicAndMalformedCandidatesAreConsumedBeforeNormalInputDispatch()
    {
        using DualSenseDevice device = new DualSenseDevice(new StubHidDevice(), new StubHidDeviceInfo());
        int normalReports = 0;
        byte[]? receivedPcm = null;
        device.InputReportReceived += (_, _) => normalReports++;
        device.MicrophonePcmReceived += frame => receivedPcm = frame.ToArray();

        byte[] micReport = BuildFixture(out byte[] expectedPcm);
        ProcessInputReport.Invoke(device, [micReport]);

        byte[] malformedCandidate = new byte[78];
        malformedCandidate[0] = 0x31;
        malformedCandidate[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        malformedCandidate[3] = 0xD4;
        ProcessInputReport.Invoke(device, [malformedCandidate]);

        byte[] normalReport = new byte[BluetoothMicFrameDecoder.BluetoothReportBytes];
        normalReport[0] = BluetoothMicFrameDecoder.InputReportId;
        normalReport[1] = BluetoothMicFrameDecoder.NormalInputFlag;
        normalReport[2] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(normalReport.AsSpan(^4),
            BluetoothMicFrameDecoder.ComputeInputCrc32(normalReport.AsSpan(0, normalReport.Length - 4)));
        ProcessInputReport.Invoke(device, [normalReport]);

        Assert.Multiple(() =>
        {
            Assert.That(receivedPcm, Is.EqualTo(expectedPcm));
            Assert.That(receivedPcm, Has.Length.EqualTo(1920));
            Assert.That(normalReports, Is.EqualTo(1), "mic and malformed candidates must not reach gamepad dispatch");
        });
    }

    [Test]
    public void EveryNormalLeftStickYValueRemainsGamepadInputEvenWhenItIsD4()
    {
        using DualSenseDevice device = new DualSenseDevice(new StubHidDevice(), new StubHidDeviceInfo());
        int normalReports = 0;
        device.InputReportReceived += (_, _) => normalReports++;

        for (int leftStickY = 0; leftStickY <= byte.MaxValue; leftStickY++)
        {
            byte[] normalReport = new byte[78];
            normalReport[0] = BluetoothMicFrameDecoder.InputReportId;
            normalReport[1] = BluetoothMicFrameDecoder.NormalInputFlag;
            normalReport[2] = 0x80;
            normalReport[3] = (byte)leftStickY;
            BinaryPrimitives.WriteUInt32LittleEndian(normalReport.AsSpan(^4),
                BluetoothMicFrameDecoder.ComputeInputCrc32(normalReport.AsSpan(0, normalReport.Length - 4)));
            ProcessInputReport.Invoke(device, [normalReport]);
        }

        Assert.That(normalReports, Is.EqualTo(256));
    }

    [Test]
    public void ConnectionMicMuteStatusSuppressesMicFramesUntilThePhysicalButtonClears()
    {
        using DualSenseDevice device = new DualSenseDevice(new StubHidDevice(), new StubHidDeviceInfo());
        List<bool> muteChanges = [];
        byte[]? receivedPcm = null;
        device.MicrophoneMuteChanged += muteChanges.Add;
        device.MicrophonePcmReceived += frame => receivedPcm = frame.ToArray();
        byte[] micReport = BuildFixture(out byte[] expectedPcm);

        byte[] mutedState = new byte[BluetoothMicFrameDecoder.BluetoothReportBytes];
        mutedState[0] = BluetoothMicFrameDecoder.InputReportId;
        mutedState[1] = BluetoothMicFrameDecoder.NormalInputFlag;
        mutedState[2] = 0x80;
        mutedState[55] = 0x06; // microphone present + physical mute bit
        BinaryPrimitives.WriteUInt32LittleEndian(mutedState.AsSpan(^4),
            BluetoothMicFrameDecoder.ComputeInputCrc32(mutedState.AsSpan(0, mutedState.Length - 4)));
        ProcessInputReport.Invoke(device, [mutedState]);
        ProcessInputReport.Invoke(device, [micReport]);

        byte[] unmutedState = (byte[])mutedState.Clone();
        unmutedState[55] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(unmutedState.AsSpan(^4),
            BluetoothMicFrameDecoder.ComputeInputCrc32(unmutedState.AsSpan(0, unmutedState.Length - 4)));
        ProcessInputReport.Invoke(device, [unmutedState]);
        ProcessInputReport.Invoke(device, [micReport]);

        Assert.Multiple(() =>
        {
            Assert.That(muteChanges, Is.EqualTo([true, false]));
            Assert.That(receivedPcm, Is.EqualTo(expectedPcm));
            Assert.That(device.MicrophoneMuted, Is.False);
        });
    }

    private static byte[] BuildFixture(out byte[] expectedPcm)
    {
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        encoder.Bitrate = BluetoothMicFrameDecoder.OpusPayloadBytes * 8 * 100;
        encoder.UseVBR = false;
        encoder.Complexity = 0;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND;

        short[] input = new short[960];
        for (int i = 0; i < 480; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 330 * i / 48000) * 6000);
            input[i * 2] = sample;
            input[i * 2 + 1] = sample;
        }

        byte[] opus = new byte[BluetoothMicFrameDecoder.OpusPayloadBytes];
        Assert.That(encoder.Encode(input, 480, opus, opus.Length), Is.EqualTo(opus.Length));
        Assert.That(opus[0], Is.EqualTo(0xD4));

        using IOpusDecoder expectedDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        short[] decoded = new short[480];
        Assert.That(expectedDecoder.Decode(opus, decoded, 5760, false), Is.EqualTo(480));
        expectedPcm = new byte[1920];
        Span<short> stereo = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(expectedPcm.AsSpan());
        for (int sample = 0; sample < 480; sample++)
        {
            stereo[sample * 2] = decoded[sample];
            stereo[sample * 2 + 1] = decoded[sample];
        }

        byte[] report = new byte[BluetoothMicFrameDecoder.BluetoothReportBytes];
        report[0] = BluetoothMicFrameDecoder.InputReportId;
        report[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        opus.CopyTo(report.AsSpan(3));
        BinaryPrimitives.WriteUInt32LittleEndian(
            report.AsSpan(^4),
            DualSenseClient.Controllers.DualSense.Audio.BluetoothMicFrameDecoder.ComputeInputCrc32(report.AsSpan(0, report.Length - 4)));
        return report;
    }
}
