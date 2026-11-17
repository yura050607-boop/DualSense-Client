using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Utilities;

namespace DualSenseBluetoothMicProbe;

/// <summary>
/// Offline assertions for the input packet verifier and effective neutral teardown.
/// The fixture is synthetic and is never sent to a controller or persisted.
/// </summary>
internal static class ProtocolSelfTest
{
    public static int Run()
    {
        try
        {
            AssertInputCrcKnownVector();
            byte[] validFixture = BuildPositiveOpusFixture();
            AssertPositiveFrame(validFixture);
            AssertRejectedFrames(validFixture);
            AssertStereoDecodeDifferential();
            AssertExactCaptureIdentity();
            AssertNativeCaptureIdentity();
            AssertWindowsPaddedWrites();
            AssertShortAndFailedWrites();
            AssertCancellationTeardown();
            AssertReadFailureTeardown();
            AssertWriteFailureStillAttemptsNeutralRestore();

            Console.WriteLine(
                "self_test=pass input_crc_a1=pass positive_opus480_packet=pass " +
                "wrong_a2_rejected=pass malformed_rejected=pass teardown_fields=pass " +
                "capture_identity=pass capture_native_wchar=pass " +
                "padded547=pass zero_write=pass short_write=pass negative_write=pass " +
                "throw_write=pass cancellation=pass read_failure=pass write_failure=pass");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"self_test=fail reason={ex.GetType().Name}:{ex.Message}");
            return 1;
        }
    }

    private static byte[] BuildPositiveOpusFixture()
    {
        // Keep a real 10 ms D4-marked Opus packet in the gate rather than a hand-written
        // pseudo-packet. The production decoder requests one mono output channel and
        // widens it to the virtual sink's required stereo frame.
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            48000,
            2,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        encoder.Bitrate = BluetoothMicFrameDecoder.OpusPayloadBytes * 8 * 100;
        encoder.UseVBR = false;
        encoder.Complexity = 0;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND;

        short[] pcm = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 2];
        for (int i = 0; i < BluetoothMicFrameDecoder.PcmFrameSamples; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 7000);
            pcm[i * 2] = sample;
            pcm[i * 2 + 1] = sample;
        }

        byte[] opus = new byte[BluetoothMicFrameDecoder.OpusPayloadBytes];
        int encoded = encoder.Encode(pcm, BluetoothMicFrameDecoder.PcmFrameSamples, opus, opus.Length);
        if (encoded != BluetoothMicFrameDecoder.OpusPayloadBytes)
        {
            throw new InvalidOperationException($"independent Opus fixture encoded to {encoded} bytes, expected {BluetoothMicFrameDecoder.OpusPayloadBytes}");
        }

        byte[] report = new byte[BluetoothMicFrameDecoder.BluetoothReportBytes];
        report[0] = BluetoothMicFrameDecoder.InputReportId;
        report[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        opus.CopyTo(report.AsSpan(3));
        if (report[3] != BluetoothMicFrameDecoder.OpusMarker)
        {
            throw new InvalidOperationException($"independent Opus TOC was 0x{report[3]:X2}, expected 0xD4");
        }

        WriteInputCrc(report);
        return report;
    }

    private static void AssertPositiveFrame(byte[] validFixture)
    {
        using BluetoothMicFrameDecoder decoder = new BluetoothMicFrameDecoder();
        if (!decoder.TryDecode(validFixture, out int decodedSamples)
            || decodedSamples != 480)
        {
            throw new InvalidOperationException($"positive Opus fixture did not decode to 480 samples (got {decodedSamples})");
        }
    }

    private static void AssertStereoDecodeDifferential()
    {
        byte[] differentBandOpus = BuildStereoOpusFixture(phaseOpposition: false);
        byte[] phaseOppositionOpus = BuildStereoOpusFixture(phaseOpposition: true);
        if (differentBandOpus[0] != BluetoothMicFrameDecoder.OpusMarker
            || phaseOppositionOpus[0] != BluetoothMicFrameDecoder.OpusMarker
            || (differentBandOpus[0] & 0x04) == 0)
        {
            throw new InvalidOperationException(
                "stereo differential fixtures did not carry the expected D4 stereo TOC");
        }

        (short[] differentBandReference, short[] differentBandMono) =
            DecodeStereoAndMono(differentBandOpus);
        (short[] phaseReference, short[] phaseMono) =
            DecodeStereoAndMono(phaseOppositionOpus);

        double differentBandChannelMae = ChannelMae(differentBandReference);
        double differentBandLegalDownmixMae = DownmixMae(
            differentBandReference, differentBandMono);
        double differentBandDuplicatedMae = DuplicatedMonoMae(
            differentBandReference, differentBandMono);
        double phaseLegalDownmixRms = DownmixRms(phaseReference);
        double phaseDuplicatedMae = DuplicatedMonoMae(phaseReference, phaseMono);

        // This is the exact behavior under test: the current decoder asks Opus for
        // one channel, then duplicates that chosen mono conversion into the VIIPER
        // stereo frame. It is legal downmixing, but it cannot preserve independent
        // channel content; the synthetic fixtures make that loss measurable.
        if (differentBandMono.All(sample => sample == 0)
            || phaseMono.All(sample => sample == 0)
            || differentBandDuplicatedMae <= 0d
            || phaseDuplicatedMae <= 0d)
        {
            throw new InvalidOperationException(
                "stereo differential did not expose channel information loss");
        }

        Console.WriteLine(
            $"stereo_differential=pass toc=0x{differentBandOpus[0]:X2} "
            + $"current_decoder=mono_then_duplicate "
            + $"different_band_channel_mae={FormatMetric(differentBandChannelMae, "F3")} "
            + $"different_band_legal_downmix_mae={FormatMetric(differentBandLegalDownmixMae, "F3")} "
            + $"different_band_duplicated_mae={FormatMetric(differentBandDuplicatedMae, "F3")} "
            + $"phase_legal_downmix_rms={FormatMetric(phaseLegalDownmixRms, "F6")} "
            + $"phase_duplicated_mae={FormatMetric(phaseDuplicatedMae, "F3")} "
            + "interpretation=legal_downmix_not_proven_symptomcause");
    }

    private static string FormatMetric(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static void AssertExactCaptureIdentity()
    {
        if (!WindowsCaptureSignalProbe.IsExactEndpointMatch(
                "\\\\?\\SWD#MMDEVAPI#{A}", "\\\\?\\SWD#MMDEVAPI#{A}")
            || WindowsCaptureSignalProbe.IsExactEndpointMatch(
                "\\\\?\\SWD#MMDEVAPI#{A}", "\\\\?\\SWD#MMDEVAPI#{a}")
            || WindowsCaptureSignalProbe.IsExactEndpointMatch("DualSense", "Wireless Controller")
            || WindowsCaptureSignalProbe.IsExactEndpointMatch("default", "default")
            || WindowsCaptureSignalProbe.IsExactEndpointMatch("", ""))
        {
            throw new InvalidOperationException(
                "capture endpoint selection accepted a name/default/case-folded fallback");
        }
    }

    private static void AssertNativeCaptureIdentity()
    {
        const string first = "{0.0.1.00000000}.{4271184C-4BC9-41A5-8DDC-0FD9D314B1D8}";
        const string second = "{0.0.1.00000000}.{45BA18F9-4709-4572-BFDF-3301C8559064}";
        IntPtr memory = Marshal.AllocHGlobal(128); // ma_device_id.wasapi[64], UTF-16.
        try
        {
            foreach (string id in new[] { first, second })
            {
                Marshal.Copy(new byte[128], 0, memory, 128);
                for (int index = 0; index < id.Length; index++)
                    Marshal.WriteInt16(memory, index * 2, (short)id[index]);
                if (WindowsCaptureSignalProbe.DecodeWasapiEndpointId(memory) != id)
                    throw new InvalidOperationException("native WCHAR capture ID was truncated or changed");
            }

            foreach (string invalid in new[] { "", "{", "default", first[..^1], new string('X', 64) })
            {
                Marshal.Copy(new byte[128], 0, memory, 128);
                for (int index = 0; index < invalid.Length; index++)
                    Marshal.WriteInt16(memory, index * 2, (short)invalid[index]);
                if (WindowsCaptureSignalProbe.DecodeWasapiEndpointId(memory) != string.Empty)
                    throw new InvalidOperationException("invalid/unterminated native capture ID accepted");
            }

            if (WindowsCaptureSignalProbe.DecodeWasapiEndpointId(IntPtr.Zero) != string.Empty)
                throw new InvalidOperationException("null native capture ID accepted");
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static byte[] BuildStereoOpusFixture(bool phaseOpposition)
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

        short[] pcm = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 2];
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            double time = sample / 48000d;
            double left = Math.Sin(2d * Math.PI * 440d * time) * 9000d;
            double right = phaseOpposition
                ? Math.Sin(2d * Math.PI * 440d * time + Math.PI) * 9000d
                : Math.Sin(2d * Math.PI * 3200d * time) * 9000d;
            pcm[sample * 2] = (short)Math.Clamp((int)left, short.MinValue, short.MaxValue);
            pcm[sample * 2 + 1] = (short)Math.Clamp((int)right, short.MinValue, short.MaxValue);
        }

        byte[] opus = new byte[BluetoothMicFrameDecoder.OpusPayloadBytes];
        if (encoder.Encode(pcm, BluetoothMicFrameDecoder.PcmFrameSamples,
                opus, opus.Length) != opus.Length)
        {
            throw new InvalidOperationException("stereo differential Opus fixture was not fixed-size");
        }

        return opus;
    }

    private static (short[] Reference, short[] Mono) DecodeStereoAndMono(byte[] opus)
    {
        using IOpusDecoder stereoDecoder = OpusCodecFactory.CreateDecoder(48000, 2);
        using IOpusDecoder monoDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        short[] stereo = new short[BluetoothMicFrameDecoder.PcmFrameSamples * 2];
        short[] mono = new short[BluetoothMicFrameDecoder.PcmFrameSamples];
        if (stereoDecoder.Decode(opus, stereo, stereo.Length, false)
                != BluetoothMicFrameDecoder.PcmFrameSamples
            || monoDecoder.Decode(opus, mono, mono.Length, false)
                != BluetoothMicFrameDecoder.PcmFrameSamples)
        {
            throw new InvalidOperationException("stereo differential Opus fixture did not decode to 480 samples");
        }

        return (stereo, mono);
    }

    private static double ChannelMae(short[] stereo)
    {
        long total = 0;
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            total += Math.Abs(stereo[sample * 2] - stereo[sample * 2 + 1]);
        }

        return total / (double)BluetoothMicFrameDecoder.PcmFrameSamples;
    }

    private static double DownmixMae(short[] stereo, short[] mono)
    {
        long total = 0;
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            int downmix = (stereo[sample * 2] + stereo[sample * 2 + 1]) / 2;
            total += Math.Abs(downmix - mono[sample]);
        }

        return total / (double)BluetoothMicFrameDecoder.PcmFrameSamples;
    }

    private static double DuplicatedMonoMae(short[] stereo, short[] mono)
    {
        long total = 0;
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            total += Math.Abs(stereo[sample * 2] - mono[sample]);
            total += Math.Abs(stereo[sample * 2 + 1] - mono[sample]);
        }

        return total / (double)(BluetoothMicFrameDecoder.PcmFrameSamples * 2);
    }

    private static double DownmixRms(short[] stereo)
    {
        double sumSquares = 0d;
        for (int sample = 0; sample < BluetoothMicFrameDecoder.PcmFrameSamples; sample++)
        {
            double downmix = (stereo[sample * 2] + stereo[sample * 2 + 1]) / (2d * 32768d);
            sumSquares += downmix * downmix;
        }

        return Math.Sqrt(sumSquares / BluetoothMicFrameDecoder.PcmFrameSamples);
    }

    private static void AssertInputCrcKnownVector()
    {
        // Independent Linux/zlib-compatible vector for A1 followed by report byte
        // 0x31. This prevents the fixture from validating a shared production/test
        // helper by coincidence.
        uint actual = BluetoothMicFrameDecoder.ComputeInputCrc32(new byte[] { 0x31 });
        if (actual != 0xA71B9F6Du)
        {
            throw new InvalidOperationException($"A1 input CRC vector changed (actual=0x{actual:X8})");
        }
    }

    private static void AssertRejectedFrames(byte[] validFixture)
    {
        using BluetoothMicFrameDecoder decoder = new BluetoothMicFrameDecoder();

        byte[] wrongA2 = (byte[])validFixture.Clone();
        uint outputSeededCrc = DualSenseCRC32.Compute(wrongA2, 0, wrongA2.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongA2.AsSpan(wrongA2.Length - 4), outputSeededCrc);
        AssertRejected(decoder, wrongA2, "output-seeded A2 CRC");

        byte[] badCrc = (byte[])validFixture.Clone();
        badCrc[^1] ^= 0x01;
        AssertRejected(decoder, badCrc, "bad CRC");

        byte[] truncated = validFixture[..^1];
        AssertRejected(decoder, truncated, "truncated report");

        byte[] markerOnly = new byte[BluetoothMicFrameDecoder.BluetoothReportBytes];
        markerOnly[0] = BluetoothMicFrameDecoder.InputReportId;
        markerOnly[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        markerOnly[3] = BluetoothMicFrameDecoder.OpusMarker;
        AssertRejected(decoder, markerOnly, "marker-only report");

        byte[] malformed = new byte[3 + 2 + 4];
        malformed[0] = BluetoothMicFrameDecoder.InputReportId;
        malformed[1] = BluetoothMicFrameDecoder.MicrophoneInputFlag;
        malformed[3] = BluetoothMicFrameDecoder.OpusMarker;
        malformed[4] = 0x00;
        WriteInputCrc(malformed);
        AssertRejected(decoder, malformed, "malformed Opus payload");
    }

    private static void AssertRejected(BluetoothMicFrameDecoder decoder, byte[] report, string description)
    {
        if (decoder.TryDecode(report, out int decodedSamples))
        {
            throw new InvalidOperationException($"{description} was accepted ({decodedSamples} samples)");
        }
    }

    private static void AssertWindowsPaddedWrites()
    {
        // The production diagnostic is Windows-only; retaining the guard keeps this
        // offline self-test harmless if the project is inspected on another host.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using FakeMicProbeTransport fake = new FakeMicProbeTransport
        {
            WriteResultOverride = (_, _) => MicProbeWriteResult.WindowsPaddedOutputReportLength
        };
        using MicProbeSession session = new MicProbeSession(fake, TimeSpan.FromMilliseconds(100));
        MicProbeSummary summary = session.Run();

        if (!summary.Activated || summary.Failure is not null
            || summary.Counters.TeardownAttempts != 2
            || summary.Counters.TeardownFailures != 0
            || fake.Writes.Count != 3
            || fake.Writes[0].Length != MicProbeWriteResult.CombinedReportLength
            || fake.Writes[1].Length != MicProbeWriteResult.CombinedReportLength
            || fake.Writes[2].Length != MicProbeWriteResult.BluetoothStateReportLength)
        {
            throw new InvalidOperationException("Windows SDL padded 547-byte writes were not accepted for 398/78-byte reports");
        }

        if (MicProbeWriteResult.IsSuccessful(MicProbeWriteResult.WindowsPaddedOutputReportLength + 1,
                MicProbeWriteResult.CombinedReportLength)
            || MicProbeWriteResult.IsSuccessful(MicProbeWriteResult.WindowsPaddedOutputReportLength,
                64))
        {
            throw new InvalidOperationException("unexplained padded write result was accepted");
        }
    }

    private static void AssertShortAndFailedWrites()
    {
        AssertRejectedWriteResult(0, "zero write");
        AssertRejectedWriteResult(MicProbeWriteResult.CombinedReportLength - 1, "short write");
        AssertRejectedWriteResult(-1, "negative write");

        using FakeMicProbeTransport throwing = new FakeMicProbeTransport { FailWriteNumber = 1 };
        using MicProbeSession throwingSession = new MicProbeSession(throwing, TimeSpan.FromMilliseconds(100));
        MicProbeSummary throwingSummary = throwingSession.Run();
        if (throwingSummary.Failure is null || throwingSummary.Counters.TeardownAttempts != 2
            || throwingSummary.Counters.TeardownFailures != 0)
        {
            throw new InvalidOperationException("throwing write was not reported while teardown remained independent");
        }
    }

    private static void AssertRejectedWriteResult(int result, string description)
    {
        using FakeMicProbeTransport fake = new FakeMicProbeTransport
        {
            WriteResultOverride = (_, _) => result
        };
        using MicProbeSession session = new MicProbeSession(fake, TimeSpan.FromMilliseconds(100));
        MicProbeSummary summary = session.Run();

        if (summary.Failure is null || summary.Activated
            || summary.Counters.TeardownAttempts != 2
            || summary.Counters.TeardownFailures != 2)
        {
            throw new InvalidOperationException($"{description} was accepted or did not preserve teardown attempts");
        }
    }

    private static void AssertCancellationTeardown()
    {
        using FakeMicProbeTransport fake = new FakeMicProbeTransport();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        fake.BeforeRead = cancellation.Cancel;
        using MicProbeSession session = new MicProbeSession(fake, TimeSpan.FromMilliseconds(100));
        MicProbeSummary summary = session.Run(cancellation.Token);

        if (!summary.Cancelled || summary.Counters.TeardownAttempts != 2
            || summary.Counters.TeardownFailures != 0 || fake.Writes.Count != 3)
        {
            throw new InvalidOperationException("cancellation did not complete both teardown writes");
        }

        AssertSessionWriteOrderAndFields(fake.Writes);
    }

    private static void AssertReadFailureTeardown()
    {
        using FakeMicProbeTransport fake = new FakeMicProbeTransport { ThrowOnRead = true };
        using MicProbeSession session = new MicProbeSession(fake, TimeSpan.FromMilliseconds(100));
        MicProbeSummary summary = session.Run();

        if (!summary.Disconnected || summary.Failure is null
            || summary.Counters.TeardownAttempts != 2
            || summary.Counters.TeardownFailures != 0)
        {
            throw new InvalidOperationException("read failure did not preserve both teardown attempts");
        }

        AssertSessionWriteOrderAndFields(fake.Writes);
    }

    private static void AssertWriteFailureStillAttemptsNeutralRestore()
    {
        using FakeMicProbeTransport fake = new FakeMicProbeTransport { FailWriteNumber = 2 };
        using MicProbeSession session = new MicProbeSession(fake, TimeSpan.FromMilliseconds(100));
        MicProbeSummary summary = session.Run();

        if (summary.Failure is null || summary.Counters.TeardownAttempts != 2
            || summary.Counters.TeardownFailures != 1 || fake.Writes.Count != 3)
        {
            throw new InvalidOperationException("write failure did not preserve the second neutral teardown attempt");
        }

        AssertSessionWriteOrderAndFields(fake.Writes);
    }

    private static void AssertSessionWriteOrderAndFields(IReadOnlyList<FakeMicProbeTransport.WriteMetadata> writes)
    {
        if (writes.Count != 3
            || writes[0] is not { ReportId: 0x36, SessionMask: 0xFF }
            || writes[1] is not { ReportId: 0x36, SessionMask: 0xFE }
            || writes[2] is not { ReportId: 0x31, SessionMask: 0x00 })
        {
            throw new InvalidOperationException("activation/disable/neutral output ordering is not preserved");
        }

        FakeMicProbeTransport.WriteMetadata activation = writes[0];
        RequireFlags(activation.ValidFlag1,
            ValidFlags.AllowMuteLight | ValidFlags.AllowAudioMute | ValidFlags.AllowAudioControl2,
            "activation audio flags");
        if (activation.MicVolume != 0x40
            || activation.AudioControl != (byte)AudioControl.MicSelectInternal
            || activation.PowerSaveControl != 0)
        {
            throw new InvalidOperationException("activation did not explicitly select and unmute the internal microphone");
        }

        for (int i = 1; i < writes.Count; i++)
        {
            FakeMicProbeTransport.WriteMetadata neutral = writes[i];
            RequireFlags(neutral.ValidFlag0,
                ValidFlags.EnableRumbleEmulation | ValidFlags.UseRumbleNotHaptics
                | ValidFlags.AllowRightTriggerFfb | ValidFlags.AllowLeftTriggerFfb
                | ValidFlags.AllowSpeakerVolume | ValidFlags.AllowHeadphoneVolume
                | ValidFlags.AllowMicVolume | ValidFlags.AllowAudioControl,
                $"teardown {i} rumble/trigger/audio flags");
            RequireFlags(neutral.ValidFlag1,
                ValidFlags.AllowMuteLight | ValidFlags.AllowAudioMute | ValidFlags.AllowAudioControl2,
                $"teardown {i} mute/audio flags");
            RequireFlags(neutral.ValidFlag2, ValidFlags.EnableImprovedRumbleEmu, $"teardown {i} rumble v2 flag");
            if (neutral.RumbleRight != 0 || neutral.RumbleLeft != 0
                || neutral.R2TriggerMode != 0 || neutral.L2TriggerMode != 0
                || neutral.SpeakerVolume != 0 || neutral.HeadphoneVolume != 0
                || neutral.MicVolume != 0
                || neutral.AudioControl != (byte)AudioControl.OutputPathSpeaker
                || neutral.PowerSaveControl != 0x10)
            {
                throw new InvalidOperationException($"teardown {i} did not carry effective neutral fields");
            }
        }
    }

    private static void RequireFlags(ValidFlags actual, ValidFlags expected, string description)
    {
        if ((actual & expected) != expected)
        {
            throw new InvalidOperationException($"{description} missing flags (actual=0x{(byte)actual:X2}, expected=0x{(byte)expected:X2})");
        }
    }

    private static void WriteInputCrc(byte[] report)
    {
        uint crc = ComputeFixtureInputCrc(report.AsSpan(0, report.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(report.Length - 4), crc);
    }

    private static uint ComputeFixtureInputCrc(ReadOnlySpan<byte> reportWithoutCrc)
    {
        uint crc = uint.MaxValue;
        crc = UpdateFixtureCrc(crc, 0xA1);
        foreach (byte value in reportWithoutCrc)
        {
            crc = UpdateFixtureCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateFixtureCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0
                ? crc >> 1
                : (crc >> 1) ^ 0xEDB88320u;
        }

        return crc;
    }
}
