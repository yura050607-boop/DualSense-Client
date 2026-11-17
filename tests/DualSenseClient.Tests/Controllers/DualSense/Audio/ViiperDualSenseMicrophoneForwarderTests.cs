using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public sealed class ViiperDualSenseMicrophoneForwarderTests
{
    private sealed class FakeSource : IDualSenseMicrophoneSource
    {
        private readonly object _eventLock = new object();
        private Action<byte[]>? _pcmHandlers;
        private Action<bool>? _muteHandlers;
        private readonly List<Action<byte[]>> _capturedPcmHandlers = [];

        public event Action<byte[]>? MicrophonePcmReceived
        {
            add
            {
                lock (_eventLock)
                {
                    _capturedPcmHandlers.Add(value!);
                    _pcmHandlers += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _pcmHandlers -= value;
                }
            }
        }

        public event Action<bool>? MicrophoneMuteChanged
        {
            add
            {
                lock (_eventLock)
                {
                    _muteHandlers += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _muteHandlers -= value;
                }
            }
        }

        public bool MicrophoneMuted { get; private set; }

        public IReadOnlyList<Action<byte[]>> CapturedPcmHandlers
        {
            get
            {
                lock (_eventLock)
                {
                    return _capturedPcmHandlers.ToArray();
                }
            }
        }

        public Action<byte[]>? CurrentPcmHandler
        {
            get
            {
                lock (_eventLock)
                {
                    return _pcmHandlers;
                }
            }
        }

        public void Emit(byte[] frame)
        {
            Action<byte[]>? handlers;
            lock (_eventLock)
            {
                handlers = _pcmHandlers;
            }

            handlers?.Invoke(frame);
        }

        public void SetMuted(bool muted)
        {
            MicrophoneMuted = muted;
            Action<bool>? handlers;
            lock (_eventLock)
            {
                handlers = _muteHandlers;
            }

            handlers?.Invoke(muted);
        }
    }

    [Test]
    public void StartFrameMuteAndStop_UseBoundedExactSilenceLifecycle()
    {
        FakeSource source = new FakeSource();
        List<byte[]> submitted = [];
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, frame =>
            {
                submitted.Add(frame.ToArray());
                return true;
            });
        byte[] frame = Enumerable.Repeat((byte)0x55, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        byte[] silence = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];

        Assert.That(forwarder.Start(), Is.True);
        source.Emit(frame);
        source.SetMuted(true);
        source.Emit(frame);
        source.SetMuted(false);
        source.Emit(frame);
        forwarder.Stop();

        Assert.Multiple(() =>
        {
            int tail = ViiperDualSenseMicrophoneForwarder.SilenceTailFrames;
            Assert.That(submitted, Has.Count.EqualTo(2 + tail + 1 + tail),
                "start silence, two frames, bounded mute tail, and bounded stop tail");
            Assert.That(submitted[0], Is.EqualTo(silence));
            Assert.That(submitted[1], Is.EqualTo(frame));
            Assert.That(submitted.Skip(2).Take(tail).All(item => item.SequenceEqual(silence)), Is.True);
            Assert.That(submitted[2 + tail], Is.EqualTo(frame));
            Assert.That(submitted.Skip(3 + tail).All(item => item.SequenceEqual(silence)), Is.True);
            Assert.That(submitted.All(item => item.Length == BluetoothMicFrameDecoder.PcmFrameBytes), Is.True);
            Assert.That(forwarder.IsActive, Is.False);
        });

        // The source can still raise a stale callback after unsubscription; it must
        // not revive a stopped virtual capture queue.
        source.Emit(frame);
        Assert.That(submitted, Has.Count.EqualTo(2 + ViiperDualSenseMicrophoneForwarder.SilenceTailFrames + 1
                                                  + ViiperDualSenseMicrophoneForwarder.SilenceTailFrames));
    }

    [Test]
    public void StopThenStart_ReestablishesFreshGenerationWithoutBacklog()
    {
        FakeSource source = new FakeSource();
        List<byte[]> submitted = [];
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, frame =>
            {
                submitted.Add(frame.ToArray());
                return true;
            });
        byte[] first = Enumerable.Repeat((byte)0x11, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x22, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();

        Assert.That(forwarder.Start(), Is.True);
        source.Emit(first);
        forwarder.Stop();
        int afterStop = submitted.Count;
        source.Emit(first);
        Assert.That(submitted, Has.Count.EqualTo(afterStop));

        Assert.That(forwarder.Start(), Is.True);
        source.Emit(second);

        Assert.Multiple(() =>
        {
            Assert.That(submitted[^2], Is.EqualTo(new byte[BluetoothMicFrameDecoder.PcmFrameBytes]));
            Assert.That(submitted[^1], Is.EqualTo(second));
            Assert.That(submitted.Count, Is.EqualTo(afterStop + 2));
        });
    }

    [Test]
    public void CapturedOldCallbackAfterStopAndRestart_IsDroppedByGenerationToken()
    {
        FakeSource source = new FakeSource();
        List<byte[]> submitted = [];
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, frame =>
            {
                submitted.Add(frame.ToArray());
                return true;
            });
        byte[] frame = Enumerable.Repeat((byte)0x33, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();

        Assert.That(forwarder.Start(), Is.True);
        Action<byte[]> oldCallback = source.CurrentPcmHandler!;
        forwarder.Stop();
        Assert.That(forwarder.Start(), Is.True);
        int beforeLateCallback = submitted.Count;

        oldCallback(frame);
        Assert.That(submitted, Has.Count.EqualTo(beforeLateCallback),
            "a callback captured before Stop must not write into the restarted generation");

        source.Emit(frame);
        Assert.That(submitted, Has.Count.EqualTo(beforeLateCallback + 1));
        Assert.That(submitted[^1], Is.EqualTo(frame));
    }

    [Test]
    public void ConcurrentStartStop_LeavesOnlyCurrentCallbacksAbleToSubmit()
    {
        FakeSource source = new FakeSource();
        List<byte[]> submitted = [];
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, frame =>
            {
                submitted.Add(frame.ToArray());
                return true;
            });
        byte[] frame = Enumerable.Repeat((byte)0x44, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        using ManualResetEventSlim gate = new ManualResetEventSlim(false);

        Task[] operations = Enumerable.Range(0, 32)
            .Select(index => Task.Run(() =>
            {
                gate.Wait();
                if ((index & 1) == 0)
                {
                    forwarder.Start();
                }
                else
                {
                    forwarder.Stop();
                }
            }))
            .ToArray();
        gate.Set();
        Assert.DoesNotThrow(() => Task.WaitAll(operations));

        Action<byte[]>? current = source.CurrentPcmHandler;
        Action<byte[]>[] oldCallbacks = source.CapturedPcmHandlers
            .Where(callback => !ReferenceEquals(callback, current))
            .ToArray();
        int beforeOldCallbacks = submitted.Count;
        foreach (Action<byte[]> callback in oldCallbacks)
        {
            callback(frame);
        }

        Assert.That(submitted, Has.Count.EqualTo(beforeOldCallbacks),
            "callbacks from every superseded start must be rejected");
        if (current is not null && forwarder.IsActive)
        {
            current(frame);
            Assert.That(submitted, Has.Count.EqualTo(beforeOldCallbacks + 1));
        }
    }

    [Test]
    public void InitialSinkFailure_DeactivatesAndUnsubscribes()
    {
        FakeSource source = new FakeSource();
        int calls = 0;
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, _ =>
            {
                calls++;
                return false;
            });

        Assert.That(forwarder.Start(), Is.False);
        int callsAfterStart = calls;
        source.Emit(new byte[BluetoothMicFrameDecoder.PcmFrameBytes]);

        Assert.Multiple(() =>
        {
            Assert.That(forwarder.IsActive, Is.False);
            Assert.That(calls, Is.EqualTo(callsAfterStart));
        });
    }

    [Test]
    public void LaterSinkFailure_DeactivatesAndUnsubscribes()
    {
        FakeSource source = new FakeSource();
        int calls = 0;
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, _ => ++calls == 1);

        Assert.That(forwarder.Start(), Is.True);
        source.Emit(new byte[BluetoothMicFrameDecoder.PcmFrameBytes]);
        int callsAfterFailure = calls;
        source.Emit(new byte[BluetoothMicFrameDecoder.PcmFrameBytes]);

        Assert.Multiple(() =>
        {
            Assert.That(forwarder.IsActive, Is.False);
            Assert.That(callsAfterFailure, Is.EqualTo(3), "failed PCM plus best-effort silence");
            Assert.That(calls, Is.EqualTo(callsAfterFailure), "failed source must be unsubscribed");
        });
    }

    [Test]
    public void ContinuousFrames_RecordCadenceMuteAndFinalSinkBytesAsAggregates()
    {
        long now = 0;
        BluetoothMicrophoneDiagnostics diagnostics = new BluetoothMicrophoneDiagnostics(
            true,
            () => now,
            1000,
            TimeSpan.FromSeconds(60));
        FakeSource source = new FakeSource();
        List<byte[]> submitted = [];
        using ViiperDualSenseMicrophoneForwarder forwarder =
            new ViiperDualSenseMicrophoneForwarder(source, frame =>
            {
                submitted.Add(frame.ToArray());
                return true;
            }, diagnostics);
        byte[] first = Enumerable.Repeat((byte)0x11, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x22, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        byte[] third = Enumerable.Repeat((byte)0x33, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();
        byte[] fourth = Enumerable.Repeat((byte)0x44, BluetoothMicFrameDecoder.PcmFrameBytes).ToArray();

        void EmitDecoded(long timestamp, byte[] frame)
        {
            now = timestamp;
            diagnostics.ObserveInputReport(BluetoothMicFrameDecoder.BluetoothReportBytes);
            diagnostics.ObserveMicrophoneCandidate(
                BluetoothMicFrameDecoder.BluetoothReportBytes, true);
            diagnostics.ObserveOpusPacketMetadata(
                BluetoothMicFrameDecoder.OpusMarker,
                BluetoothMicFrameDecoder.OpusPayloadBytes);
            diagnostics.ObserveDecodeResult(
                frame,
                true,
                1,
                BluetoothMicFrameDecoder.PcmFrameSamples);
            source.Emit(frame);
        }

        Assert.That(forwarder.Start(), Is.True);
        EmitDecoded(10, first);
        EmitDecoded(20, second);
        EmitDecoded(45, third);

        now = 45;
        diagnostics.ObserveInputReport(BluetoothMicFrameDecoder.BluetoothReportBytes);
        diagnostics.ObserveMicrophoneCandidate(
            BluetoothMicFrameDecoder.BluetoothReportBytes, true);
        diagnostics.ObserveMutedFrame();
        diagnostics.ObserveMuteChange(true);
        source.SetMuted(true);

        now = 55;
        diagnostics.ObserveMuteChange(false);
        source.SetMuted(false);
        EmitDecoded(55, fourth);
        forwarder.Stop();

        BluetoothMicrophoneDiagnosticsSnapshot snapshot = diagnostics.Snapshot();
        int silenceFrames = 1 + ViiperDualSenseMicrophoneForwarder.SilenceTailFrames * 2;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.MicrophoneCandidates, Is.EqualTo(5));
            Assert.That(snapshot.DecodedFrames, Is.EqualTo(4));
            Assert.That(snapshot.MutedFrames, Is.EqualTo(1));
            Assert.That(snapshot.MuteChanges, Is.EqualTo(2));
            Assert.That(snapshot.CandidateGaps, Is.EqualTo(1),
                "the synthetic 25 ms physical gap must be visible without raw audio");
            Assert.That(snapshot.SinkGaps, Is.EqualTo(1));
            Assert.That(snapshot.SinkPcmFrames, Is.EqualTo(4));
            Assert.That(snapshot.SinkSilenceFrames, Is.EqualTo(silenceFrames));
            Assert.That(snapshot.SinkAccepted, Is.EqualTo(4 + silenceFrames));
            Assert.That(snapshot.SinkBytes,
                Is.EqualTo((4 + silenceFrames) * BluetoothMicFrameDecoder.PcmFrameBytes));
            Assert.That(snapshot.PcmNonZeroSamples, Is.EqualTo(snapshot.PcmSamples));
            Assert.That(snapshot.PcmRms, Is.GreaterThan(0));
            Assert.That(snapshot.PcmPeak, Is.GreaterThan(0));
            Assert.That(snapshot.OpusPackets, Is.EqualTo(4));
            Assert.That(snapshot.FirstOpusToc, Is.EqualTo(BluetoothMicFrameDecoder.OpusMarker));
            Assert.That(snapshot.LastOpusToc, Is.EqualTo(BluetoothMicFrameDecoder.OpusMarker));
            Assert.That(snapshot.OpusTocChanges, Is.Zero);
            Assert.That(snapshot.OpusPayloadLengthMinimum,
                Is.EqualTo(BluetoothMicFrameDecoder.OpusPayloadBytes));
            Assert.That(snapshot.OpusPayloadLengthMaximum,
                Is.EqualTo(BluetoothMicFrameDecoder.OpusPayloadBytes));
            Assert.That(snapshot.DecodedSampleMinimum,
                Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
            Assert.That(snapshot.DecodedSampleMaximum,
                Is.EqualTo(BluetoothMicFrameDecoder.PcmFrameSamples));
            Assert.That(snapshot.DecodedSampleTotal,
                Is.EqualTo(4 * BluetoothMicFrameDecoder.PcmFrameSamples));
            Assert.That(snapshot.UnexpectedDecodedFrameSizes, Is.Zero);
            Assert.That(snapshot.PcmClippedSamples, Is.Zero);
            Assert.That(snapshot.Signal.Frames,
                Is.EqualTo(4 * BluetoothMicFrameDecoder.PcmFrameSamples));
            Assert.That(snapshot.Signal.Channels, Is.EqualTo(2));
            Assert.That(snapshot.Signal.ChannelMetrics, Has.Count.EqualTo(2));
            Assert.That(snapshot.Signal.DurationMilliseconds, Is.EqualTo(40).Within(0.001));
            Assert.That(submitted.All(frame =>
                frame.Length == BluetoothMicFrameDecoder.PcmFrameBytes), Is.True);
            Assert.That(submitted[1], Is.EqualTo(first));
            Assert.That(submitted[2], Is.EqualTo(second));
            Assert.That(submitted[3], Is.EqualTo(third));
            Assert.That(submitted[3 + ViiperDualSenseMicrophoneForwarder.SilenceTailFrames + 1],
                Is.EqualTo(fourth));
            Assert.That(submitted[^1], Is.EqualTo(new byte[BluetoothMicFrameDecoder.PcmFrameBytes]));
        });
    }
}
