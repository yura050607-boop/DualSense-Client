using System.Globalization;

namespace DualSenseClient.Controllers.DualSense.Audio;

/// <summary>
/// Computes bounded, aggregate-only statistics for an interleaved audio signal.
/// Samples are consumed immediately and are never retained or emitted.
/// </summary>
/// <remarks>
/// The accumulator is intentionally shared by the Bluetooth decoder diagnostics and
/// the explicit Windows capture probe so both paths use the same normalization and
/// summary conventions. It accepts one through eight channels; the first two
/// channels are treated as L/R for correlation and spectral summaries.
/// </remarks>
public sealed class BluetoothMicrophoneSignalAccumulator
{
    private static readonly double[] SpectralFrequenciesHz = [500d, 2000d, 6000d];

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly long[] _sampleCounts;
    private readonly double[] _sum;
    private readonly double[] _sumSquares;
    private readonly double[] _peak;
    private readonly long[] _clipped;
    private readonly double[] _spectralReal = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _spectralImaginary = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _spectralPower = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _phaseCosine = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _phaseSine = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _stepCosine = new double[SpectralFrequenciesHz.Length];
    private readonly double[] _stepSine = new double[SpectralFrequenciesHz.Length];
    private readonly bool[] _spectralFrequencyAvailable = new bool[SpectralFrequenciesHz.Length];
    private readonly object _sync = new object();
    private readonly int _spectralBlockFrames;
    private long _frames;
    private double _leftRightCross;
    private long _spectralBlockFrameCount;

    /// <summary>
    /// Creates an aggregate calculator for the negotiated signal format.
    /// </summary>
    public BluetoothMicrophoneSignalAccumulator(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels),
                "Only one through eight interleaved channels are supported by the bounded diagnostic.");
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _sampleCounts = new long[channels];
        _sum = new double[channels];
        _sumSquares = new double[channels];
        _peak = new double[channels];
        _clipped = new long[channels];
        _spectralBlockFrames = Math.Max(128, sampleRate / 10);

        for (int index = 0; index < SpectralFrequenciesHz.Length; index++)
        {
            double frequency = SpectralFrequenciesHz[index];
            if (frequency >= sampleRate * 0.5)
            {
                _spectralFrequencyAvailable[index] = false;
                _stepCosine[index] = 1d;
                _stepSine[index] = 0d;
            }
            else
            {
                _spectralFrequencyAvailable[index] = true;
                double phase = 2d * Math.PI * frequency / sampleRate;
                _stepCosine[index] = Math.Cos(phase);
                _stepSine[index] = Math.Sin(phase);
            }

            _phaseCosine[index] = 1d;
            _phaseSine[index] = 0d;
        }
    }

    /// <summary>
    /// Adds interleaved signed 16-bit little-endian PCM using a [-1,1] scale.
    /// </summary>
    public void ObservePcm16(ReadOnlySpan<byte> pcm)
    {
        int bytesPerFrame = checked(_channels * sizeof(short));
        if (pcm.Length % bytesPerFrame != 0)
        {
            throw new ArgumentException(
                "PCM byte count must contain complete interleaved frames.", nameof(pcm));
        }

        lock (_sync)
        {
            Span<float> frame = stackalloc float[8];
            for (int offset = 0; offset < pcm.Length; offset += bytesPerFrame)
            {
                for (int channel = 0; channel < _channels; channel++)
                {
                    short value = BitConverter.ToInt16(pcm.Slice(
                        offset + channel * sizeof(short), sizeof(short)));
                    frame[channel] = value / 32768f;
                }

                ObserveFrameLocked(frame[.._channels]);
            }
        }
    }

    /// <summary>
    /// Adds interleaved floating-point PCM from the SoundFlow capture callback.
    /// </summary>
    public void ObserveFloat(ReadOnlySpan<float> samples)
    {
        if (samples.Length % _channels != 0)
        {
            throw new ArgumentException(
                "The callback sample count must contain complete interleaved frames.", nameof(samples));
        }

        lock (_sync)
        {
            for (int offset = 0; offset < samples.Length; offset += _channels)
            {
                ObserveFrameLocked(samples.Slice(offset, _channels));
            }
        }
    }

    /// <summary>
    /// Returns a snapshot containing only aggregate metadata.
    /// </summary>
    public BluetoothMicrophoneSignalSnapshot Snapshot()
    {
        lock (_sync)
        {
            FlushSpectralBlockLocked();

            BluetoothMicrophoneChannelMetrics[] channels = new BluetoothMicrophoneChannelMetrics[_channels];
            for (int channel = 0; channel < _channels; channel++)
            {
                long count = _sampleCounts[channel];
                channels[channel] = new BluetoothMicrophoneChannelMetrics(
                    count == 0 ? 0d : Math.Sqrt(_sumSquares[channel] / count),
                    _peak[channel],
                    _clipped[channel],
                    count == 0 ? 0d : _sum[channel] / count);
            }

            double? correlation = null;
            if (_channels >= 2 && _sampleCounts[0] > 1 && _sampleCounts[1] == _sampleCounts[0])
            {
                double count = _sampleCounts[0];
                double leftVariance = count * _sumSquares[0] - _sum[0] * _sum[0];
                double rightVariance = count * _sumSquares[1] - _sum[1] * _sum[1];
                double denominator = Math.Sqrt(Math.Max(0d, leftVariance * rightVariance));
                correlation = denominator <= double.Epsilon
                    ? null
                    : Math.Clamp((count * _leftRightCross - _sum[0] * _sum[1]) / denominator, -1d, 1d);
            }

            double totalPower = _spectralPower.Sum();
            double[] spectralRatios = new double[SpectralFrequenciesHz.Length];
            if (totalPower > double.Epsilon)
            {
                for (int index = 0; index < spectralRatios.Length; index++)
                {
                    spectralRatios[index] = _spectralPower[index] / totalPower;
                }
            }

            return new BluetoothMicrophoneSignalSnapshot(
                _sampleRate,
                _channels,
                _frames,
                _frames == 0 ? 0d : _frames * 1000d / _sampleRate,
                channels,
                correlation,
                spectralRatios[0],
                spectralRatios[1],
                spectralRatios[2]);
        }
    }

    private void ObserveFrameLocked(ReadOnlySpan<float> frame)
    {
        _frames++;
        for (int channel = 0; channel < _channels; channel++)
        {
            double sample = frame[channel];
            _sampleCounts[channel]++;
            _sum[channel] += sample;
            _sumSquares[channel] += sample * sample;
            _peak[channel] = Math.Max(_peak[channel], Math.Abs(sample));
            if (Math.Abs(sample) >= 0.999d)
            {
                _clipped[channel]++;
            }
        }

        if (_channels >= 2)
        {
            double left = frame[0];
            double right = frame[1];
            _leftRightCross += left * right;
        }

        int spectralChannels = Math.Min(2, _channels);
        double spectralSample = 0d;
        for (int channel = 0; channel < spectralChannels; channel++)
        {
            spectralSample += frame[channel];
        }

        spectralSample /= spectralChannels;
        for (int index = 0; index < SpectralFrequenciesHz.Length; index++)
        {
            if (!_spectralFrequencyAvailable[index])
            {
                continue;
            }

            _spectralReal[index] += spectralSample * _phaseCosine[index];
            _spectralImaginary[index] += spectralSample * _phaseSine[index];
            double nextCosine = _phaseCosine[index] * _stepCosine[index]
                - _phaseSine[index] * _stepSine[index];
            double nextSine = _phaseSine[index] * _stepCosine[index]
                + _phaseCosine[index] * _stepSine[index];
            _phaseCosine[index] = nextCosine;
            _phaseSine[index] = nextSine;
        }

        _spectralBlockFrameCount++;
        if (_spectralBlockFrameCount >= _spectralBlockFrames)
        {
            FlushSpectralBlockLocked();
        }
    }

    private void FlushSpectralBlockLocked()
    {
        if (_spectralBlockFrameCount == 0)
        {
            return;
        }

        double scale = 1d / (_spectralBlockFrameCount * (double)_spectralBlockFrameCount);
        for (int index = 0; index < SpectralFrequenciesHz.Length; index++)
        {
            if (!_spectralFrequencyAvailable[index])
            {
                continue;
            }

            _spectralPower[index] += (
                _spectralReal[index] * _spectralReal[index]
                + _spectralImaginary[index] * _spectralImaginary[index]) * scale;
            _spectralReal[index] = 0d;
            _spectralImaginary[index] = 0d;
            _phaseCosine[index] = 1d;
            _phaseSine[index] = 0d;
        }

        _spectralBlockFrameCount = 0;
    }
}

/// <summary>
/// Aggregate metadata for one channel of a bounded signal window.
/// </summary>
public readonly record struct BluetoothMicrophoneChannelMetrics(
    double Rms,
    double Peak,
    long ClippedSamples,
    double DcMean)
{
    public override string ToString() =>
        $"rms={Rms.ToString("F6", CultureInfo.InvariantCulture)} "
        + $"peak={Peak.ToString("F6", CultureInfo.InvariantCulture)} "
        + $"clipped={ClippedSamples.ToString(CultureInfo.InvariantCulture)} "
        + $"dc={DcMean.ToString("F6", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Immutable, privacy-preserving signal summary shared by the BT source and exact-ID
/// Windows capture probe. No samples or packet bytes are present.
/// </summary>
public sealed record BluetoothMicrophoneSignalSnapshot(
    int SampleRate,
    int Channels,
    long Frames,
    double DurationMilliseconds,
    IReadOnlyList<BluetoothMicrophoneChannelMetrics> ChannelMetrics,
    double? LeftRightCorrelation,
    double LowBandRatio,
    double MidBandRatio,
    double HighBandRatio)
{
    public override string ToString()
    {
        string channelText = string.Join(
            " ",
            ChannelMetrics.Select((metrics, index) =>
                $"ch{index}_{metrics}"));
        string correlation = LeftRightCorrelation is double value
            ? value.ToString("F6", CultureInfo.InvariantCulture)
            : "unavailable";
        return $"signal rate_hz={SampleRate.ToString(CultureInfo.InvariantCulture)} "
            + $"channels={Channels.ToString(CultureInfo.InvariantCulture)} "
            + $"frames={Frames.ToString(CultureInfo.InvariantCulture)} "
            + $"duration_ms={DurationMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} "
            + channelText
            + $" lr_corr={correlation}"
            + $" band_ratio_low500={LowBandRatio.ToString("F6", CultureInfo.InvariantCulture)}"
            + $" band_ratio_mid2000={MidBandRatio.ToString("F6", CultureInfo.InvariantCulture)}"
            + $" band_ratio_high6000={HighBandRatio.ToString("F6", CultureInfo.InvariantCulture)}";
    }
}
