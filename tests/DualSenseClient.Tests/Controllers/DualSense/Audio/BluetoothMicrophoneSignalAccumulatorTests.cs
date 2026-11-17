using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public class BluetoothMicrophoneSignalAccumulatorTests
{
    [Test]
    public void SilenceReportsZeroAggregateAndNoCorrelation()
    {
        BluetoothMicrophoneSignalAccumulator accumulator =
            new BluetoothMicrophoneSignalAccumulator(48000, 2);
        accumulator.ObserveFloat(new float[960]);

        BluetoothMicrophoneSignalSnapshot snapshot = accumulator.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SampleRate, Is.EqualTo(48000));
            Assert.That(snapshot.Channels, Is.EqualTo(2));
            Assert.That(snapshot.Frames, Is.EqualTo(480));
            Assert.That(snapshot.DurationMilliseconds, Is.EqualTo(10).Within(0.001));
            Assert.That(snapshot.ChannelMetrics[0].Rms, Is.EqualTo(0));
            Assert.That(snapshot.ChannelMetrics[1].Peak, Is.EqualTo(0));
            Assert.That(snapshot.LeftRightCorrelation, Is.Null);
            Assert.That(snapshot.LowBandRatio, Is.EqualTo(0));
            Assert.That(snapshot.MidBandRatio, Is.EqualTo(0));
            Assert.That(snapshot.HighBandRatio, Is.EqualTo(0));
        });
    }

    [Test]
    public void UnequalChunksPreserveChannelIdentityAndBandDiscrimination()
    {
        const int sampleRate = 48000;
        const int frameCount = 4800;
        float[] samples = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double time = frame / (double)sampleRate;
            samples[frame * 2] = (float)(Math.Sin(2 * Math.PI * 500 * time) * 0.5);
            samples[frame * 2 + 1] = (float)(Math.Sin(2 * Math.PI * 6000 * time) * 0.5);
        }

        BluetoothMicrophoneSignalAccumulator accumulator =
            new BluetoothMicrophoneSignalAccumulator(sampleRate, 2);
        int offset = 0;
        int[] chunkFrames = [13, 257, 1024, 7, 499, 3000];
        foreach (int frameChunk in chunkFrames)
        {
            int availableFrames = Math.Min(frameChunk, frameCount - offset / 2);
            accumulator.ObserveFloat(samples.AsSpan(offset, availableFrames * 2));
            offset += availableFrames * 2;
            if (offset == samples.Length)
            {
                break;
            }
        }

        BluetoothMicrophoneSignalSnapshot snapshot = accumulator.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Frames, Is.EqualTo(frameCount));
            Assert.That(snapshot.ChannelMetrics[0].Rms, Is.EqualTo(0.3535).Within(0.01));
            Assert.That(snapshot.ChannelMetrics[1].Rms, Is.EqualTo(0.3535).Within(0.01));
            Assert.That(snapshot.ChannelMetrics[0].Peak, Is.EqualTo(0.5).Within(0.01));
            Assert.That(snapshot.ChannelMetrics[1].Peak, Is.EqualTo(0.5).Within(0.01));
            Assert.That(snapshot.ChannelMetrics[0].DcMean, Is.EqualTo(0).Within(0.01));
            Assert.That(snapshot.ChannelMetrics[1].DcMean, Is.EqualTo(0).Within(0.01));
            Assert.That(snapshot.LeftRightCorrelation, Is.Not.Null);
            Assert.That(Math.Abs(snapshot.LeftRightCorrelation!.Value), Is.LessThan(0.2));
            Assert.That(snapshot.LowBandRatio, Is.GreaterThan(snapshot.MidBandRatio));
            Assert.That(snapshot.HighBandRatio, Is.GreaterThan(snapshot.MidBandRatio));
        });
    }

    [Test]
    public void Pcm16TracksClippingAndRejectsPartialFrames()
    {
        BluetoothMicrophoneSignalAccumulator accumulator =
            new BluetoothMicrophoneSignalAccumulator(48000, 2);
        byte[] pcm = new byte[4 * 4];
        for (int frame = 0; frame < 4; frame++)
        {
            short left = frame % 2 == 0 ? short.MaxValue : short.MinValue;
            short right = 8192;
            BitConverter.TryWriteBytes(pcm.AsSpan(frame * 4, 2), left);
            BitConverter.TryWriteBytes(pcm.AsSpan(frame * 4 + 2, 2), right);
        }

        accumulator.ObservePcm16(pcm);
        BluetoothMicrophoneSignalSnapshot snapshot = accumulator.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Frames, Is.EqualTo(4));
            Assert.That(snapshot.ChannelMetrics[0].ClippedSamples, Is.EqualTo(4));
            Assert.That(snapshot.ChannelMetrics[0].Peak, Is.EqualTo(1).Within(0.0001));
            Assert.That(snapshot.ChannelMetrics[1].DcMean, Is.EqualTo(0.25).Within(0.001));
            Assert.That(snapshot.LeftRightCorrelation, Is.Null,
                "a constant right channel has no defined Pearson correlation");
        });

        Assert.Throws<ArgumentException>(() => accumulator.ObservePcm16(new byte[2]));
    }

    [Test]
    public void MonoSignalLeavesLeftRightCorrelationUnavailable()
    {
        BluetoothMicrophoneSignalAccumulator accumulator =
            new BluetoothMicrophoneSignalAccumulator(16000, 1);
        accumulator.ObserveFloat([0.25f, -0.25f, 0.25f, -0.25f]);

        BluetoothMicrophoneSignalSnapshot snapshot = accumulator.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Channels, Is.EqualTo(1));
            Assert.That(snapshot.ChannelMetrics, Has.Count.EqualTo(1));
            Assert.That(snapshot.LeftRightCorrelation, Is.Null);
        });
    }
}
