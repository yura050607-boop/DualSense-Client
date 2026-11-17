using System.Diagnostics;
using System.Collections.Concurrent;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Triggers;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Hid;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public class ViiperDualSenseAudioForwarderTests
{
    /// <summary>
    /// Fake physical-controller audio lane recording every call.
    /// </summary>
    private sealed class FakeAudioOutputs : IDualSenseAudioOutputs
    {
        public ConnectionType ConnectionType { get; init; } = ConnectionType.Bluetooth;
        private volatile bool _microphoneMuted;
        public bool MicrophoneMuted
        {
            get => _microphoneMuted;
            set => _microphoneMuted = value;
        }

        public ConcurrentQueue<byte> PowerSaveControlHistory { get; } = new ConcurrentQueue<byte>();
        public int MicrophoneEnableCallCount { get; private set; }
        public bool LastMicrophoneEnabled { get; private set; }

        public int ResetCount { get; private set; }
        public int PrimeCount { get; private set; }
        public int CombinedReportCount { get; private set; }
        public int AudioOnlyReportCount { get; private set; }
        public int AudioOutputApplyCount { get; private set; }
        public byte? LastAppliedSpeakerVolume { get; private set; }
        public AudioControl? LastAppliedControl { get; private set; }
        public byte LastOpusFrameLength { get; private set; }
        public byte LastHapticsLength { get; private set; }
        public byte[] LastHapticsFrame { get; private set; } = [];
        public bool AnyHapticsNonZero { get; private set; }
        public bool BlockAudioAfterPrime { get; set; }
        public bool BlockMicrophoneDisable { get; set; }
        public ManualResetEventSlim? AudioSendEntered { get; set; }
        public ManualResetEventSlim? ReleaseAudioSend { get; set; }
        public bool BlockDuringPrime { get; set; }
        public ManualResetEventSlim? PrimeEntered { get; set; }
        public ManualResetEventSlim? ReleasePrime { get; set; }
        public int AudioWriteDelayMs { get; set; }

        public int ReportCount
        {
            get
            {
                return CombinedReportCount + AudioOnlyReportCount;
            }
        }

        public byte[] LastStateBlock { get; private set; } = new byte[SetStateData.PayloadSize];
        public byte[] LastPrimeStateBlock { get; private set; } = new byte[SetStateData.PayloadSize];

        public void SetVibration(byte left, byte right)
        {
        }

        public void SendOutputState(SetStateData payload)
        {
        }

        public void ResetBluetoothAudioStream() => ResetCount++;

        public void SetBluetoothMicrophoneEnabled(bool enabled)
        {
            MicrophoneEnableCallCount++;
            LastMicrophoneEnabled = enabled;
        }

        public void SendBluetoothAudioPrime(SetStateData state)
        {
            PrimeCount++;
            state.CopyTo(LastPrimeStateBlock, 0);
            if (BlockDuringPrime)
            {
                PrimeEntered?.Set();
                ReleasePrime?.Wait(TimeSpan.FromSeconds(5));
            }
        }

        public void SendBluetoothAudioAndHaptics(SetStateData state, ReadOnlySpan<byte> opusFrame, ReadOnlySpan<byte> hapticsPcm, BluetoothAudioRoute route)
        {
            if (AudioWriteDelayMs > 0)
            {
                Thread.Sleep(AudioWriteDelayMs);
            }

            bool microphoneDisable = (state.AudioControl & AudioControl.MicSelectMask) != AudioControl.MicSelectInternal;
            if (((BlockAudioAfterPrime && PrimeCount > 0 && CombinedReportCount >= 8)
                 || (BlockMicrophoneDisable && PrimeCount > 0 && CombinedReportCount >= 8 && microphoneDisable)))
            {
                AudioSendEntered?.Set();
                ReleaseAudioSend?.Wait(TimeSpan.FromSeconds(5));
            }

            CombinedReportCount++;
            state.CopyTo(LastStateBlock, 0);
            PowerSaveControlHistory.Enqueue(state.PowerSaveControl);
            LastOpusFrameLength = (byte)opusFrame.Length;
            LastHapticsLength = (byte)hapticsPcm.Length;
            LastHapticsFrame = hapticsPcm.ToArray();
            AnyHapticsNonZero |= LastHapticsFrame.Any(b => b != 0);
        }

        public void SendBluetoothAudio(ReadOnlySpan<byte> opusFrame, BluetoothAudioRoute route)
        {
            if (AudioWriteDelayMs > 0)
            {
                Thread.Sleep(AudioWriteDelayMs);
            }

            AudioOnlyReportCount++;
            LastOpusFrameLength = (byte)opusFrame.Length;
        }

        public void SetAudioOutput(AudioControl outputControl, byte speakerVolume, byte headphoneVolume)
        {
            AudioOutputApplyCount++;
            LastAppliedControl = outputControl;
            LastAppliedSpeakerVolume = speakerVolume;
        }
    }

    private static readonly float[] AudioBlock = MakeAudioBlock(0.5f);

    /// <summary>
    /// Builds a 398-byte vDS-style combined Bluetooth report carrying the given haptics
    /// payload: <c>0x36</c> id, <c>0x91 0x07 0xFE</c> session block, <c>0x90 0x3F</c>
    /// state block, haptics at offset 78.
    /// </summary>
    private static DSOutputState MakeCombinedReport(byte[] haptics, byte reportId = 0x36)
    {
        byte[] combined = new byte[398];
        combined[0] = reportId;
        combined[2] = 0x91;
        combined[3] = 0x07;
        combined[4] = 0xFE;
        combined[11] = 0x90;
        combined[12] = 0x3F;
        haptics.CopyTo(combined, 78);
        return new DSOutputState
        {
            BluetoothCombinedOutputReport = combined
        };
    }

    /// <summary>
    /// Builds one 512-frame stereo block with a constant amplitude on both channels.
    /// </summary>
    private static float[] MakeAudioBlock(float amplitude)
    {
        float[] block = new float[DualSenseBtAudioPipeline.FramesPerBlock * 2];
        Array.Fill(block, amplitude);
        return block;
    }

    private static short[] MakeNativeHapticsBlock(short left, short right)
    {
        short[] block = new short[DualSenseBtAudioPipeline.FramesPerBlock * 2];
        for (int i = 0; i < DualSenseBtAudioPipeline.FramesPerBlock; i++)
        {
            block[i * 2] = left;
            block[i * 2 + 1] = right;
        }

        return block;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout elapses.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Thread.Sleep(5);
        }
    }

    [Test]
    public void Start_LeavesBluetoothLaneClosedUntilAudioIsFed()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);

        Assert.That(forwarder.Start(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(fake.ResetCount, Is.Zero, "Start must not open the Bluetooth audio lane");
            Assert.That(fake.PrimeCount, Is.Zero, "Start must not open the Bluetooth audio lane");
            Assert.That(fake.AudioOutputApplyCount, Is.Zero, "Start must not touch the audio configuration");
            Assert.That(fake.ReportCount, Is.Zero, "Start must not send any reports");
        });

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.ResetCount, Is.EqualTo(1), "the first audio block must open the stream");
            Assert.That(fake.PrimeCount, Is.EqualTo(1));
            Assert.That(fake.AudioOutputApplyCount, Is.EqualTo(1));
            Assert.That(fake.LastAppliedControl, Is.EqualTo(AudioControl.OutputPathSpeaker));
            Assert.That(fake.LastAppliedSpeakerVolume, Is.EqualTo(0x50));
            Assert.That(fake.ReportCount, Is.GreaterThanOrEqualTo(8), "the first block must warm up the stream with the 8 silence reports");
        });

        forwarder.Stop();
    }

    [Test]
    public void SlowBluetoothWrites_AreSeparatedFromForwarderComputeTiming()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth,
            AudioWriteDelayMs = 15
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null, diagnostics);

        Assert.That(forwarder.Start(), Is.True);
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(
            () => diagnostics.GetMetricSnapshot(AudioTimingMetric.BluetoothAudioWrite).Samples >= 9,
            TimeSpan.FromSeconds(3));
        forwarder.Stop();

        AudioTimingMetricSnapshot write = diagnostics.GetMetricSnapshot(AudioTimingMetric.BluetoothAudioWrite);
        AudioTimingMetricSnapshot compute = diagnostics.GetMetricSnapshot(AudioTimingMetric.ForwarderCompute);
        Assert.Multiple(() =>
        {
            Assert.That(write.Samples, Is.GreaterThanOrEqualTo(9), "eight preroll reports and the first audio report must be measured");
            Assert.That(write.MaxStopwatchTicks, Is.GreaterThanOrEqualTo(Stopwatch.Frequency / 100), "the fake physical writes deliberately exceed 10 ms");
            Assert.That(compute.Samples, Is.GreaterThanOrEqualTo(1));
            Assert.That(compute.MaxStopwatchTicks, Is.LessThan(write.MaxStopwatchTicks), "the synthetic delay must remain in the physical-write metric, not compute time");
        });
    }

    [Test]
    public async Task ExistingForwarder_UsesManualCaptureStartedAfterConstruction_AndWritesOnlyAggregates()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-audio-timing-attempt41-forwarder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        NativeAudioTimingDiagnosticsManager? manager = null;
        try
        {
            manager = NativeAudioTimingDiagnosticsManager.CreateForTesting(
                root,
                Stopwatch.GetTimestamp,
                () => "attempt41-test",
                testClock: false,
                startPublisher: true);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            FakeAudioOutputs fake = new();
            using ViiperDualSenseAudioForwarder forwarder = new(fake, null, router);

            Assert.That(forwarder.Start(), Is.True);
            forwarder.FeedPcm(AudioBlock);
            WaitUntil(() => fake.ReportCount >= 9, TimeSpan.FromSeconds(3));
            Assert.That(control.GetStatus().State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Off));
            Assert.That(Directory.Exists(Path.Combine(root, "Logs")), Is.False,
                "a disabled, already-constructed production object must not create a log");

            int reportsBeforeCapture = fake.ReportCount;
            NativeAudioTimingDiagnosticsStatus started = await control.StartAsync(3);
            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;
            forwarder.FeedPcm(AudioBlock);
            WaitUntil(
                () => fake.ReportCount > reportsBeforeCapture
                      && session.GetMetricSnapshot(AudioTimingMetric.BluetoothAudioWrite).Samples > 0,
                TimeSpan.FromSeconds(3));

            await control.StopAsync();
            await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
            string log = File.ReadAllText(session.LogPath!);

            Assert.Multiple(() =>
            {
                Assert.That(started.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
                Assert.That(Path.GetFileName(session.LogPath), Does.StartWith("native-audio-timing-attempt41-"));
                Assert.That(session.GetMetricSnapshot(AudioTimingMetric.BluetoothAudioWrite).Samples, Is.GreaterThan(0));
                Assert.That(log, Does.Contain("event=start").And.Contain("event=summary").And.Contain("event=stop"));
                Assert.That(log, Does.Contain("bluetooth_audio_write="));
                Assert.That(log, Does.Not.Contain("pcm="));
                Assert.That(log, Does.Not.Contain("voice="));
                Assert.That(log, Does.Not.Contain("hid="));
                Assert.That(log, Does.Not.Contain("head="));
            });
        }
        finally
        {
            if (manager is not null)
            {
                NativeAudioTimingDiagnostics? session = manager.GetSessionForTesting();
                if (session is not null && session.IsSessionActive)
                {
                    await new NativeAudioTimingDiagnosticsControl(manager).StopAsync();
                }

                if (session is not null)
                {
                    await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void FeedPcm_RecordsBoundedQueueDepthAndDroppedBlocks()
    {
        using ManualResetEventSlim primeEntered = new ManualResetEventSlim(false);
        using ManualResetEventSlim releasePrime = new ManualResetEventSlim(false);
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth,
            BlockDuringPrime = true,
            PrimeEntered = primeEntered,
            ReleasePrime = releasePrime
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null, diagnostics);

        Assert.That(forwarder.Start(), Is.True);
        forwarder.FeedPcm(AudioBlock);
        bool entered = primeEntered.Wait(TimeSpan.FromSeconds(3));
        if (!entered)
        {
            releasePrime.Set();
            Assert.Fail("The pump did not enter the deterministic blocked-prime window.");
        }

        float[] burst = new float[AudioBlock.Length * 34];
        forwarder.FeedPcm(burst);
        AudioTimingQueueSnapshot burstQueue = diagnostics.GetQueueSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(burstQueue.Samples, Is.EqualTo(2));
            Assert.That(burstQueue.MaxDepth, Is.EqualTo(32));
            Assert.That(burstQueue.DroppedBlocks, Is.EqualTo(3),
                "the initial queued block plus one FeedPcm callback can overflow the ring multiple times");
        });

        for (int i = 0; i < 40; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }

        releasePrime.Set();
        WaitUntil(() => diagnostics.GetQueueSnapshot().DroppedBlocks > 43, TimeSpan.FromSeconds(4));
        AudioTimingQueueSnapshot queue = diagnostics.GetQueueSnapshot();
        forwarder.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(queue.Samples, Is.EqualTo(42));
            Assert.That(queue.MaxDepth, Is.EqualTo(32));
            Assert.That(queue.DroppedBlocks, Is.GreaterThan(43), "pump catch-up discards beyond ring-overflow drops must also be counted");
        });
    }

    [Test]
    public void BlockingPhysicalAudioWrite_DoesNotHoldForwarderStateLock()
    {
        using ManualResetEventSlim audioSendEntered = new ManualResetEventSlim(false);
        using ManualResetEventSlim releaseAudioSend = new ManualResetEventSlim(false);
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            BlockAudioAfterPrime = true,
            AudioSendEntered = audioSendEntered,
            ReleaseAudioSend = releaseAudioSend
        };

        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();
        forwarder.FeedPcm(AudioBlock);
        Assert.That(audioSendEntered.Wait(TimeSpan.FromSeconds(3)), Is.True,
            "the fake physical lane must hold one steady-state report");

        Task stateUpdate = Task.Run(() => forwarder.UpdateGameOutputState(new SetStateData()));
        Task audioFeed = Task.Run(() => forwarder.FeedPcm(AudioBlock));
        try
        {
            Assert.That(stateUpdate.Wait(TimeSpan.FromMilliseconds(500)), Is.True,
                "a synchronous physical write must not block the native output-state callback");
            Assert.That(audioFeed.Wait(TimeSpan.FromMilliseconds(500)), Is.True,
                "a synchronous physical write must not block the host audio callback");
        }
        finally
        {
            releaseAudioSend.Set();
            Task.WaitAll(stateUpdate, audioFeed);
        }
    }

    [Test]
    public void BlockingMicrophoneDisable_PreservesFixed17StateLockBoundary()
    {
        using ManualResetEventSlim audioSendEntered = new ManualResetEventSlim(false);
        using ManualResetEventSlim releaseAudioSend = new ManualResetEventSlim(false);
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            BlockMicrophoneDisable = true,
            AudioSendEntered = audioSendEntered,
            ReleaseAudioSend = releaseAudioSend
        };

        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };
        forwarder.Start();
        WaitUntil(() => fake.PrimeCount == 1 && fake.CombinedReportCount >= 8, TimeSpan.FromSeconds(3));

        Task stop = Task.Run(forwarder.Stop);
        Task? stateUpdate = null;
        Task? audioFeed = null;
        try
        {
            Assert.That(audioSendEntered.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "Stop must reach the blocked microphone-disable report");

            stateUpdate = Task.Run(() => forwarder.UpdateGameOutputState(new SetStateData()));
            audioFeed = Task.Run(() => forwarder.FeedPcm(AudioBlock));
            Assert.Multiple(() =>
            {
                Assert.That(stateUpdate.Wait(TimeSpan.FromMilliseconds(500)), Is.False,
                    "fixed17 holds the state lock while its final microphone-disable report is written");
                Assert.That(audioFeed.Wait(TimeSpan.FromMilliseconds(500)), Is.False,
                    "fixed17 blocks the host audio callback behind the same teardown write");
                Assert.That(stop.IsCompleted, Is.False,
                    "Stop should still be waiting for the blocked disable report");
            });
        }
        finally
        {
            releaseAudioSend.Set();
            if (stateUpdate is not null && audioFeed is not null)
            {
                Task.WaitAll(stateUpdate, audioFeed);
            }

            stop.Wait(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public void BlockingBluetoothPrime_DoesNotHoldForwarderStateLock()
    {
        using ManualResetEventSlim primeEntered = new ManualResetEventSlim(false);
        using ManualResetEventSlim releasePrime = new ManualResetEventSlim(false);
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            BlockDuringPrime = true,
            PrimeEntered = primeEntered,
            ReleasePrime = releasePrime
        };

        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();
        forwarder.FeedPcm(AudioBlock);
        Assert.That(primeEntered.Wait(TimeSpan.FromSeconds(3)), Is.True,
            "the fake physical lane must hold the first Bluetooth prime");

        Task audioFeed = Task.Run(() => forwarder.FeedPcm(AudioBlock));
        try
        {
            Assert.That(audioFeed.Wait(TimeSpan.FromMilliseconds(500)), Is.True,
                "Bluetooth priming must not block the native virtual-USB audio callback");
        }
        finally
        {
            releasePrime.Set();
            Task.WaitAll(audioFeed);
        }
    }

    [Test]
    public void Flush_DoesNotHoldForwarderStateLockWhileWaitingForPhysicalWrite()
    {
        using ManualResetEventSlim audioSendEntered = new ManualResetEventSlim(false);
        using ManualResetEventSlim releaseAudioSend = new ManualResetEventSlim(false);
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            BlockAudioAfterPrime = true,
            AudioSendEntered = audioSendEntered,
            ReleaseAudioSend = releaseAudioSend
        };

        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();
        forwarder.FeedPcm(AudioBlock);
        Assert.That(audioSendEntered.Wait(TimeSpan.FromSeconds(3)), Is.True,
            "the fake physical lane must hold one steady-state report");

        Task flush = Task.Run(() => forwarder.Flush());
        Thread.Sleep(50);
        Task stateUpdate = Task.Run(() => forwarder.UpdateGameOutputState(new SetStateData()));
        Task audioFeed = Task.Run(() => forwarder.FeedPcm(AudioBlock));
        try
        {
            Assert.That(stateUpdate.Wait(TimeSpan.FromMilliseconds(500)), Is.True,
                "Flush must not hold the forwarder state lock while waiting for physical I/O");
            Assert.That(audioFeed.Wait(TimeSpan.FromMilliseconds(500)), Is.True,
                "Flush must not block the host audio callback while waiting for physical I/O");
            Assert.That(flush.IsCompleted, Is.False,
                "Flush should still be waiting for the blocked physical report");
        }
        finally
        {
            releaseAudioSend.Set();
            Task.WaitAll(stateUpdate, audioFeed, flush);
        }
    }

    [Test]
    public void LongIdleGap_RePrimesTheStreamOnResume()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount > 0 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        Thread.Sleep(400);

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.PrimeCount >= 2, TimeSpan.FromSeconds(3));

        Assert.That(fake.PrimeCount, Is.GreaterThanOrEqualTo(2), "resuming after a long idle gap must re-open the audio lane");

        forwarder.Stop();
    }

    [Test]
    public void Start_NoTransportReturnsFalse()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Usb
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);

        Assert.That(forwarder.Start(), Is.False);
    }

    [Test]
    public void FeedPcm_SendsCombinedAudioAndHapticsReports()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        for (int i = 0; i < 4; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }

        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.ReportCount, Is.GreaterThanOrEqualTo(12), "8 preroll reports plus the 4 fed blocks must all be sent");
            Assert.That(fake.CombinedReportCount, Is.EqualTo(fake.ReportCount), "haptics enabled must fold into the combined report");
            Assert.That(fake.AudioOnlyReportCount, Is.Zero);
            Assert.That(fake.LastOpusFrameLength, Is.EqualTo(200));
            Assert.That(fake.LastHapticsLength, Is.EqualTo(64));
            Assert.That(fake.AnyHapticsNonZero, Is.False, "speaker audio must not be promoted to haptics when no native haptics source was supplied");
        });

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_HapticsDisabledSendsAudioOnlyReports()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.HapticsEnabled = false;
        forwarder.Start();

        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }

        WaitUntil(() => fake.ReportCount >= 11, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.AudioOnlyReportCount, Is.GreaterThanOrEqualTo(11), "8 preroll reports plus the 3 fed blocks must all be sent audio-only");
            Assert.That(fake.CombinedReportCount, Is.Zero);
            Assert.That(fake.LastOpusFrameLength, Is.EqualTo(200));
        });

        forwarder.Stop();
    }

    [Test]
    public void MicrophoneOptIn_PrimesDuplexOnceWithoutSpeakerAudioAndDisablesOnStop()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };

        Assert.That(forwarder.Start(), Is.True);
        WaitUntil(() => fake.PrimeCount == 1 && fake.CombinedReportCount >= 8, TimeSpan.FromSeconds(3));

        int reportsBeforeStop = fake.CombinedReportCount;
        Assert.Multiple(() =>
        {
            Assert.That(reportsBeforeStop, Is.GreaterThanOrEqualTo(8));
            Assert.That(fake.MicrophoneEnableCallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(fake.LastMicrophoneEnabled, Is.True);
            Assert.That(fake.LastStateBlock[7] & (byte)AudioControl.MicSelectInternal,
                Is.EqualTo((byte)AudioControl.MicSelectInternal));
            Assert.That(fake.LastStateBlock[6], Is.EqualTo(0x40));
        });

        Thread.Sleep(150);
        Assert.That(fake.CombinedReportCount, Is.EqualTo(reportsBeforeStop),
            "a microphone-only idle session must not emit recurring speaker-silence reports");

        forwarder.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(fake.MicrophoneEnableCallCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(fake.LastMicrophoneEnabled, Is.False);
            Assert.That(fake.CombinedReportCount, Is.GreaterThan(reportsBeforeStop),
                "stop must send one duplex-off silence report");
        });
    }

    [Test]
    public void MicrophoneOnlyCapture_LeadingSilenceStaysIdle_ButActiveSilenceAndNativeClearArePreserved()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };
        float[] silence = MakeAudioBlock(0f);
        short[] zeroHaptics = new short[silence.Length];

        Assert.That(forwarder.Start(), Is.True);
        WaitUntil(() => fake.PrimeCount == 1 && fake.CombinedReportCount >= 8,
            TimeSpan.FromSeconds(3));
        int prerollReports = fake.CombinedReportCount;

        // This is the capture shape observed in the quality1 run: repeated host
        // render blocks are digitally silent while the physical mic is enabled.
        for (int i = 0; i < 5; i++)
        {
            forwarder.FeedPcm(silence, zeroHaptics);
        }

        Thread.Sleep(150);
        Assert.That(fake.CombinedReportCount, Is.EqualTo(prerollReports),
            "leading capture silence must not become a recurring Bluetooth speaker clock");

        // A native-haptics-only block is real activity even when the speaker pair is
        // silent; it must open the lane and reach the physical haptics payload.
        short[] activeHaptics = MakeNativeHapticsBlock(8192, -8192);
        forwarder.FeedPcm(silence, activeHaptics);
        WaitUntil(() => fake.CombinedReportCount > prerollReports
                        && fake.LastHapticsFrame.Any(value => value != 0),
            TimeSpan.FromSeconds(3));
        int activeReports = fake.CombinedReportCount;

        // Once activity has started, an all-zero block is a truthful silent interval
        // and the native clear-to-neutral payload must not be discarded.
        forwarder.FeedPcm(silence, zeroHaptics);
        WaitUntil(() => fake.CombinedReportCount > activeReports
                        && fake.LastHapticsFrame.All(value => value == 0),
            TimeSpan.FromSeconds(3));
        Assert.That(fake.CombinedReportCount, Is.GreaterThan(activeReports));

        // Toggling mic off still re-primes with the duplex request cleared; the
        // existing lifecycle path, rather than the silence gate, owns that transition.
        forwarder.MicrophoneEnabled = false;
        forwarder.FeedPcm(silence, zeroHaptics);
        WaitUntil(() => fake.MicrophoneEnableCallCount >= 2 && !fake.LastMicrophoneEnabled,
            TimeSpan.FromSeconds(3));
        Assert.That(fake.LastMicrophoneEnabled, Is.False);

        forwarder.Stop();
    }

    [Test]
    public void MicrophoneOptIn_ResumesDuplexReportsWhenSpeakerAudioArrives()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };

        Assert.That(forwarder.Start(), Is.True);
        WaitUntil(() => fake.PrimeCount == 1 && fake.CombinedReportCount >= 8, TimeSpan.FromSeconds(3));
        int reportsBeforeAudio = fake.CombinedReportCount;

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.CombinedReportCount > reportsBeforeAudio, TimeSpan.FromSeconds(3));

        Assert.That(fake.PrimeCount, Is.EqualTo(1),
            "a short microphone-only idle gap must not reopen the Bluetooth stream");

        forwarder.Stop();
    }

    [Test]
    public void MicrophoneMute_PreservesGamePowerBitsAcrossRepeatedDuplexTicks()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };
        forwarder.UpdateGameOutputState(new SetStateData
        {
            PowerSaveControl = 0x04
        });

        Assert.That(forwarder.Start(), Is.True);
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= 8, TimeSpan.FromSeconds(3));
        Assert.That(fake.PowerSaveControlHistory.All(value => value == 0x04), Is.True,
            "an unmuted microphone session must preserve unrelated game power-save bits");

        int beforeMute = fake.PowerSaveControlHistory.Count;
        fake.MicrophoneMuted = true;
        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= beforeMute + 3,
            TimeSpan.FromSeconds(3));
        byte[] mutedTicks = fake.PowerSaveControlHistory.Skip(beforeMute).ToArray();
        Assert.That(mutedTicks.Length, Is.GreaterThanOrEqualTo(3));
        Assert.That(mutedTicks.All(value => value == 0x14), Is.True,
            "repeated duplex ticks must keep the physical mute bit set without clobbering game bits");

        int beforeUnmute = fake.PowerSaveControlHistory.Count;
        fake.MicrophoneMuted = false;
        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= beforeUnmute + 3,
            TimeSpan.FromSeconds(3));
        byte[] unmutedTicks = fake.PowerSaveControlHistory.Skip(beforeUnmute).ToArray();
        Assert.That(unmutedTicks.Length, Is.GreaterThanOrEqualTo(3));
        Assert.That(unmutedTicks.All(value => value == 0x04), Is.True,
            "repeated duplex ticks must clear only the owned physical mute bit");

        forwarder.Stop();
    }

    [Test]
    public void HostMicMuteRequest_RemainsSetWhenPhysicalButtonIsReleased()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null)
        {
            MicrophoneEnabled = true
        };
        forwarder.UpdateGameOutputState(new SetStateData
        {
            PowerSaveControl = 0x14
        });

        Assert.That(forwarder.Start(), Is.True);
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= 3, TimeSpan.FromSeconds(3));
        Assert.That(fake.PowerSaveControlHistory.All(value => value == 0x14), Is.True,
            "the host mute request must not be cleared while the physical mic is unmuted");

        fake.MicrophoneMuted = true;
        int beforeRelease = fake.PowerSaveControlHistory.Count;
        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= beforeRelease + 3,
            TimeSpan.FromSeconds(3));
        fake.MicrophoneMuted = false;
        int beforeUnmuted = fake.PowerSaveControlHistory.Count;
        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }
        WaitUntil(() => fake.PowerSaveControlHistory.Count >= beforeUnmuted + 3,
            TimeSpan.FromSeconds(3));
        Assert.That(fake.PowerSaveControlHistory.Skip(beforeUnmuted).All(value => value == 0x14), Is.True,
            "releasing the physical button must not clear the host mute request");

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_PairedNativeHapticsAreForwardedWhenSpeakerAudioIsSilent()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        short[] nativeHaptics = MakeNativeHapticsBlock(8192, -8192);
        for (int i = 0; i < 4; i++)
        {
            forwarder.FeedPcm(MakeAudioBlock(0f), nativeHaptics);
        }

        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastHapticsFrame.Length, Is.EqualTo(64));
            Assert.That(fake.LastHapticsFrame.Where((_, i) => i % 2 == 0).All(b => b == 0x20), Is.True,
                "the native left haptics channel must be decimated without using speaker audio");
            Assert.That(fake.LastHapticsFrame.Where((_, i) => i % 2 == 1).All(b => b == 0xE0), Is.True,
                "the native right haptics channel must be decimated without using speaker audio");
        });

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_PairedNativeSilenceRemainsSilentWithNonSilentSpeakerAudio()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.FeedPcm(AudioBlock, new short[AudioBlock.Length]);
        WaitUntil(() => fake.ReportCount >= 9, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "a valid all-zero native haptics block is authoritative silence");

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_PartialBlocksAreAccumulatedAndChunked()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        int halfBlock = DualSenseBtAudioPipeline.FramesPerBlock;
        forwarder.FeedPcm(AudioBlock.AsSpan(0, halfBlock));
        forwarder.FeedPcm(AudioBlock.AsSpan(0, halfBlock));
        forwarder.FeedPcm(AudioBlock);

        WaitUntil(() => fake.ReportCount >= 10, TimeSpan.FromSeconds(3));

        Assert.That(fake.ReportCount, Is.GreaterThanOrEqualTo(10), "two half blocks plus a full block must yield two reports on top of the 8-report preroll");

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_VolumeChangeAppliesWithinOneTick()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.SpeakerVolume = 0x90;
        forwarder.FeedPcm(AudioBlock);

        WaitUntil(() => fake.AudioOutputApplyCount >= 1 && fake.LastAppliedSpeakerVolume == 0x90, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastAppliedSpeakerVolume, Is.EqualTo(0x90), "the volume set before the first block must be applied when the stream opens");
            Assert.That(fake.LastAppliedControl, Is.EqualTo(AudioControl.OutputPathSpeaker));
        });

        forwarder.SpeakerVolume = 0x70;
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.LastAppliedSpeakerVolume == 0x70, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastAppliedSpeakerVolume, Is.EqualTo(0x70), "a volume change during streaming must be applied within one tick");

        forwarder.Stop();
    }

    [Test]
    public void Stop_NoFurtherReportsAreSent()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount > 8, TimeSpan.FromSeconds(3));
        forwarder.Stop();
        int countAtStop = fake.ReportCount;
        Assert.That(countAtStop, Is.GreaterThan(8));

        forwarder.FeedPcm(AudioBlock);
        Thread.Sleep(150);

        Assert.That(fake.ReportCount, Is.EqualTo(countAtStop), "feeding after stop must be ignored");
    }

    [Test]
    public void Dispose_StopsThePumpAndReleases()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount > 8, TimeSpan.FromSeconds(3));
        int reportsBeforeDispose = fake.ReportCount;
        Assert.That(reportsBeforeDispose, Is.GreaterThan(8));

        forwarder.Dispose();

        Assert.That(forwarder.IsActive, Is.False);
        forwarder.FeedPcm(AudioBlock);
        Thread.Sleep(100);
        Assert.That(fake.ReportCount, Is.EqualTo(reportsBeforeDispose), "no reports may be sent after dispose");
    }

    [Test]
    public void UpdateGameOutputState_StateRidesTheCombinedAndPrimeReports()
    {
        byte[] right = new byte[11];
        right[0] = (byte)TriggerEffectType.Trigger;
        byte[] left = new byte[11];
        left[0] = (byte)TriggerEffectType.Automatic;

        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameOutputState(new SetStateData
        {
            ValidFlag0 = ValidFlags.UseRumbleNotHaptics | ValidFlags.EnableRumbleEmulation
                                                        | ValidFlags.AllowRightTriggerFfb | ValidFlags.AllowLeftTriggerFfb,
            ValidFlag2 = ValidFlags.EnableImprovedRumbleEmu,
            RumbleLeft = 0x90,
            RumbleRight = 0x40,
            MuteLedMode = 0x01,
            PlayerLeds = (PlayerLedMask)0x0A,
            LedRed = 0x12,
            LedGreen = 0x34,
            LedBlue = 0x56,
            R2TriggerEffect = new TriggerEffectBlock(right, 0),
            L2TriggerEffect = new TriggerEffectBlock(left, 0)
        });
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            byte[] prime = fake.LastPrimeStateBlock;
            Assert.That(prime[0], Is.EqualTo(0xBC), "the init-prime must carry the merged flags (rumble bits cleared, triggers and audio enabled)");
            Assert.That(prime[3], Is.EqualTo(0x90), "the init-prime must carry the game's rumble bytes");
            Assert.That(prime[44], Is.EqualTo(0x12), "the init-prime must carry the lightbar");
            Assert.That(prime[10], Is.EqualTo((byte)TriggerEffectType.Trigger), "the init-prime must carry the R2 trigger block");

            byte[] state = fake.LastStateBlock;
            Assert.That(state[0] & 0x03, Is.Zero, "the rumble-mode bits must be cleared while the audio lane is open");
            Assert.That(state[0] & 0x0C, Is.EqualTo(0x0C), "the trigger enable bits must stay set so the pad applies the blocks");
            Assert.That(state[0] & 0xB0, Is.EqualTo(0xB0), "the audio enable bits must be set");
            Assert.That(state[38] & 0x04, Is.Zero, "improved rumble emulation must be cleared while the lane is open");
            Assert.That(state[2], Is.EqualTo(0x40), "the rumble bytes must ride the combined state block");
            Assert.That(state[3], Is.EqualTo(0x90));
            Assert.That(state[8], Is.EqualTo(0x01), "the mic LED mode must ride the state block");
            Assert.That(state[43], Is.EqualTo(0x0A), "the player LED mask must ride the state block");
            Assert.That(state[44], Is.EqualTo(0x12), "the lightbar must ride the state block");
            Assert.That(state[45], Is.EqualTo(0x34));
            Assert.That(state[46], Is.EqualTo(0x56));
            Assert.That(state[10], Is.EqualTo((byte)TriggerEffectType.Trigger), "the R2 trigger block must ride the state block");
            Assert.That(state[21], Is.EqualTo((byte)TriggerEffectType.Automatic), "the L2 trigger block must ride the state block");
            Assert.That(state[4], Is.EqualTo(0x3F), "the headphone volume must be overridden for the speaker route");
            Assert.That(state[5], Is.EqualTo(0x50), "the speaker volume must be overridden");
            Assert.That(state[7], Is.EqualTo((byte)AudioControl.OutputPathSpeaker), "the audio control must be overridden");
            Assert.That(state[37], Is.EqualTo(0x02), "audio control 2 must be overridden");
        });

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameOutputState_GameDisabledTriggerBitsStayClearInTheEmbeddedState()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        // The game stopped driving the triggers: its allow bits are clear, so the pad
        // must retain its current effect instead of re-applying stale block bytes.
        forwarder.UpdateGameOutputState(new SetStateData
        {
            ValidFlag0 = ValidFlags.AllowSpeakerVolume,
            R2TriggerEffect = new TriggerEffectBlock([0x26, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0)
        });
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        byte[] state = fake.LastStateBlock;
        Assert.Multiple(() =>
        {
            Assert.That(state[0] & 0x0C, Is.Zero, "the game's cleared trigger allow bits must pass through unchanged");
            Assert.That(state[10], Is.EqualTo(0x26), "the block bytes still ride the state block");
        });

        forwarder.Stop();
    }

    [Test]
    public void FeedPcm_WithoutNativeHapticsRemainSilent()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        for (int i = 0; i < 3; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }

        WaitUntil(() => fake.ReportCount >= 11, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "an audio block without a native haptics source must produce haptics silence");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameOutputState_GameRumbleIsSynthesizedIntoHapticsPcm()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameOutputState(new SetStateData
        {
            ValidFlag0 = ValidFlags.EnableRumbleEmulation,
            RumbleLeft = 0xFF,
            RumbleRight = 0x00
        });
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        byte[] frame = fake.LastHapticsFrame;
        Assert.Multiple(() =>
        {
            Assert.That(frame.Distinct().Count(), Is.GreaterThan(8), "the 60 Hz rumble sine must make the haptics vary across the frame");
            Assert.That(frame.Max(b => (sbyte)b), Is.GreaterThan(20), "the rumble must drive the voice coils");
        });

        forwarder.Stop();
    }

    [Test]
    public void Flush_DiscardsBufferedAudioAndClosesTheLane()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        for (int i = 0; i < 5; i++)
        {
            forwarder.FeedPcm(AudioBlock);
        }

        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));
        int beforeFlush = fake.ReportCount;
        Assert.That(beforeFlush, Is.GreaterThanOrEqualTo(12));

        forwarder.Flush();
        Thread.Sleep(100);

        Assert.That(fake.ReportCount - beforeFlush, Is.LessThanOrEqualTo(2),
            "the ring must be empty after Flush: the pump may only finish in-flight reports, not keep streaming buffered audio");

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ResetCount >= 2, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(fake.ResetCount, Is.GreaterThanOrEqualTo(2), "the next block must reopen the lane");
            Assert.That(fake.PrimeCount, Is.GreaterThanOrEqualTo(2));
        });

        forwarder.Stop();
    }

    [Test]
    public void Flush_ForcesRePrimeEvenWhenAudioResumesWithinTheIdleWindow()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.PrimeCount == 1, TimeSpan.FromSeconds(3));
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 10, TimeSpan.FromSeconds(3));

        forwarder.Flush();
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.PrimeCount >= 2, TimeSpan.FromSeconds(3));

        Assert.That(fake.ResetCount, Is.GreaterThanOrEqualTo(2),
            "resuming within the idle window must still re-prime after Flush (unlike a plain short gap)");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameOutputState_MotorBytesWithoutRumbleSelectorStaySilent()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameOutputState(new SetStateData
        {
            // Partial native reports retain motor bytes, but no selector means the
            // game did not request a motor update and those bytes are not a rumble cue.
            RumbleLeft = 0xFF,
            RumbleRight = 0xFF
        });
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 9, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "retained motor bytes without a valid rumble selector must not synthesize haptics");

        forwarder.Stop();
    }

    [Test]
    public void Flush_ClearsNativeHapticsBeforeTheNextGeneration()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        short[] nativeHaptics = MakeNativeHapticsBlock(8192, 8192);
        forwarder.FeedPcm(MakeAudioBlock(0f), nativeHaptics);
        WaitUntil(() => fake.ReportCount >= 9, TimeSpan.FromSeconds(3));
        Assert.That(fake.LastHapticsFrame.Any(b => b != 0), Is.True);

        forwarder.Flush();
        for (int i = 0; i < 4; i++)
        {
            forwarder.FeedPcm(MakeAudioBlock(0.5f));
        }

        WaitUntil(() => fake.PrimeCount >= 2 && fake.ReportCount >= 18, TimeSpan.FromSeconds(3));
        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "resetting the stream must not replay a stale native haptics block");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_ReplacesAudioDerivedHaptics()
    {
        byte[] gameHaptics = new byte[64];
        Array.Fill(gameHaptics, (byte)0x10);

        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        for (int i = 0; i < 8; i++)
        {
            forwarder.FeedPcm(MakeAudioBlock(0f));
        }

        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        // Deliver the payload only after the stream is primed: the ~85 ms prime and
        // preroll would otherwise age the payload past the 100 ms freshness window
        // under load. In the field the callback fires continuously, so a post-prime
        // delivery is the faithful shape.
        forwarder.UpdateGameHaptics(MakeCombinedReport(gameHaptics));
        WaitUntil(() => fake.LastHapticsFrame is { Length: 64 } && fake.LastHapticsFrame.All(b => b == 0x10), TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0x10), Is.True,
            "the game's fresh haptics payload must be forwarded, including its non-zero frame");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_NonZeroThenZeroTransitionsToNativeSilence()
    {
        byte[] nonZero = new byte[64];
        Array.Fill(nonZero, (byte)0x10);
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        for (int i = 0; i < 8; i++)
        {
            forwarder.FeedPcm(MakeAudioBlock(0f));
        }

        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));
        forwarder.UpdateGameHaptics(MakeCombinedReport(nonZero));
        forwarder.FeedPcm(MakeAudioBlock(0f));
        WaitUntil(() => fake.LastHapticsFrame.All(b => b == 0x10), TimeSpan.FromSeconds(3));

        forwarder.UpdateGameHaptics(MakeCombinedReport(new byte[64]));
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.LastHapticsFrame.Length == 64 && fake.LastHapticsFrame.All(b => b == 0), TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "a fresh native zero payload must transition directly to silence");
        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_SilentPayloadIsAuthoritativeSilence()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameHaptics(MakeCombinedReport(new byte[64]));
        forwarder.FeedPcm(AudioBlock);
        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "a valid all-zero game payload must suppress speaker-derived haptics");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_StalePayloadDoesNotReviveSpeakerAudio()
    {
        byte[] gameHaptics = new byte[64];
        Array.Fill(gameHaptics, (byte)0x10);

        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameHaptics(MakeCombinedReport(gameHaptics));
        Thread.Sleep(150);

        forwarder.FeedPcm(MakeAudioBlock(0f));
        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "a payload older than the freshness window must not be used (silence derives to zeros)");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_InvalidReportIsIgnored()
    {
        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        forwarder.UpdateGameHaptics(MakeCombinedReport(new byte[64], 0x31));
        forwarder.FeedPcm(MakeAudioBlock(0f));
        WaitUntil(() => fake.ReportCount >= 12, TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0), Is.True,
            "a report without the 0x36 id must be rejected and the derived haptics kept");

        forwarder.Stop();
    }

    [Test]
    public void UpdateGameHaptics_AcceptsCapturedViiperReportShape()
    {
        // Regression for the real libVIIPER wire format captured in the field logs:
        // 36 00 91 07 FE 10 10 10 10 10 00 90 3F FD F7 00 ... with haptics at offset 78.
        byte[] gameHaptics = new byte[64];
        Array.Fill(gameHaptics, (byte)0x10);

        FakeAudioOutputs fake = new FakeAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(fake, null);
        forwarder.Start();

        DSOutputState output = MakeCombinedReport(gameHaptics);
        output.BluetoothCombinedOutputReport[1] = 0x00;
        output.BluetoothCombinedOutputReport[5] = 0x10;
        output.BluetoothCombinedOutputReport[6] = 0x10;
        output.BluetoothCombinedOutputReport[7] = 0x10;
        output.BluetoothCombinedOutputReport[8] = 0x10;
        output.BluetoothCombinedOutputReport[9] = 0x10;
        output.BluetoothCombinedOutputReport[13] = 0xFD;
        output.BluetoothCombinedOutputReport[14] = 0xF7;
        for (int i = 0; i < 8; i++)
        {
            forwarder.FeedPcm(MakeAudioBlock(0f));
        }

        WaitUntil(() => fake.ReportCount >= 8 && fake.PrimeCount == 1, TimeSpan.FromSeconds(3));

        // Deliver the payload only after the stream is primed: the ~85 ms prime and
        // preroll would otherwise age the payload past the 100 ms freshness window
        // under load.
        forwarder.UpdateGameHaptics(output);
        WaitUntil(() => fake.LastHapticsFrame is { Length: 64 } && fake.LastHapticsFrame.All(b => b == 0x10), TimeSpan.FromSeconds(3));

        Assert.That(fake.LastHapticsFrame.All(b => b == 0x10), Is.True,
            "the captured VIIPER combined-report shape (session block, 0x90 0x3F state block, haptics at 78) must be accepted");

        forwarder.Stop();
    }
}
