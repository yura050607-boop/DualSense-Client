using System.Diagnostics;
using System.Globalization;

using DualSenseClient.Logging;

namespace DualSenseClient.Controllers.DualSense.Audio;

/// <summary>
/// Opt-in aggregate telemetry for the physical DualSense Bluetooth microphone
/// path. It deliberately records no report bytes, PCM, or voice content.
/// </summary>
/// <remarks>
/// The telemetry is disabled by default. Set
/// <c>DUALSENSECLIENT_MIC_DIAGNOSTICS_SECONDS</c> to a bounded number of seconds
/// before starting the application to collect a short aggregate window. This is
/// a measurement seam, not a second audio pipeline: the production path and its
/// synchronous handoff remain unchanged while the window is active.
/// </remarks>
public sealed class BluetoothMicrophoneDiagnostics
{
    /// <summary>
    /// Environment variable enabling a bounded aggregate diagnostics window.
    /// Values are clamped to one through sixty seconds.
    /// </summary>
    public const string EnvironmentVariableName = "DUALSENSECLIENT_MIC_DIAGNOSTICS_SECONDS";

    private const int MaximumWindowSeconds = 60;
    private const int MicrophoneFramesPerSecond = 100;
    private const int WarmupFrameCount = 10;
    private const int GapMultiplierNumerator = 3;
    private const int GapMultiplierDenominator = 2;

    private static readonly DualSenseClientLogger Log =
        DualSenseClientLogger.For("BluetoothMicrophoneDiagnostics");

    private readonly object _sync = new object();
    private readonly bool _enabled;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private readonly long _windowTicks;
    private readonly long _gapThresholdTicks;
    private readonly BluetoothMicrophoneSignalAccumulator _signal =
        new BluetoothMicrophoneSignalAccumulator(48000, 2);
    private long _windowStart = -1;
    private long _lastLog = -1;
    private long _lastCandidate = -1;
    private long _lastDecoded = -1;
    private long _lastSink = -1;
    private long _candidateIntervalMin = long.MaxValue;
    private long _candidateIntervalMax;
    private long _decodedIntervalMin = long.MaxValue;
    private long _decodedIntervalMax;
    private long _sinkIntervalMin = long.MaxValue;
    private long _sinkIntervalMax;
    private long _decodeDurationMin = long.MaxValue;
    private long _decodeDurationMax;
    private long _decodeDurationTotal;
    private long _sinkDurationMin = long.MaxValue;
    private long _sinkDurationMax;
    private long _sinkDurationTotal;
    private long _pipelineDurationMin = long.MaxValue;
    private long _pipelineDurationMax;
    private long _pipelineDurationTotal;
    private long _inputReports;
    private long _inputBytes;
    private long _normalReports;
    private long _microphoneCandidates;
    private long _envelopedCandidates;
    private int _candidateLengthMin = int.MaxValue;
    private int _candidateLengthMax;
    private long _decodedFrames;
    private long _rejectedFrames;
    private long _mutedFrames;
    private long _decoderResets;
    private long _muteChanges;
    private long _sinkAttempts;
    private long _sinkPcmFrames;
    private long _sinkSilenceFrames;
    private long _sinkAccepted;
    private long _sinkFailed;
    private long _sinkBytes;
    private long _candidateGaps;
    private long _decodedGaps;
    private long _sinkGaps;
    private long _pcmSamples;
    private long _pcmNonZeroSamples;
    private long _pcmSquareTotal;
    private int _pcmPeak;
    private long _pcmClippedSamples;
    private long _warmupPcmSamples;
    private long _warmupPcmSquareTotal;
    private long _steadyPcmSamples;
    private long _steadyPcmSquareTotal;
    private long _opusPackets;
    private int _opusPayloadLengthMin = int.MaxValue;
    private int _opusPayloadLengthMax;
    private bool _opusTocKnown;
    private byte _firstOpusToc;
    private byte _lastOpusToc;
    private long _opusTocChanges;
    private int _decodedSampleMinimum = int.MaxValue;
    private int _decodedSampleMaximum;
    private long _decodedSampleTotal;
    private long _unexpectedDecodedFrameSizes;
    private long _sessionStarts;
    private long _sessionStops;
    private bool _lastMute;
    private bool _finalLogged;

    /// <summary>
    /// Creates the telemetry policy from the process environment. Missing,
    /// malformed, or non-positive values keep the seam disabled.
    /// </summary>
    public static BluetoothMicrophoneDiagnostics CreateFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int seconds) || seconds <= 0)
        {
            return new BluetoothMicrophoneDiagnostics(false, Stopwatch.GetTimestamp,
                Stopwatch.Frequency, TimeSpan.Zero);
        }

        seconds = Math.Min(seconds, MaximumWindowSeconds);
        return new BluetoothMicrophoneDiagnostics(true, Stopwatch.GetTimestamp,
            Stopwatch.Frequency, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Constructs a deterministic seam for focused tests.
    /// </summary>
    internal BluetoothMicrophoneDiagnostics(
        bool enabled,
        Func<long> timestamp,
        long frequency,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        _enabled = enabled;
        _timestamp = timestamp;
        _frequency = frequency;
        _windowTicks = window <= TimeSpan.Zero
            ? 0
            : Math.Max(1, (long)(window.TotalSeconds * frequency));
        long expectedInterval = Math.Max(1, frequency / MicrophoneFramesPerSecond);
        _gapThresholdTicks = Math.Max(1,
            expectedInterval * GapMultiplierNumerator / GapMultiplierDenominator);
    }

    /// <summary>
    /// Returns the current monotonic timestamp used by this diagnostics seam.
    /// </summary>
    internal long Timestamp() => _enabled ? _timestamp() : 0;

    /// <summary>
    /// Records every non-empty physical input report before dispatch.
    /// </summary>
    internal void ObserveInputReport(int length)
    {
        if (!BeginObservation())
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _inputReports++;
            _inputBytes += Math.Max(0, length);
            MaybeLogLocked(_timestamp(), false);
        }
    }

    /// <summary>
    /// Records the microphone-lane envelope classification and cadence.
    /// </summary>
    internal void ObserveMicrophoneCandidate(int length, bool hasEnvelope)
    {
        if (!BeginObservation())
        {
            return;
        }

        long now = _timestamp();
        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _microphoneCandidates++;
            _candidateLengthMin = Math.Min(_candidateLengthMin, Math.Max(0, length));
            _candidateLengthMax = Math.Max(_candidateLengthMax, Math.Max(0, length));
            if (hasEnvelope)
            {
                _envelopedCandidates++;
            }

            ObserveIntervalLocked(now, ref _lastCandidate,
                ref _candidateIntervalMin, ref _candidateIntervalMax,
                ref _candidateGaps);
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records a normal gamepad state report that was not a microphone candidate.
    /// </summary>
    internal void ObserveNormalReport()
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (IsWindowOpenLocked())
            {
                _normalReports++;
                MaybeLogLocked(now, false);
            }
        }
    }

    /// <summary>
    /// Records a candidate that was skipped because the physical mute bit was set.
    /// </summary>
    internal void ObserveMutedFrame()
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _mutedFrames++;
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records one decoder result and aggregate PCM level statistics.
    /// </summary>
    internal void ObserveDecodeResult(
        ReadOnlySpan<byte> pcm,
        bool decoded,
        long durationTicks,
        int decodedSamples = 0)
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            if (!decoded)
            {
                _rejectedFrames++;
            }
            else
            {
                _decodedFrames++;
                int sampleCount = decodedSamples > 0
                    ? decodedSamples
                    : pcm.Length / (sizeof(short) * 2);
                _decodedSampleMinimum = Math.Min(_decodedSampleMinimum, sampleCount);
                _decodedSampleMaximum = Math.Max(_decodedSampleMaximum, sampleCount);
                _decodedSampleTotal += sampleCount;
                if (sampleCount != BluetoothMicFrameDecoder.PcmFrameSamples)
                {
                    _unexpectedDecodedFrameSizes++;
                }
                ObserveIntervalLocked(now, ref _lastDecoded,
                    ref _decodedIntervalMin, ref _decodedIntervalMax,
                    ref _decodedGaps);
                ObservePcmLocked(pcm, _decodedFrames <= WarmupFrameCount);
                _signal.ObservePcm16(pcm);
            }

            ObserveDurationLocked(durationTicks, ref _decodeDurationMin,
                ref _decodeDurationMax, ref _decodeDurationTotal);
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records Opus TOC and payload metadata from a structurally enveloped packet
    /// without retaining its bytes. Decoded sample counts are recorded separately.
    /// </summary>
    internal void ObserveOpusPacketMetadata(byte toc, int payloadLength)
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _opusPackets++;
            payloadLength = Math.Max(0, payloadLength);
            _opusPayloadLengthMin = Math.Min(_opusPayloadLengthMin, payloadLength);
            _opusPayloadLengthMax = Math.Max(_opusPayloadLengthMax, payloadLength);
            if (!_opusTocKnown)
            {
                _firstOpusToc = toc;
                _opusTocKnown = true;
            }
            else if (_lastOpusToc != toc)
            {
                _opusTocChanges++;
            }

            _lastOpusToc = toc;
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records a decoder reset associated with a mute or lifecycle barrier.
    /// </summary>
    internal void ObserveDecoderReset()
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _decoderResets++;
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records the complete synchronous decode-to-sink callback duration.
    /// </summary>
    internal void ObservePipelineDuration(long durationTicks)
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            ObserveDurationLocked(durationTicks, ref _pipelineDurationMin,
                ref _pipelineDurationMax, ref _pipelineDurationTotal);
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records a physical mute transition without retaining its source report.
    /// </summary>
    internal void ObserveMuteChange(bool muted)
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _muteChanges++;
            _lastMute = muted;
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records one exact sink handoff. The frame is inspected only for aggregate
    /// zero/non-zero classification and byte count; no bytes are retained.
    /// </summary>
    internal void ObserveSinkSubmission(
        ReadOnlySpan<byte> frame,
        bool accepted,
        bool silence,
        long durationTicks)
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWindowOpenLocked())
            {
                return;
            }

            _sinkAttempts++;
            if (silence)
            {
                _sinkSilenceFrames++;
            }
            else
            {
                _sinkPcmFrames++;
            }

            if (accepted)
            {
                _sinkAccepted++;
                _sinkBytes += frame.Length;
            }
            else
            {
                _sinkFailed++;
            }

            ObserveIntervalLocked(now, ref _lastSink,
                ref _sinkIntervalMin, ref _sinkIntervalMax, ref _sinkGaps);
            ObserveDurationLocked(durationTicks, ref _sinkDurationMin,
                ref _sinkDurationMax, ref _sinkDurationTotal);
            MaybeLogLocked(now, false);
        }
    }

    /// <summary>
    /// Records a virtual microphone lifecycle transition.
    /// </summary>
    internal void ObserveSessionStart()
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (IsWindowOpenLocked())
            {
                _sessionStarts++;
                MaybeLogLocked(now, false);
            }
        }
    }

    /// <summary>
    /// Records a virtual microphone lifecycle transition.
    /// </summary>
    internal void ObserveSessionStop()
    {
        if (!TryEnterWindow(out long now))
        {
            return;
        }

        lock (_sync)
        {
            if (IsWindowOpenLocked())
            {
                _sessionStops++;
                MaybeLogLocked(now, false);
            }
        }
    }

    /// <summary>
    /// Gets an immutable aggregate view for tests and the final log record.
    /// </summary>
    internal BluetoothMicrophoneDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new BluetoothMicrophoneDiagnosticsSnapshot(
                _enabled,
                ElapsedMillisecondsLocked(_timestamp()),
                _inputReports,
                _inputBytes,
                _normalReports,
                _microphoneCandidates,
                _envelopedCandidates,
                _candidateLengthMin == int.MaxValue ? 0 : _candidateLengthMin,
                _candidateLengthMax,
                _decodedFrames,
                _rejectedFrames,
                _mutedFrames,
                _decoderResets,
                _muteChanges,
                _sinkAttempts,
                _sinkPcmFrames,
                _sinkSilenceFrames,
                _sinkAccepted,
                _sinkFailed,
                _sinkBytes,
                _candidateGaps,
                _decodedGaps,
                _sinkGaps,
                ToMilliseconds(_candidateIntervalMin),
                ToMilliseconds(_candidateIntervalMax),
                ToMilliseconds(_decodedIntervalMin),
                ToMilliseconds(_decodedIntervalMax),
                ToMilliseconds(_sinkIntervalMin),
                ToMilliseconds(_sinkIntervalMax),
                ToMilliseconds(_decodeDurationMin),
                ToMilliseconds(_decodeDurationMax),
                AverageMilliseconds(_decodeDurationTotal, _decodedFrames + _rejectedFrames),
                ToMilliseconds(_sinkDurationMin),
                ToMilliseconds(_sinkDurationMax),
                AverageMilliseconds(_sinkDurationTotal, _sinkAttempts),
                ToMilliseconds(_pipelineDurationMin),
                ToMilliseconds(_pipelineDurationMax),
                AverageMilliseconds(_pipelineDurationTotal, _decodedFrames),
                _pcmSamples,
                _pcmNonZeroSamples,
                _pcmSamples == 0 ? 0 : Math.Sqrt((double)_pcmSquareTotal / _pcmSamples),
                _pcmPeak,
                _pcmClippedSamples,
                _warmupPcmSamples == 0
                    ? 0
                    : Math.Sqrt((double)_warmupPcmSquareTotal / _warmupPcmSamples),
                _steadyPcmSamples == 0
                    ? 0
                    : Math.Sqrt((double)_steadyPcmSquareTotal / _steadyPcmSamples),
                _opusPackets,
                _opusPayloadLengthMin == int.MaxValue ? 0 : _opusPayloadLengthMin,
                _opusPayloadLengthMax,
                _opusTocKnown ? _firstOpusToc : (byte)0,
                _opusTocKnown ? _lastOpusToc : (byte)0,
                _opusTocChanges,
                _decodedSampleMinimum == int.MaxValue ? 0 : _decodedSampleMinimum,
                _decodedSampleMaximum,
                _decodedSampleTotal,
                _unexpectedDecodedFrameSizes,
                _sessionStarts,
                _sessionStops,
                _lastMute,
                _signal.Snapshot());
        }
    }

    /// <summary>
    /// Emits a final aggregate record if this window was enabled and observed.
    /// </summary>
    internal void LogFinal()
    {
        lock (_sync)
        {
            if (_enabled && _windowStart >= 0 && !_finalLogged)
            {
                MaybeLogLocked(_timestamp(), true);
                _finalLogged = true;
            }
        }
    }

    private bool BeginObservation()
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_sync)
        {
            if (_windowStart < 0)
            {
                _windowStart = _timestamp();
            }

            return IsWindowOpenLocked();
        }
    }

    private bool TryEnterWindow(out long now)
    {
        now = _timestamp();
        if (!_enabled)
        {
            return false;
        }

        lock (_sync)
        {
            if (_windowStart < 0)
            {
                _windowStart = now;
            }

            return IsWindowOpenLocked();
        }
    }

    private bool IsWindowOpenLocked()
    {
        return _enabled && _windowStart >= 0
            && (_windowTicks <= 0 || _timestamp() - _windowStart <= _windowTicks);
    }

    private long ElapsedMillisecondsLocked(long now)
    {
        if (_windowStart < 0)
        {
            return 0;
        }

        return (long)ToMilliseconds(Math.Max(0, now - _windowStart));
    }

    private void ObserveIntervalLocked(
        long now,
        ref long previous,
        ref long minimum,
        ref long maximum,
        ref long gaps)
    {
        if (previous >= 0)
        {
            long interval = Math.Max(0, now - previous);
            minimum = Math.Min(minimum, interval);
            maximum = Math.Max(maximum, interval);
            if (interval > _gapThresholdTicks)
            {
                gaps++;
            }
        }

        previous = now;
    }

    private static void ObserveDurationLocked(
        long duration,
        ref long minimum,
        ref long maximum,
        ref long total)
    {
        duration = Math.Max(0, duration);
        minimum = Math.Min(minimum, duration);
        maximum = Math.Max(maximum, duration);
        total += duration;
    }

    private void ObservePcmLocked(ReadOnlySpan<byte> pcm, bool warmup)
    {
        for (int index = 0; index + 1 < pcm.Length; index += 2)
        {
            short sample = BitConverter.ToInt16(pcm.Slice(index, sizeof(short)));
            int absolute = Math.Abs((int)sample);
            _pcmSamples++;
            if (sample != 0)
            {
                _pcmNonZeroSamples++;
            }

            _pcmSquareTotal += (long)sample * sample;
            if (sample == short.MinValue || sample == short.MaxValue)
            {
                _pcmClippedSamples++;
            }

            if (warmup)
            {
                _warmupPcmSamples++;
                _warmupPcmSquareTotal += (long)sample * sample;
            }
            else
            {
                _steadyPcmSamples++;
                _steadyPcmSquareTotal += (long)sample * sample;
            }

            _pcmPeak = Math.Max(_pcmPeak, absolute);
        }
    }

    private void MaybeLogLocked(long now, bool force)
    {
        if (!_enabled || _windowStart < 0 || (!force && _lastLog >= 0
                && now - _lastLog < _frequency * 2))
        {
            return;
        }

        _lastLog = now;
        BluetoothMicrophoneDiagnosticsSnapshot snapshot = SnapshotLocked(now);
        Log.Info(snapshot.ToString());
    }

    private BluetoothMicrophoneDiagnosticsSnapshot SnapshotLocked(long now)
    {
        return new BluetoothMicrophoneDiagnosticsSnapshot(
            _enabled,
            ElapsedMillisecondsLocked(now),
            _inputReports,
            _inputBytes,
            _normalReports,
            _microphoneCandidates,
            _envelopedCandidates,
            _candidateLengthMin == int.MaxValue ? 0 : _candidateLengthMin,
            _candidateLengthMax,
            _decodedFrames,
            _rejectedFrames,
            _mutedFrames,
            _decoderResets,
            _muteChanges,
            _sinkAttempts,
            _sinkPcmFrames,
            _sinkSilenceFrames,
            _sinkAccepted,
            _sinkFailed,
            _sinkBytes,
            _candidateGaps,
            _decodedGaps,
            _sinkGaps,
            ToMilliseconds(_candidateIntervalMin),
            ToMilliseconds(_candidateIntervalMax),
            ToMilliseconds(_decodedIntervalMin),
            ToMilliseconds(_decodedIntervalMax),
            ToMilliseconds(_sinkIntervalMin),
            ToMilliseconds(_sinkIntervalMax),
            ToMilliseconds(_decodeDurationMin),
            ToMilliseconds(_decodeDurationMax),
            AverageMilliseconds(_decodeDurationTotal, _decodedFrames + _rejectedFrames),
            ToMilliseconds(_sinkDurationMin),
            ToMilliseconds(_sinkDurationMax),
            AverageMilliseconds(_sinkDurationTotal, _sinkAttempts),
            ToMilliseconds(_pipelineDurationMin),
            ToMilliseconds(_pipelineDurationMax),
            AverageMilliseconds(_pipelineDurationTotal, _decodedFrames),
            _pcmSamples,
            _pcmNonZeroSamples,
            _pcmSamples == 0 ? 0 : Math.Sqrt((double)_pcmSquareTotal / _pcmSamples),
            _pcmPeak,
            _pcmClippedSamples,
            _warmupPcmSamples == 0
                ? 0
                : Math.Sqrt((double)_warmupPcmSquareTotal / _warmupPcmSamples),
            _steadyPcmSamples == 0
                ? 0
                : Math.Sqrt((double)_steadyPcmSquareTotal / _steadyPcmSamples),
            _opusPackets,
            _opusPayloadLengthMin == int.MaxValue ? 0 : _opusPayloadLengthMin,
            _opusPayloadLengthMax,
            _opusTocKnown ? _firstOpusToc : (byte)0,
            _opusTocKnown ? _lastOpusToc : (byte)0,
            _opusTocChanges,
            _decodedSampleMinimum == int.MaxValue ? 0 : _decodedSampleMinimum,
            _decodedSampleMaximum,
            _decodedSampleTotal,
            _unexpectedDecodedFrameSizes,
            _sessionStarts,
            _sessionStops,
            _lastMute,
            _signal.Snapshot());
    }

    private double ToMilliseconds(long ticks)
    {
        return ticks == long.MaxValue ? 0 : ticks * 1000.0 / _frequency;
    }

    private double AverageMilliseconds(long ticks, long count)
    {
        return count <= 0 ? 0 : ToMilliseconds(ticks) / count;
    }
}

/// <summary>
/// Immutable aggregate microphone-path metrics. Values are intentionally
/// metadata-only; no raw input report or PCM buffer is exposed.
/// </summary>
internal readonly record struct BluetoothMicrophoneDiagnosticsSnapshot(
    bool Enabled,
    long ElapsedMilliseconds,
    long InputReports,
    long InputBytes,
    long NormalReports,
    long MicrophoneCandidates,
    long EnvelopedCandidates,
    int CandidateLengthMinimum,
    int CandidateLengthMaximum,
    long DecodedFrames,
    long RejectedFrames,
    long MutedFrames,
    long DecoderResets,
    long MuteChanges,
    long SinkAttempts,
    long SinkPcmFrames,
    long SinkSilenceFrames,
    long SinkAccepted,
    long SinkFailed,
    long SinkBytes,
    long CandidateGaps,
    long DecodedGaps,
    long SinkGaps,
    double CandidateIntervalMinimumMilliseconds,
    double CandidateIntervalMaximumMilliseconds,
    double DecodedIntervalMinimumMilliseconds,
    double DecodedIntervalMaximumMilliseconds,
    double SinkIntervalMinimumMilliseconds,
    double SinkIntervalMaximumMilliseconds,
    double DecodeDurationMinimumMilliseconds,
    double DecodeDurationMaximumMilliseconds,
    double DecodeDurationAverageMilliseconds,
    double SinkDurationMinimumMilliseconds,
    double SinkDurationMaximumMilliseconds,
    double SinkDurationAverageMilliseconds,
    double PipelineDurationMinimumMilliseconds,
    double PipelineDurationMaximumMilliseconds,
    double PipelineDurationAverageMilliseconds,
    long PcmSamples,
    long PcmNonZeroSamples,
    double PcmRms,
    int PcmPeak,
    long PcmClippedSamples,
    double WarmupPcmRms,
    double SteadyPcmRms,
    long OpusPackets,
    int OpusPayloadLengthMinimum,
    int OpusPayloadLengthMaximum,
    byte FirstOpusToc,
    byte LastOpusToc,
    long OpusTocChanges,
    int DecodedSampleMinimum,
    int DecodedSampleMaximum,
    long DecodedSampleTotal,
    long UnexpectedDecodedFrameSizes,
    long SessionStarts,
    long SessionStops,
    bool LastMute,
    BluetoothMicrophoneSignalSnapshot Signal)
{
    public override string ToString()
    {
        return $"mic aggregate window_ms={ElapsedMilliseconds} input_reports={InputReports} "
            + $"input_bytes={InputBytes} normal_reports={NormalReports} "
            + $"candidates={MicrophoneCandidates} envelopes={EnvelopedCandidates} "
            + $"candidate_length={CandidateLengthMinimum}..{CandidateLengthMaximum} "
            + $"decoded={DecodedFrames} rejected={RejectedFrames} muted_skip={MutedFrames} "
            + $"decoder_resets={DecoderResets} mute_changes={MuteChanges} "
            + $"candidate_gap={CandidateGaps} decoded_gap={DecodedGaps} "
            + $"sink_gap={SinkGaps} "
            + $"candidate_interval_ms={CandidateIntervalMinimumMilliseconds:F2}..{CandidateIntervalMaximumMilliseconds:F2} "
            + $"decoded_interval_ms={DecodedIntervalMinimumMilliseconds:F2}..{DecodedIntervalMaximumMilliseconds:F2} "
            + $"sink_interval_ms={SinkIntervalMinimumMilliseconds:F2}..{SinkIntervalMaximumMilliseconds:F2} "
            + $"decode_ms={DecodeDurationMinimumMilliseconds:F3}/{DecodeDurationAverageMilliseconds:F3}/{DecodeDurationMaximumMilliseconds:F3} "
            + $"sink_attempts={SinkAttempts} sink_pcm={SinkPcmFrames} sink_silence={SinkSilenceFrames} "
            + $"sink_accepted={SinkAccepted} sink_failed={SinkFailed} sink_bytes={SinkBytes} "
            + $"sink_ms={SinkDurationMinimumMilliseconds:F3}/{SinkDurationAverageMilliseconds:F3}/{SinkDurationMaximumMilliseconds:F3} "
            + $"pipeline_ms={PipelineDurationMinimumMilliseconds:F3}/{PipelineDurationAverageMilliseconds:F3}/{PipelineDurationMaximumMilliseconds:F3} "
            + $"opus_packets={OpusPackets} toc=0x{FirstOpusToc:X2}->0x{LastOpusToc:X2} "
            + $"toc_changes={OpusTocChanges} payload={OpusPayloadLengthMinimum}..{OpusPayloadLengthMaximum} "
            + $"decoded_samples={DecodedSampleMinimum}..{DecodedSampleMaximum}/{DecodedSampleTotal} "
            + $"unexpected_frames={UnexpectedDecodedFrameSizes} "
            + $"pcm_nonzero={PcmNonZeroSamples}/{PcmSamples} pcm_rms={PcmRms:F1} "
            + $"warmup_rms={WarmupPcmRms:F1} steady_rms={SteadyPcmRms:F1} "
            + $"pcm_peak={PcmPeak} clipped={PcmClippedSamples}/{PcmSamples} "
            + $"sessions={SessionStarts}/{SessionStops} muted={LastMute} sequence_gaps=unavailable "
            + "queue_underrun_overflow=unavailable "
            + Signal;
    }
}
