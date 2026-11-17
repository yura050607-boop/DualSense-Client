using System.Buffers.Binary;
using System.Diagnostics;
using DualSenseClient.Controllers;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Feature;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Utilities;
using DualSenseClient.Hid;

namespace DualSenseBluetoothMicProbe;

/// <summary>
/// Minimal HID transport surface used by the bounded microphone diagnostic.
/// </summary>
public interface IMicProbeTransport : IDisposable
{
    /// <summary>
    /// Whether the underlying HID handle is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Writes one complete HID output report.
    /// </summary>
    int Write(byte[] report, int offset, int count);

    /// <summary>
    /// Reads one input report with a bounded timeout.
    /// </summary>
    int Read(byte[] buffer, int offset, int count, int timeoutMs);
}

/// <summary>
/// The narrow Windows SDL/HIDAPI write contract used by the standard DualSense
/// Bluetooth probe. On Windows, HIDAPI obtains the device's
/// <c>OutputReportByteLength</c>, zero-pads shorter writes to that length, and
/// returns the padded byte count from <c>WriteFile</c>. The current standard
/// DualSense Bluetooth handle reports 547 bytes while this diagnostic sends the
/// 398-byte combined or 78-byte state report.
/// See SDL's Windows HIDAPI implementation at
/// <c>src/hidapi/windows/hid.c</c>: it loads <c>caps.OutputReportByteLength</c>,
/// zero-fills shorter buffers, and returns the <c>WriteFile</c> byte count.
/// </summary>
internal static class MicProbeWriteResult
{
    public const int CombinedReportLength = 398;
    public const int BluetoothStateReportLength = 78;
    public const int WindowsPaddedOutputReportLength = 547;

    /// <summary>
    /// Accepts an exact logical write, or the one documented Windows padding result
    /// for one of this probe's two known DualSense report lengths. Everything else,
    /// including short, zero, negative, and unexplained larger counts, is rejected.
    /// </summary>
    public static bool IsSuccessful(int reported, int requested)
    {
        if (reported == requested)
        {
            return true;
        }

        return OperatingSystem.IsWindows()
               && reported == WindowsPaddedOutputReportLength
               && requested is CombinedReportLength or BluetoothStateReportLength;
    }
}

/// <summary>
/// Adapter around the repository's existing SDL HID handle.
/// </summary>
public sealed class HidMicProbeTransport(IHidDevice device) : IMicProbeTransport
{
    private readonly IHidDevice _device = device;

    /// <inheritdoc/>
    public bool IsConnected => _device.IsConnected;

    /// <inheritdoc/>
    public int Write(byte[] report, int offset, int count) => _device.Write(report, offset, count);

    /// <inheritdoc/>
    public int Read(byte[] buffer, int offset, int count, int timeoutMs) => _device.Read(buffer, offset, count, timeoutMs);

    /// <inheritdoc/>
    public void Dispose() => _device.Dispose();
}

/// <summary>
/// Counters emitted by the probe. No raw input reports, decoded PCM, or speech are
/// retained; only these aggregate values are returned.
/// </summary>
public sealed class MicProbeCounters
{
    public int ReportsRead { get; internal set; }
    public int BytesRead { get; internal set; }
    public int ReadTimeouts { get; internal set; }
    public int EnvelopeCandidates { get; internal set; }
    public int RejectedCandidates { get; internal set; }
    public int ValidOpusFrames { get; internal set; }
    public int TeardownAttempts { get; internal set; }
    public int TeardownFailures { get; internal set; }
}

/// <summary>
/// Result of one bounded microphone diagnostic run.
/// </summary>
public sealed record MicProbeSummary(
    MicProbeCounters Counters,
    bool Activated,
    bool Cancelled,
    bool Disconnected,
    string? Failure)
{
    /// <summary>
    /// Whether at least one fully validated microphone Opus frame was observed.
    /// </summary>
    public bool HasValidatedMicFrames => Counters.ValidOpusFrames > 0;

    /// <summary>
    /// Optional aggregate decoded-PCM summary. It is present only when the caller
    /// explicitly requested source-side signal metrics.
    /// </summary>
    public BluetoothMicrophoneSignalSnapshot? Signal { get; init; }
}

/// <summary>
/// Runs an opt-in, short-lived Bluetooth microphone session over an injected HID
/// transport. The session owns no virtual device and never publishes gamepad input.
/// </summary>
public sealed class MicProbeSession : IDisposable
{
    /// <summary>
    /// Standard DualSense combined report size used for duplex session activation.
    /// </summary>
    private const int CombinedReportSize = MicProbeWriteResult.CombinedReportLength;

    /// <summary>
    /// Maximum report count accepted by the reader, matching the parser safety cap.
    /// </summary>
    private const int MaxInputReportSize = BluetoothMicFrameDecoder.MaxReportBytes;

    /// <summary>
    /// Bounded read timeout so cancellation and disconnects reach the teardown path.
    /// </summary>
    private const int ReadTimeoutMs = 100;

    /// <summary>
    /// Trailing CRC size for Bluetooth output reports.
    /// </summary>
    private const int CrcBytes = 4;

    /// <summary>
    /// Injected physical HID transport.
    /// </summary>
    private readonly IMicProbeTransport _transport;

    /// <summary>
    /// Bounded activation/read duration.
    /// </summary>
    private readonly TimeSpan _duration;

    /// <summary>
    /// Stateful decoder used only for this run.
    /// </summary>
    private readonly BluetoothMicFrameDecoder _decoder = new BluetoothMicFrameDecoder();

    /// <summary>
    /// Optional aggregate-only sink for decoded PCM. The frame remains ephemeral.
    /// </summary>
    private readonly BluetoothMicrophoneSignalAccumulator? _signal;

    /// <summary>
    /// Reused decoded frame; it is never persisted or exposed from the session.
    /// </summary>
    private readonly byte[] _pcmFrame = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];

    /// <summary>
    /// Encoder used to put a valid, silent Opus packet in the activation report. A
    /// syntactically valid silence frame avoids asking the controller to decode an
    /// arbitrary all-zero payload while keeping user audio out of the probe.
    /// </summary>
    private readonly DualSenseBtAudioPipeline _silencePipeline = new DualSenseBtAudioPipeline();

    private readonly byte[] _silentOpusFrame = new byte[DualSenseBtAudioPipeline.OpusBytes];

    /// <summary>
    /// Safe neutral state used after every activation attempt. The probe does not
    /// read a complete pre-probe output state, so this is a documented neutral reset,
    /// not a claim to restore unknown game-specific volumes or effects.
    /// </summary>
    private static SetStateData SafeNeutralState => new SetStateData
    {
        ValidFlag0 = ValidFlags.EnableRumbleEmulation | ValidFlags.UseRumbleNotHaptics
                     | ValidFlags.AllowRightTriggerFfb | ValidFlags.AllowLeftTriggerFfb
                     | ValidFlags.AllowSpeakerVolume | ValidFlags.AllowHeadphoneVolume
                     | ValidFlags.AllowMicVolume | ValidFlags.AllowAudioControl,
        ValidFlag1 = ValidFlags.AllowMuteLight | ValidFlags.AllowAudioMute
                     | ValidFlags.AllowAudioControl2,
        ValidFlag2 = ValidFlags.EnableImprovedRumbleEmu,
        RumbleRight = 0,
        RumbleLeft = 0,
        HeadphoneVolume = 0,
        SpeakerVolume = 0,
        MicVolume = 0,
        AudioControl = AudioControl.OutputPathSpeaker,
        MuteLedMode = 0x01,
        // Linux's maintained DualSense driver uses bit 4 to mute the microphone.
        PowerSaveControl = 0x10,
        R2TriggerEffect = DualSenseClient.Controllers.DualSense.Triggers.TriggerEffectBuilder.Off(),
        L2TriggerEffect = DualSenseClient.Controllers.DualSense.Triggers.TriggerEffectBuilder.Off(),
        AudioControl2 = 0x02
    };

    /// <summary>
    /// Single fixed-size read buffer; reports are parsed immediately and discarded.
    /// </summary>
    private readonly byte[] _readBuffer = new byte[MaxInputReportSize];

    /// <summary>
    /// Shared sequence tag for the diagnostic's output report families.
    /// </summary>
    private byte _sequence;

    /// <summary>
    /// Session packet counter.
    /// </summary>
    private byte _packetCounter;

    /// <summary>
    /// Creates a probe session with an explicit bounded duration.
    /// </summary>
    public MicProbeSession(
        IMicProbeTransport transport,
        TimeSpan duration,
        BluetoothMicrophoneSignalAccumulator? signal = null)
    {
        if (duration < TimeSpan.FromMilliseconds(100) || duration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The diagnostic duration must be between 100 ms and 10 s.");
        }

        _transport = transport;
        _duration = duration;
        _signal = signal;
        _silencePipeline.EncodeOpus(
            new short[DualSenseBtAudioPipeline.OpusFrameSamples * DualSenseBtAudioPipeline.Channels],
            _silentOpusFrame);
    }

    /// <summary>
    /// Runs the activation/read loop and always attempts duplex disable plus neutral
    /// output restoration after an activation attempt.
    /// </summary>
    public MicProbeSummary Run(CancellationToken cancellationToken = default)
    {
        MicProbeCounters counters = new MicProbeCounters();
        bool activated = false;
        bool activationAttempted = false;
        bool cancelled = false;
        bool disconnected = false;
        string? failure = null;

        try
        {
            if (!_transport.IsConnected)
            {
                disconnected = true;
                failure = "the selected Bluetooth HID handle is not connected";
            }
            else
            {
                activationAttempted = true;
                byte[] enable = BuildDuplexSessionReport(0xFF, BuildMicState());
                if (!TryWrite(enable, counters, ref failure))
                {
                    disconnected = true;
                }
                else
                {
                    activated = true;
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    while (stopwatch.Elapsed < _duration)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int count;
                        try
                        {
                            count = _transport.Read(_readBuffer, 0, _readBuffer.Length, ReadTimeoutMs);
                        }
                        catch (Exception ex)
                        {
                            disconnected = true;
                            failure = $"HID read failed ({ex.GetType().Name})";
                            break;
                        }

                        if (count <= 0)
                        {
                            counters.ReadTimeouts++;
                            continue;
                        }

                        if (count > _readBuffer.Length)
                        {
                            counters.RejectedCandidates++;
                            continue;
                        }

                        counters.ReportsRead++;
                        counters.BytesRead += count;
                        ReadOnlySpan<byte> report = _readBuffer.AsSpan(0, count);
                        if (!BluetoothMicFrameDecoder.HasMicEnvelope(report))
                        {
                            continue;
                        }

                        counters.EnvelopeCandidates++;
                        if (_decoder.TryDecode(report, _pcmFrame, out _))
                        {
                            counters.ValidOpusFrames++;
                            _signal?.ObservePcm16(_pcmFrame);
                        }
                        else
                        {
                            counters.RejectedCandidates++;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = $"probe failed ({ex.GetType().Name})";
        }
        finally
        {
            if (activationAttempted)
            {
                // Both teardown writes are attempted independently so a failed
                // disable cannot prevent neutral restoration.
                SetStateData safeNeutral = SafeNeutralState;
                counters.TeardownAttempts++;
                if (!TryWrite(BuildDuplexSessionReport(0xFE, safeNeutral), counters, ref failure))
                {
                    counters.TeardownFailures++;
                }

                counters.TeardownAttempts++;
                OutputReport neutral = OutputReport.ForBluetooth(safeNeutral, NextSequence());
                if (!TryWrite(neutral.Raw, counters, ref failure))
                {
                    counters.TeardownFailures++;
                }
            }
        }

        return new MicProbeSummary(counters, activated, cancelled, disconnected, failure)
        {
            Signal = _signal?.Snapshot()
        };
    }

    /// <summary>
    /// Writes one complete report and records only aggregate teardown/activation state.
    /// </summary>
    private bool TryWrite(byte[] report, MicProbeCounters counters, ref string? failure)
    {
        try
        {
            int written = _transport.Write(report, 0, report.Length);
            if (!MicProbeWriteResult.IsSuccessful(written, report.Length))
            {
                failure ??= $"HID write returned {written} for {report.Length} bytes";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure ??= $"HID write failed ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Builds a bounded combined report with the requested mic session mask. The
    /// payload is silent and carries no gamepad publication or host microphone data.
    /// </summary>
    private byte[] BuildDuplexSessionReport(byte sessionMask, SetStateData state)
    {
        byte[] report = new byte[CombinedReportSize];
        report[0] = 0x36;
        report[1] = (byte)((NextSequence() & 0x0F) << 4);

        // Packet 0x11 session block. 0xFF enables the opt-in duplex experiment;
        // 0xFE is the maintained mic-disabled state used during teardown.
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = sessionMask;
        report[5] = 0x10;
        report[6] = 0x10;
        report[7] = 0x10;
        report[8] = 0x10;
        report[9] = 0x10;
        report[10] = _packetCounter++;

        // Packet 0x10 state block.
        report[11] = 0x90;
        report[12] = 0x3F;
        state.CopyTo(report, 13);

        // Packet 0x12 haptics block: explicit silence during the diagnostic.
        report[76] = 0x92;
        report[77] = 0x40;

        // Packet 0x13 speaker route with a valid silent Opus body. Volumes in the
        // activation state are explicitly zero, so this does not play user audio.
        report[142] = (byte)BluetoothAudioRoute.Speaker;
        report[143] = 0xC8;
        _silentOpusFrame.CopyTo(report.AsSpan(144));

        WriteCrc(report);
        return report;
    }

    /// <summary>
    /// Selects the physical internal microphone while keeping output volume muted.
    /// </summary>
    private static SetStateData BuildMicState()
    {
        return new SetStateData
        {
            ValidFlag0 = ValidFlags.AllowSpeakerVolume | ValidFlags.AllowHeadphoneVolume
                         | ValidFlags.AllowMicVolume | ValidFlags.AllowAudioControl,
            ValidFlag1 = ValidFlags.AllowMuteLight | ValidFlags.AllowAudioMute
                         | ValidFlags.AllowAudioControl2,
            SpeakerVolume = 0,
            HeadphoneVolume = 0,
            MicVolume = 0x40,
            AudioControl = AudioControl.MicSelectInternal,
            MuteLedMode = 0,
            // Clear the maintained bit-4 microphone mute flag before capture.
            PowerSaveControl = 0,
            AudioControl2 = 0x02
        };
    }

    /// <summary>
    /// Allocates one monotonic low-nibble Bluetooth sequence value.
    /// </summary>
    private byte NextSequence()
    {
        byte value = (byte)(_sequence & 0x0F);
        _sequence = (byte)((_sequence + 1) & 0x0F);
        return value;
    }

    /// <summary>
    /// Writes the seeded DualSense CRC32 at the end of an output report.
    /// </summary>
    private static void WriteCrc(byte[] report)
    {
        uint crc = DualSenseCRC32.Compute(report, 0, report.Length - CrcBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(report.Length - CrcBytes), crc);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _decoder.Dispose();
        _silencePipeline.Dispose();
    }
}

/// <summary>
/// In-memory fake transport used by the diagnostic self-test to prove teardown on
/// cancellation. It records only output metadata, never raw reports or PCM.
/// </summary>
public sealed class FakeMicProbeTransport : IMicProbeTransport
{
    /// <summary>
    /// Metadata for one output write.
    /// </summary>
    public readonly record struct WriteMetadata(
        byte ReportId,
        int Length,
        byte SessionMask,
        ValidFlags ValidFlag0,
        ValidFlags ValidFlag1,
        ValidFlags ValidFlag2,
        byte RumbleRight,
        byte RumbleLeft,
        byte SpeakerVolume,
        byte HeadphoneVolume,
        byte MicVolume,
        byte AudioControl,
        byte PowerSaveControl,
        byte R2TriggerMode,
        byte L2TriggerMode);

    private readonly List<WriteMetadata> _writes = [];

    /// <summary>
    /// Called immediately before a read returns; useful for deterministic cancellation.
    /// </summary>
    public Action? BeforeRead { get; set; }

    /// <summary>
    /// Makes the next read throw a transport failure.
    /// </summary>
    public bool ThrowOnRead { get; set; }

    /// <summary>
    /// One-based write attempt that should throw, after its metadata is recorded.
    /// </summary>
    public int? FailWriteNumber { get; set; }

    /// <summary>
    /// Optional raw result returned by the fake after a write is recorded. This
    /// allows the self-test to exercise the Windows 547-byte padded result without
    /// changing the logical report metadata.
    /// </summary>
    public Func<int, int, int>? WriteResultOverride { get; set; }

    /// <summary>
    /// Current fake connection state.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Output metadata observed by the self-test.
    /// </summary>
    public IReadOnlyList<WriteMetadata> Writes => _writes;

    /// <inheritdoc/>
    public int Write(byte[] report, int offset, int count)
    {
        byte sessionMask = count > 4 && report[offset] == 0x36 ? report[offset + 4] : (byte)0;
        byte reportId = count > 0 ? report[offset] : (byte)0;
        int stateOffset = reportId switch
        {
            0x36 when count >= 13 + SetStateData.PayloadSize => offset + 13,
            0x31 when count >= 3 + SetStateData.PayloadSize => offset + 3,
            _ => -1
        };

        SetStateData state = stateOffset >= 0
            ? new SetStateData(report, stateOffset)
            : new SetStateData();
        _writes.Add(new WriteMetadata(
            reportId,
            count,
            sessionMask,
            state.ValidFlag0,
            state.ValidFlag1,
            state.ValidFlag2,
            state.RumbleRight,
            state.RumbleLeft,
            state.SpeakerVolume,
            state.HeadphoneVolume,
            state.MicVolume,
            (byte)state.AudioControl,
            state.PowerSaveControl,
            (byte)state.R2TriggerEffect.Mode,
            (byte)state.L2TriggerEffect.Mode));

        if (FailWriteNumber == _writes.Count)
        {
            throw new IOException("fake write failure");
        }

        return WriteResultOverride?.Invoke(_writes.Count, count) ?? count;
    }

    /// <inheritdoc/>
    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        BeforeRead?.Invoke();
        if (ThrowOnRead)
        {
            throw new IOException("fake disconnect");
        }

        return 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IsConnected = false;
    }
}
