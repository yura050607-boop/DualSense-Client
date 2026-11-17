using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Utilities;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public sealed class BluetoothMicFrameDecoderTests
{
    [Test]
    public void ValidD4OpusFrame_DecodesExactStereoPcmFrame()
    {
        byte[] report = BuildFixture(out byte[] expectedPcm);
        byte[] actualPcm = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];

        using BluetoothMicFrameDecoder decoder = new BluetoothMicFrameDecoder();
        bool decoded = decoder.TryDecode(report, actualPcm, out int samples);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(samples, Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
            Assert.That(actualPcm, Is.EqualTo(expectedPcm));
            Assert.That(actualPcm.Any(value => value != 0), Is.True);
        });
    }

    [Test]
    public void InvalidCrcAndMalformedCandidates_AreRejectedBeforePcmOutput()
    {
        byte[] report = BuildFixture(out _);
        using BluetoothMicFrameDecoder decoder = new BluetoothMicFrameDecoder();
        byte[] output = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];

        byte[] badInputCrc = (byte[])report.Clone();
        badInputCrc[^1] ^= 0x01;
        byte[] outputSeededCrc = (byte[])report.Clone();
        uint a2 = DualSenseCRC32.Compute(outputSeededCrc, 0, outputSeededCrc.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(outputSeededCrc.AsSpan(^4), a2);
        byte[] truncated = report[..^1];
        byte[] markerOnly = new byte[32];
        markerOnly[0] = BluetoothMicFrameDecoder.InputReportId;
        markerOnly[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        markerOnly[3] = BluetoothMicFrameDecoder.OpusMarker;

        Assert.Multiple(() =>
        {
            Assert.That(decoder.TryDecode(badInputCrc, output, out _), Is.False);
            Assert.That(decoder.TryDecode(outputSeededCrc, output, out _), Is.False);
            Assert.That(decoder.TryDecode(truncated, output, out _), Is.False);
            Assert.That(decoder.TryDecode(markerOnly, output, out _), Is.False);
        });
    }

    [Test]
    public void ClassifierSeparatesNormalBluetoothStateFromMicCandidate()
    {
        byte[] normalState = new byte[78];
        normalState[0] = BluetoothMicFrameDecoder.InputReportId;
        normalState[1] = BluetoothMicFrameDecoder.NormalInputFlag;
        normalState[3] = BluetoothMicFrameDecoder.OpusMarker;
        BinaryPrimitives.WriteUInt32LittleEndian(normalState.AsSpan(^4),
            BluetoothMicFrameDecoder.ComputeInputCrc32(normalState.AsSpan(0, normalState.Length - 4)));

        byte[] malformedMic = new byte[78];
        malformedMic[0] = BluetoothMicFrameDecoder.InputReportId;
        malformedMic[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        malformedMic[3] = BluetoothMicFrameDecoder.OpusMarker;

        Assert.Multiple(() =>
        {
            Assert.That(BluetoothMicFrameDecoder.LooksLikeMicCandidate(normalState), Is.False);
            Assert.That(BluetoothMicFrameDecoder.LooksLikeMicCandidate(malformedMic), Is.True);
            Assert.That(BluetoothMicFrameDecoder.HasMicEnvelope(malformedMic), Is.True);
        });

        byte[] truncatedMic = malformedMic[..20];
        Assert.That(BluetoothMicFrameDecoder.LooksLikeMicCandidate(truncatedMic), Is.True,
            "the header bit must classify a truncated mic report before normal parsing");
    }

    [Test]
    public void ContinuousSignal_AdvancingDecoderAcrossMutePreservesPredictionState()
    {
        byte[][] reports = BuildContinuousReports(7);
        byte[] referenceFrame = DecodeReferenceFrameAt(reports, frameIndex: 6);
        byte[] advancingFrame = DecodeFrameAt(
            reports, resetBeforeFrame: -1, frameIndex: 6, skipMutedFrames: false);
        byte[] resetFrame = DecodeFrameAt(
            reports, resetBeforeFrame: 3, frameIndex: 6, skipMutedFrames: true);

        double advancingMae = MeanAbsoluteDifference(referenceFrame, advancingFrame);
        double resetMae = MeanAbsoluteDifference(referenceFrame, resetFrame);
        TestContext.Progress.WriteLine(
            $"continuous_mute_prediction reference_vs_advance_mae={advancingMae:F3} "
            + $"reference_vs_reset_mae={resetMae:F3} frames={reports.Length} "
            + $"frame_samples={BluetoothMicFrameDecoder.PcmFrameSamples}");

        Assert.That(advancingMae, Is.EqualTo(0).Within(0.001),
            "decoding every physical frame must preserve the Opus prediction state");
    }

    private static byte[] DecodeReferenceFrameAt(byte[][] reports, int frameIndex)
    {
        using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        short[] decoded = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 12];
        byte[]? result = null;
        for (int index = 0; index <= frameIndex; index++)
        {
            int samples = decoder.Decode(
                reports[index].AsSpan(3, BluetoothMicFrameDecoder.OpusPayloadBytes),
                decoded,
                decoded.Length,
                false);
            Assert.That(samples, Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
            if (index == frameIndex)
            {
                result = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];
                Span<short> stereo = System.Runtime.InteropServices.MemoryMarshal
                    .Cast<byte, short>(result.AsSpan());
                for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
                {
                    stereo[sample * 2] = decoded[sample];
                    stereo[sample * 2 + 1] = decoded[sample];
                }
            }
        }

        return result!;
    }

    private static byte[] DecodeFrameAt(
        byte[][] reports,
        int resetBeforeFrame,
        int frameIndex,
        bool skipMutedFrames)
    {
        using BluetoothMicFrameDecoder decoder = new BluetoothMicFrameDecoder();
        byte[] output = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];
        for (int index = 0; index <= frameIndex; index++)
        {
            if (index == resetBeforeFrame)
            {
                decoder.Reset();
            }

            if (skipMutedFrames && index >= resetBeforeFrame && index < frameIndex)
            {
                continue;
            }

            bool decoded = decoder.TryDecode(reports[index], output, out int samples);
            Assert.That(decoded, Is.True, $"continuous frame {index} must decode");
            Assert.That(samples, Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
        }

        return output.ToArray();
    }

    private static byte[][] BuildContinuousReports(int frameCount)
    {
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            48000,
            2,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        encoder.Bitrate = BluetoothMicFrameDecoder.OpusPayloadBytes * 8 * 100;
        encoder.UseVBR = false;
        encoder.Complexity = 0;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND;

        byte[][] reports = new byte[frameCount][];
        short[] input = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
            {
                int absoluteSample = frame * BluetoothMicFrameDecoder.PcmFrameSamples + sample;
                short value = (short)(Math.Sin(2 * Math.PI * 440 * absoluteSample / 48000) * 7000);
                input[sample * 2] = value;
                input[sample * 2 + 1] = value;
            }

            byte[] opus = new byte[BluetoothMicFrameDecoder.OpusPayloadBytes];
            Assert.That(encoder.Encode(
                input,
                BluetoothMicFrameDecoder.PcmFrameSamples,
                opus,
                opus.Length), Is.EqualTo(opus.Length));
            Assert.That(opus[0], Is.EqualTo(BluetoothMicFrameDecoder.OpusMarker));
            reports[frame] = BuildReport(opus);
        }

        return reports;
    }

    private static byte[] BuildReport(byte[] opus)
    {
        byte[] report = new byte[3 + opus.Length + 4];
        report[0] = BluetoothMicFrameDecoder.InputReportId;
        report[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        opus.CopyTo(report.AsSpan(3));
        uint crc = BluetoothMicFrameDecoder.ComputeInputCrc32(report.AsSpan(0, report.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(^4), crc);
        return report;
    }

    private static double MeanAbsoluteDifference(byte[] left, byte[] right)
    {
        Assert.That(left.Length, Is.EqualTo(right.Length));
        long total = 0;
        int sampleCount = left.Length / sizeof(short);
        for (int index = 0; index < left.Length; index += sizeof(short))
        {
            short first = BinaryPrimitives.ReadInt16LittleEndian(left.AsSpan(index, sizeof(short)));
            short second = BinaryPrimitives.ReadInt16LittleEndian(right.AsSpan(index, sizeof(short)));
            total += Math.Abs(first - second);
        }

        return sampleCount == 0 ? 0 : (double)total / sampleCount;
    }

    private static byte[] BuildFixture(out byte[] expectedPcm)
    {
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            48000,
            2,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        encoder.Bitrate = BluetoothMicFrameDecoder.OpusPayloadBytes * 8 * 100;
        encoder.UseVBR = false;
        encoder.Complexity = 0;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND;

        short[] input = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 2];
        for (int i = 0; i < BluetoothMicFrameDecoder.PcmFrameSamples; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 7000);
            input[i * 2] = sample;
            input[i * 2 + 1] = sample;
        }

        byte[] opus = new byte[BluetoothMicFrameDecoder.OpusPayloadBytes];
        Assert.That(encoder.Encode(input, BluetoothMicFrameDecoder.PcmFrameSamples, opus, opus.Length), Is.EqualTo(opus.Length));
        Assert.That(opus[0], Is.EqualTo(BluetoothMicFrameDecoder.OpusMarker));

        using IOpusDecoder expectedDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        short[] expectedSamples = new short[BluetoothMicFrameDecoder.PcmFrameSamples];
        Assert.That(expectedDecoder.Decode(opus, expectedSamples, BluetoothMicFrameDecoder.PcmFrameSamples * 12, false),
            Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
        expectedPcm = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];
        Span<short> expectedStereo = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, short>(expectedPcm.AsSpan());
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            expectedStereo[sample * 2] = expectedSamples[sample];
            expectedStereo[sample * 2 + 1] = expectedSamples[sample];
        }

        return BuildReport(opus);
    }
}
