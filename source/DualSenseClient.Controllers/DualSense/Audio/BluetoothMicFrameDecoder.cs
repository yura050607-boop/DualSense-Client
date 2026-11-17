using System.Buffers.Binary;
using Concentus;
using DualSenseClient.Controllers.DualSense.Utilities;

namespace DualSenseClient.Controllers.DualSense.Audio;

/// <summary>
/// Validates and decodes one DualSense Bluetooth built-in-microphone input report.
/// </summary>
/// <remarks>
/// The controller sends the microphone as a 78-byte Bluetooth input report (0x31).
/// Header byte 1 carries the maintained public input-lane discriminator: bit 1 is
/// the microphone lane and bit 0 is the normal gamepad lane. The Opus packet starts
/// at byte 3 with the 0xD4 marker. The report uses the HIDP A1 input CRC, not the
/// A2 output CRC used by <see cref="DualSenseCRC32"/>. The decoder is deliberately
/// strict: a marker alone is not enough to classify a report as microphone data,
/// and malformed candidates are never passed to the normal gamepad parser.
/// </remarks>
public sealed class BluetoothMicFrameDecoder : IDisposable
{
    /// <summary>
    /// Maximum input report accepted by the controller reader and decoder.
    /// </summary>
    public const int MaxReportBytes = 256;

    /// <summary>
    /// Maintained Bluetooth input report size for both normal state and microphone
    /// frames. A microphone candidate may be shorter while being classified so a
    /// truncated frame cannot fall through to the gamepad parser.
    /// </summary>
    public const int BluetoothReportBytes = 78;

    /// <summary>
    /// Bluetooth input report identifier shared by normal state and microphone frames.
    /// </summary>
    public const byte InputReportId = 0x31;

    /// <summary>
    /// Opus microphone packet marker at byte offset 3.
    /// </summary>
    public const byte OpusMarker = 0xD4;

    /// <summary>
    /// Bluetooth input header bit indicating the microphone lane.
    /// </summary>
    public const byte MicrophoneInputFlag = 0x02;

    /// <summary>
    /// Bluetooth input header bit indicating the normal gamepad lane.
    /// </summary>
    public const byte NormalInputFlag = 0x01;

    /// <summary>
    /// Opus payload bytes carried by the maintained 78-byte microphone report.
    /// </summary>
    public const int OpusPayloadBytes = 71;

    /// <summary>
    /// Exact VIIPER DualSense microphone frame size: 480 mono samples widened to
    /// stereo S16LE at 48 kHz.
    /// </summary>
    public const int PcmFrameBytes = 1920;

    /// <summary>
    /// Samples per channel in a decoded 10 ms frame.
    /// </summary>
    public const int PcmFrameSamples = 480;

    private const int CrcBytes = 4;
    private const byte InputHidpCrcPrefix = 0xA1;
    private const int DecoderCapacity = 5760;
    private const int DecoderChannels = 1;

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(48000, DecoderChannels);
    private readonly short[] _pcmScratch = new short[DecoderCapacity];

    /// <summary>
    /// Returns true for the unmistakable outer microphone envelope.  This is a
    /// classifier only; callers must still run <see cref="TryDecode"/>.
    /// </summary>
    public static bool HasMicEnvelope(ReadOnlySpan<byte> report)
    {
        return report.Length == BluetoothReportBytes
               && report[0] == InputReportId
               && (report[1] & MicrophoneInputFlag) != 0
               && report[3] == OpusMarker;
    }

    /// <summary>
    /// Returns true for any report that looks like a microphone candidate, including
    /// truncated/malformed reports.  The physical reader uses this before gamepad
    /// parsing so a bad candidate cannot become phantom stick/button input.
    /// </summary>
    public static bool LooksLikeMicCandidate(ReadOnlySpan<byte> report)
    {
        return report.Length >= 2
               && report.Length <= MaxReportBytes
               && report[0] == InputReportId
               && (report[1] & MicrophoneInputFlag) != 0;
    }

    /// <summary>
    /// Decodes a candidate without exposing PCM.  This overload is useful for
    /// protocol diagnostics that need only a validated frame count.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> report, out int decodedSamples)
    {
        Span<byte> scratch = stackalloc byte[PcmFrameBytes];
        return TryDecode(report, scratch, out decodedSamples);
    }

    /// <summary>
    /// Validates and decodes one microphone report into an exact 1920-byte stereo
    /// S16LE frame suitable for <c>SetDualSenseMicrophonePCM</c>.  The output span is
    /// never retained.  Invalid input clears the reported sample count and returns
    /// false without exposing partial decoder state.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> report, Span<byte> pcmFrame, out int decodedSamples)
    {
        decodedSamples = 0;
        if (!HasMicEnvelope(report) || pcmFrame.Length < PcmFrameBytes)
        {
            return false;
        }

        int crcOffset = report.Length - CrcBytes;
        if (crcOffset <= 3)
        {
            return false;
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(crcOffset, CrcBytes));
        uint actualCrc = ComputeInputCrc32(report[..crcOffset]);
        if (actualCrc != expectedCrc)
        {
            return false;
        }

        ReadOnlySpan<byte> opusPacket = report.Slice(3, crcOffset - 3);
        int samples;
        try
        {
            samples = _decoder.Decode(opusPacket, _pcmScratch, DecoderCapacity, false);
        }
        catch (Exception)
        {
            _decoder.ResetState();
            return false;
        }

        if (samples != PcmFrameSamples)
        {
            _decoder.ResetState();
            return false;
        }

        Span<short> stereoPcm = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, short>(pcmFrame[..PcmFrameBytes]);
        for (int sample = 0; sample < PcmFrameSamples; sample++)
        {
            short value = _pcmScratch[sample];
            stereoPcm[sample * 2] = value;
            stereoPcm[sample * 2 + 1] = value;
        }
        decodedSamples = samples;
        return true;
    }

    /// <summary>
    /// Starts a fresh Opus decode generation after a physical mute or lifecycle
    /// barrier. No PCM is retained by the decoder, but Opus prediction state must not
    /// bridge a capture gap.
    /// </summary>
    public void Reset() => _decoder.ResetState();

    /// <summary>
    /// Computes the HIDP A1-seeded CRC for a Bluetooth input report without its
    /// trailing CRC field.
    /// </summary>
    public static uint ComputeInputCrc32(ReadOnlySpan<byte> reportWithoutCrc)
    {
        uint crc = uint.MaxValue;
        crc = UpdateCrc(crc, InputHidpCrcPrefix);
        foreach (byte value in reportWithoutCrc)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? (crc >> 1) ^ 0xEDB88320u
                : crc >> 1;
        }

        return crc;
    }

    /// <inheritdoc/>
    public void Dispose() => _decoder.Dispose();
}
