using System.Collections.Concurrent;
using System.Diagnostics;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Triggers;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Hid;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.Emulation;

public class VirtualDualSenseControllerOutputTests
{
    /// <summary>
    /// Records standalone writes while exposing the transport discriminator used by
    /// the realtime callback router.
    /// </summary>
    private sealed class RecordingAudioOutputs : IDualSenseAudioOutputs
    {
        public ConnectionType ConnectionType { get; init; }
        public bool MicrophoneMuted { get; set; }
        public int OutputWriteDelayMilliseconds { get; init; }

        public void SetBluetoothMicrophoneEnabled(bool enabled)
        {
        }

        public List<byte[]> StandaloneStates { get; } = [];

        public void SetVibration(byte left, byte right)
        {
        }

        public void SendOutputState(SetStateData payload)
        {
            if (OutputWriteDelayMilliseconds > 0)
            {
                Thread.Sleep(OutputWriteDelayMilliseconds);
            }

            byte[] copy = new byte[SetStateData.PayloadSize];
            payload.CopyTo(copy, 0);
            StandaloneStates.Add(copy);
        }

        public void ResetBluetoothAudioStream()
        {
        }

        public void SendBluetoothAudioPrime(SetStateData state)
        {
        }

        public void SendBluetoothAudioAndHaptics(SetStateData state, ReadOnlySpan<byte> opusFrame,
            ReadOnlySpan<byte> hapticsPcm, BluetoothAudioRoute route)
        {
        }

        public void SendBluetoothAudio(ReadOnlySpan<byte> opusFrame, BluetoothAudioRoute route)
        {
        }

        public void SetAudioOutput(AudioControl outputControl, byte speakerVolume, byte headphoneVolume)
        {
        }
    }

    /// <summary>
    /// Holds a physical output write so callback ordering against media-state
    /// publication can be observed without a native device or HID handle.
    /// </summary>
    private sealed class BlockingOutputs : IDualSenseOutputs, IDisposable
    {
        public ManualResetEventSlim WriteEntered { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim ReleaseWrite { get; } = new ManualResetEventSlim();

        public void SetVibration(byte left, byte right)
        {
        }

        public void SendOutputState(SetStateData payload)
        {
            WriteEntered.Set();
            ReleaseWrite.Wait(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            WriteEntered.Dispose();
            ReleaseWrite.Dispose();
        }
    }

    /// <summary>
    /// Models the physical Bluetooth output lock shared by standalone reports and
    /// combined audio/haptics reports. The ninth combined write is the first audio
    /// block after the eight-packet stream prime and can be held to exercise callback
    /// contention with an active audio writer.
    /// </summary>
    private sealed class BlockingBluetoothOutputs : IDualSenseAudioOutputs, IDisposable
    {
        private readonly object _writeSync = new object();
        private int _combinedReportCount;
        private int _standaloneStateWriteCount;

        public ConnectionType ConnectionType => ConnectionType.Bluetooth;
        public bool MicrophoneMuted => false;
        public ManualResetEventSlim AudioWriteEntered { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim ReleaseAudioWrite { get; } = new ManualResetEventSlim();
        public ConcurrentQueue<SetStateData> CombinedStates { get; } = new ConcurrentQueue<SetStateData>();
        public int StandaloneStateWriteCount => Volatile.Read(ref _standaloneStateWriteCount);

        public void SetVibration(byte left, byte right)
        {
        }

        public void SendOutputState(SetStateData payload)
        {
            lock (_writeSync)
            {
                Interlocked.Increment(ref _standaloneStateWriteCount);
            }
        }

        public void SetBluetoothMicrophoneEnabled(bool enabled)
        {
        }

        public void ResetBluetoothAudioStream()
        {
        }

        public void SendBluetoothAudioPrime(SetStateData state)
        {
        }

        public void SendBluetoothAudioAndHaptics(SetStateData state, ReadOnlySpan<byte> opusFrame,
            ReadOnlySpan<byte> hapticsPcm, BluetoothAudioRoute route)
        {
            lock (_writeSync)
            {
                int report = Interlocked.Increment(ref _combinedReportCount);
                if (report == 9)
                {
                    AudioWriteEntered.Set();
                    ReleaseAudioWrite.Wait(TimeSpan.FromSeconds(5));
                }

                CombinedStates.Enqueue(state);
            }
        }

        public void SendBluetoothAudio(ReadOnlySpan<byte> opusFrame, BluetoothAudioRoute route)
        {
        }

        public void SetAudioOutput(AudioControl outputControl, byte speakerVolume, byte headphoneVolume)
        {
        }

        public void Dispose()
        {
            AudioWriteEntered.Dispose();
            ReleaseAudioWrite.Dispose();
        }
    }

    /// <summary>
    /// Builds a 48-byte USB output report (report ID 0x02 + 47-byte payload) from the
    /// given payload bytes.
    /// </summary>
    private static byte[] RawReport(params byte[] payload)
    {
        Assert.That(payload.Length, Is.EqualTo(47));
        byte[] raw = new byte[48];
        raw[0] = 0x02;
        Buffer.BlockCopy(payload, 0, raw, 1, 47);
        return raw;
    }

    private static DSOutputState Output(byte[] rawReport, byte rumbleSmall = 0xEE, byte rumbleLarge = 0xDD)
    {
        return new DSOutputState
        {
            RumbleSmall = rumbleSmall,
            RumbleLarge = rumbleLarge,
            RawOutputReport = rawReport,
            BluetoothCombinedOutputReport = new byte[398]
        };
    }

    private static byte[] TriggerBytes(TriggerEffectBlock block)
    {
        byte[] raw = new byte[11];
        block.CopyTo(raw, 0);
        return raw;
    }

    /// <summary>
    /// A rich game write: v1 rumble at distinct magnitudes, volumes, audio control,
    /// mute LED, both trigger blocks allowed, timestamp, motor power reduction,
    /// brightness/fade flags and lightbar state.
    /// </summary>
    private static byte[] RichGamePayload()
    {
        byte[] p = new byte[47];
        p[0] = 0x3F; // flag0: v1 rumble | haptics select | R2 FFB | L2 FFB | headphone vol | speaker vol
        p[2] = 0xAA; // right motor
        p[3] = 0xBB; // left motor
        p[4] = 0x11; // headphone volume
        p[5] = 0x22; // speaker volume
        p[6] = 0x33; // mic volume
        p[7] = 0x01; // audio control
        p[8] = 0x01; // mute LED on
        p[9] = 0x44; // power save control
        p[10] = 0x25; // R2 block: effect mode
        p[11] = 0x10;
        p[12] = 0x20;
        p[13] = 0x30;
        p[21] = 0x21; // L2 block: effect mode
        p[22] = 0x40;
        p[23] = 0x50;
        p[24] = 0x60;
        p[32] = 0xEF; // host timestamp 0xDEADBEEF
        p[33] = 0xBE;
        p[34] = 0xAD;
        p[35] = 0xDE;
        p[36] = 0x55; // motor power reduction
        p[37] = 0x77; // audio control 2
        p[38] = 0x03; // flag2: brightness change | color fade anim
        p[39] = 0x01; // haptic low-pass filter enable
        p[41] = 0x01; // light fade animation
        p[42] = 0x02; // lightbar brightness low
        p[43] = 0x1F; // player LEDs
        p[44] = 0xC0; // red
        p[45] = 0xFF; // green
        p[46] = 0xEE; // blue
        return p;
    }

    [Test]
    public void V1GameOnV2Pad_TranslatesSelectorAndPassesEverythingElseThrough()
    {
        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(Output(RawReport(RichGamePayload())), true);

        byte[] expected = RichGamePayload();
        Assert.Multiple(() =>
        {
            // v1 selector translated to the improved-rumble encoding.
            Assert.That((byte)payload.ValidFlag0 & 0x01, Is.Zero, "the v1 compatibility bit must be cleared for a v2 pad");
            Assert.That((byte)payload.ValidFlag0 & 0x02, Is.EqualTo(0x02), "haptics select must stay set");
            Assert.That((byte)payload.ValidFlag2 & 0x04, Is.EqualTo(0x04), "the v2 selector must be set for a v2 pad");

            // Everything else rides through exactly as the game wrote it.
            Assert.That((byte)payload.ValidFlag0 & 0x0C, Is.EqualTo(0x0C), "the game's trigger allow bits must be preserved");
            Assert.That((byte)payload.ValidFlag0 & 0x30, Is.EqualTo(0x30), "the game's volume bits must be preserved");
            Assert.That(payload.RumbleRight, Is.EqualTo(0xEE), "the motor bytes must carry libVIIPER's retained magnitudes");
            Assert.That(payload.RumbleLeft, Is.EqualTo(0xDD));
            Assert.That(payload.HeadphoneVolume, Is.EqualTo(0x11));
            Assert.That(payload.SpeakerVolume, Is.EqualTo(0x22));
            Assert.That(payload.MicVolume, Is.EqualTo(0x33));
            Assert.That((byte)payload.AudioControl, Is.EqualTo(0x01));
            Assert.That(payload.MuteLedMode, Is.EqualTo(0x01));
            Assert.That(payload.PowerSaveControl, Is.EqualTo(0x44));
            Assert.That(payload.HostTimestamp, Is.EqualTo(0xDEADBEEFu));
            Assert.That(payload.MotorPowerReduction, Is.EqualTo(0x55));
            Assert.That(payload.AudioControl2, Is.EqualTo(0x77));
            Assert.That((byte)payload.ValidFlag2 & 0x03, Is.EqualTo(0x03), "the game's brightness/fade bits must be preserved");
            Assert.That(payload.HapticLowPassFilter, Is.EqualTo(0x01));
            Assert.That(payload.LightFadeAnimation, Is.EqualTo(0x01));
            Assert.That(payload.LightBrightness, Is.EqualTo(0x02));
            Assert.That((PlayerLedMask)payload.PlayerLeds, Is.EqualTo((PlayerLedMask)0x1F));
            Assert.That(payload.LedRed, Is.EqualTo(0xC0));
            Assert.That(payload.LedGreen, Is.EqualTo(0xFF));
            Assert.That(payload.LedBlue, Is.EqualTo(0xEE));
            Assert.That((byte)payload.R2TriggerEffect.Mode, Is.EqualTo(0x25), "the R2 effect block must ride the report");
            Assert.That((byte)payload.L2TriggerEffect.Mode, Is.EqualTo(0x21), "the L2 effect block must ride the report");
        });
    }

    [Test]
    public void V2GameOnV1Pad_TranslatesSelector()
    {
        byte[] p = new byte[47];
        p[0] = 0x02 | 0x0C; // flag0: haptics select + trigger FFB only
        p[38] = 0x04; // flag2: v2 selector

        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(Output(RawReport(p)), false);

        Assert.Multiple(() =>
        {
            Assert.That((byte)payload.ValidFlag0 & 0x01, Is.EqualTo(0x01), "the v1 compatibility bit must be set for a v1 pad");
            Assert.That((byte)payload.ValidFlag0 & 0x02, Is.EqualTo(0x02));
            Assert.That((byte)payload.ValidFlag2 & 0x04, Is.Zero, "the v2 selector must be cleared for a v1 pad");
            Assert.That((byte)payload.ValidFlag0 & 0x0C, Is.EqualTo(0x0C), "the trigger allow bits must be preserved");
        });
    }

    [Test]
    public void ReportWithoutRumble_ClearsSelectorSoThePadRetainsMotors()
    {
        byte[] p = new byte[47];
        p[0] = 0x0C; // trigger-only write: no rumble selector
        p[2] = 0x7F; // stale motor bytes that the pad must ignore
        p[3] = 0x7F;

        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(Output(RawReport(p)), true);

        Assert.Multiple(() =>
        {
            Assert.That((byte)payload.ValidFlag0 & 0x03, Is.Zero, "no selector bit may be set when the game did not touch rumble");
            Assert.That((byte)payload.ValidFlag2 & 0x04, Is.Zero);
            Assert.That(payload.RumbleRight, Is.EqualTo(0xEE), "subscribers must still see libVIIPER's retained magnitudes");
            Assert.That(payload.RumbleLeft, Is.EqualTo(0xDD));
        });
    }

    [Test]
    public void TriggerBitsFollowTheGameExactly()
    {
        byte[] p = new byte[47];
        p[0] = 0x04; // only R2 allowed; L2 bit deliberately clear while its block bytes are present
        p[21] = 0x26;

        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(Output(RawReport(p)), false);

        Assert.Multiple(() =>
        {
            Assert.That((byte)payload.ValidFlag0 & 0x04, Is.EqualTo(0x04), "the R2 allow bit must pass through");
            Assert.That((byte)payload.ValidFlag0 & 0x08, Is.Zero, "the L2 allow bit must stay clear so the pad retains its effect");
            Assert.That((byte)payload.L2TriggerEffect.Mode, Is.EqualTo(0x26), "the block bytes ride along regardless of gating");
        });
    }

    [Test]
    public void MalformedRawReport_FallsBackToDecodedFields()
    {
        DSOutputState output = new DSOutputState
        {
            RumbleSmall = 0x11,
            RumbleLarge = 0x22,
            LedRed = 1,
            LedGreen = 2,
            LedBlue = 3,
            PlayerLeds = 0x07,
            MicLed = 0x02,
            LightbarSetup = 0x02,
            LightbarBrightness = 0x01,
            RawOutputReport = []
        };

        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(output, true);

        Assert.Multiple(() =>
        {
            Assert.That(payload.RumbleRight, Is.EqualTo(0x11));
            Assert.That(payload.RumbleLeft, Is.EqualTo(0x22));
            Assert.That(payload.LedRed, Is.EqualTo(1));
            Assert.That(payload.LedGreen, Is.EqualTo(2));
            Assert.That(payload.LedBlue, Is.EqualTo(3));
            Assert.That((byte)payload.PlayerLeds, Is.EqualTo(0x07));
            Assert.That(payload.MuteLedMode, Is.EqualTo(0x02));
            Assert.That(payload.LightFadeAnimation, Is.EqualTo(0x02));
            Assert.That(payload.LightBrightness, Is.EqualTo(0x01));
            Assert.That((byte)payload.ValidFlag2 & 0x03, Is.EqualTo(0x03));
            Assert.That((byte)payload.ValidFlag0 & 0x0C, Is.EqualTo(0x0C), "the fallback keeps the trigger blocks alive");
        });
    }

    [Test]
    public void RealtimeOutput_BluetoothPublishesTriggerStateToCombinedLaneWithoutStandaloneWrite()
    {
        byte[] p = new byte[47];
        p[0] = 0x0C; // both trigger allow bits
        p[10] = 0x26; // R2 mode
        p[21] = 0x25; // L2 mode

        RecordingAudioOutputs outputs = new RecordingAudioOutputs
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        SetStateData? received = null;

        (SetStateData payload, bool standaloneForwarded) = VirtualDualSenseController.ForwardRealtimeOutput(
            Output(RawReport(p)), vibrationV2: false, outputs, state => received = state);

        Assert.Multiple(() =>
        {
            Assert.That(outputs.StandaloneStates, Is.Empty,
                "Bluetooth audio owns standalone reports while the realtime callback is active");
            Assert.That(standaloneForwarded, Is.False);
            Assert.That(received.HasValue, Is.True,
                "the latest native output state must reach the combined Bluetooth carrier");
            Assert.That((byte)payload.R2TriggerEffect.Mode, Is.EqualTo(0x26));
            Assert.That((byte)received!.Value.R2TriggerEffect.Mode, Is.EqualTo(0x26));
            Assert.That((byte)received.Value.L2TriggerEffect.Mode, Is.EqualTo(0x25));
            Assert.That((byte)received.Value.ValidFlag0 & 0x0C, Is.EqualTo(0x0C));
        });
    }

    [Test]
    public void RealtimeOutput_UsbPublishesStandaloneTriggerState()
    {
        byte[] p = new byte[47];
        p[0] = 0x04; // R2 trigger allow bit
        p[10] = 0x26;

        RecordingAudioOutputs outputs = new RecordingAudioOutputs
        {
            ConnectionType = ConnectionType.Usb
        };

        VirtualDualSenseController.ForwardRealtimeOutput(
            Output(RawReport(p)), vibrationV2: false, outputs, stateReceived: null);

        Assert.That(outputs.StandaloneStates, Has.Count.EqualTo(1));
        Assert.That(outputs.StandaloneStates[0][10], Is.EqualTo(0x26));
        Assert.That(outputs.StandaloneStates[0][0] & 0x04, Is.EqualTo(0x04));
    }

    [Test]
    public async Task ExistingOutputHooks_UseManualCaptureStartedAfterConstruction()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-audio-timing-attempt41-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        NativeAudioTimingDiagnosticsManager manager = NativeAudioTimingDiagnosticsManager.CreateForTesting(
            root,
            Stopwatch.GetTimestamp,
            () => "attempt41-test",
            testClock: false,
            startPublisher: false);
        NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
        NativeAudioTimingDiagnosticsControl control = new(manager);
        RecordingAudioOutputs outputs = new() { ConnectionType = ConnectionType.Usb };
        try
        {
            VirtualDualSenseController.ForwardOutputState(
                Output(RawReport(new byte[47])),
                vibrationV2: false,
                outputs,
                stateReceived: null,
                router);
            Assert.That(control.GetStatus().State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Off));

            await control.StartAsync(3);
            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;
            VirtualDualSenseController.ForwardOutputState(
                Output(RawReport(new byte[47])),
                vibrationV2: false,
                outputs,
                stateReceived: null,
                router);
            VirtualDualSenseController.ForwardRealtimeOutput(
                Output(RawReport(new byte[47])),
                vibrationV2: false,
                outputs,
                stateReceived: null,
                router);

            Assert.Multiple(() =>
            {
                Assert.That(outputs.StandaloneStates, Has.Count.EqualTo(3));
                Assert.That(session.GetMetricSnapshot(AudioTimingMetric.ControlOutputWrite).Samples, Is.EqualTo(2));
            });
            await control.StopAsync();
            await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            NativeAudioTimingDiagnostics? session = manager.GetSessionForTesting();
            if (session is not null && session.IsSessionActive)
            {
                await control.StopAsync();
            }

            if (session is not null)
            {
                await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void OutputStateCallback_PreservesFixed17WriteBeforeStatePublicationOrder()
    {
        byte[] p = new byte[47];
        p[0] = 0x04; // R2 trigger allow bit
        p[10] = 0x26;
        using BlockingOutputs outputs = new BlockingOutputs();
        using ManualResetEventSlim statePublished = new ManualResetEventSlim();
        SetStateData? forwardedState = null;
        Task callback = Task.Run(() => VirtualDualSenseController.ForwardOutputState(
            Output(RawReport(p)), vibrationV2: false, outputs, state =>
            {
                forwardedState = state;
                statePublished.Set();
            }));

        bool publishedWhileWriteBlocked = false;
        try
        {
            Assert.That(outputs.WriteEntered.Wait(TimeSpan.FromSeconds(1)), Is.True,
                "the callback must reach the deliberately slow physical writer");
            publishedWhileWriteBlocked = statePublished.Wait(TimeSpan.FromMilliseconds(150));
        }
        finally
        {
            outputs.ReleaseWrite.Set();
        }

        Assert.That(callback.Wait(TimeSpan.FromSeconds(1)), Is.True,
            "the fake physical write must be released before the callback exits");
        Assert.That(publishedWhileWriteBlocked, Is.False,
            "fixed17 publishes combined-lane state after its synchronous physical output write");
        Assert.That(statePublished.IsSet, Is.True,
            "the callback must publish the unchanged payload after the physical write completes");
        Assert.That((byte)forwardedState!.Value.R2TriggerEffect.Mode, Is.EqualTo(0x26));
    }

    [Test]
    public void ActiveBluetoothAudio_CoalescesLatestControlStateWithoutBlockingNativeCallback()
    {
        using BlockingBluetoothOutputs outputs = new BlockingBluetoothOutputs();
        using ViiperDualSenseAudioForwarder forwarder = new ViiperDualSenseAudioForwarder(outputs, null);
        Assert.That(forwarder.Start(), Is.True);
        float[] audioBlock = new float[DualSenseBtAudioPipeline.FramesPerBlock * DualSenseBtAudioPipeline.Channels];
        Array.Fill(audioBlock, 0.5f);
        forwarder.FeedPcm(audioBlock);

        Task? callback = null;
        bool audioWriteBlocked = false;
        bool callbackCompletedWhileAudioBlocked = false;
        try
        {
            audioWriteBlocked = outputs.AudioWriteEntered.Wait(TimeSpan.FromSeconds(3));
            if (audioWriteBlocked)
            {
                // Leave a real next block pending so the latest state can ride the
                // existing combined Bluetooth report cadence after the blocked write.
                forwarder.FeedPcm(audioBlock);
                byte[] report = new byte[47];
                report[0] = 0x0C;
                report[10] = 0x26;
                callback = Task.Run(() => VirtualDualSenseController.ForwardOutputState(
                    Output(RawReport(report)),
                    vibrationV2: false,
                    outputs,
                    forwarder.UpdateGameOutputState,
                    coalesceState: forwarder.TryCoalesceGameOutputState));
                callbackCompletedWhileAudioBlocked = callback.Wait(TimeSpan.FromMilliseconds(150));
            }
        }
        finally
        {
            outputs.ReleaseAudioWrite.Set();
            callback?.Wait(TimeSpan.FromSeconds(2));
        }

        bool latestStateReachedCombinedLane = SpinWait.SpinUntil(
            () => outputs.CombinedStates.Any(state => (byte)state.R2TriggerEffect.Mode == 0x26),
            TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(audioWriteBlocked, Is.True, "the regression must hold the real Bluetooth pump writer");
            Assert.That(callbackCompletedWhileAudioBlocked, Is.True,
                "a native output callback must publish to the active combined lane without waiting for the physical writer");
            Assert.That(callback?.IsCompleted, Is.True, "the callback must finish after the held audio write is released");
            Assert.That(outputs.StandaloneStateWriteCount, Is.Zero,
                "an active combined Bluetooth lane must not receive a duplicate synchronous standalone report");
            Assert.That(latestStateReachedCombinedLane, Is.True,
                "the next combined audio report must carry the latest adaptive-trigger state");
        });
    }

    [Test]
    public void IdleBluetoothAudio_PreservesImmediateStandaloneOutputState()
    {
        RecordingAudioOutputs outputs = new()
        {
            ConnectionType = ConnectionType.Bluetooth
        };
        using ViiperDualSenseAudioForwarder forwarder = new(outputs, null);
        Assert.That(forwarder.Start(), Is.True);

        byte[] report = new byte[47];
        report[0] = 0x04;
        report[10] = 0x26;
        SetStateData? published = null;
        (SetStateData payload, bool standaloneForwarded) = VirtualDualSenseController.ForwardOutputState(
            Output(RawReport(report)),
            vibrationV2: false,
            outputs,
            state => published = state,
            coalesceState: forwarder.TryCoalesceGameOutputState);

        Assert.Multiple(() =>
        {
            Assert.That(standaloneForwarded, Is.True,
                "without a queued combined audio carrier the callback must keep the immediate fallback");
            Assert.That(outputs.StandaloneStates, Has.Count.EqualTo(1));
            Assert.That(published.HasValue, Is.True);
            Assert.That((byte)payload.R2TriggerEffect.Mode, Is.EqualTo(0x26));
            Assert.That((byte)published!.Value.R2TriggerEffect.Mode, Is.EqualTo(0x26));
        });
    }

    [Test]
    public void SlowControlOutputWrite_IsRecordedSeparatelyFromCallbackPublication()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        RecordingAudioOutputs outputs = new RecordingAudioOutputs
        {
            ConnectionType = ConnectionType.Usb,
            OutputWriteDelayMilliseconds = 15
        };
        bool statePublished = false;

        VirtualDualSenseController.ForwardOutputState(
            Output(RawReport(new byte[47])),
            vibrationV2: false,
            outputs,
            _ => statePublished = true,
            diagnostics);

        AudioTimingMetricSnapshot write = diagnostics.GetMetricSnapshot(AudioTimingMetric.ControlOutputWrite);
        Assert.Multiple(() =>
        {
            Assert.That(write.Samples, Is.EqualTo(1));
            Assert.That(write.MaxStopwatchTicks, Is.GreaterThanOrEqualTo(Stopwatch.Frequency / 100));
            Assert.That(statePublished, Is.True, "fixed17 publishes the combined-lane snapshot only after the physical write returns");
        });
    }

    [Test]
    public void MalformedRawReport_ReconstructsDecodedTriggerBlocks()
    {
        DSOutputState output = new DSOutputState
        {
            TriggerR2Mode = 0x25,
            TriggerR2StartResistance = 1,
            TriggerR2EffectForce = 2,
            TriggerR2RangeForce = 3,
            TriggerR2NearReleaseStrength = 4,
            TriggerR2NearMiddleStrength = 5,
            TriggerR2PressedStrength = 6,
            TriggerR2Frequency = 7,
            TriggerL2Mode = 0x26,
            TriggerL2StartResistance = 8,
            TriggerL2EffectForce = 9,
            TriggerL2RangeForce = 10,
            TriggerL2NearReleaseStrength = 11,
            TriggerL2NearMiddleStrength = 12,
            TriggerL2PressedStrength = 13,
            TriggerL2Frequency = 14,
            RawOutputReport = []
        };

        SetStateData payload = VirtualDualSenseController.BuildOutputPayload(output, vibrationV2: false);

        Assert.Multiple(() =>
        {
            Assert.That(TriggerBytes(payload.R2TriggerEffect), Is.EqualTo(new byte[]
            {
                0x25, 1, 2, 3, 4, 5, 6, 0, 0, 7, 0
            }));
            Assert.That(TriggerBytes(payload.L2TriggerEffect), Is.EqualTo(new byte[]
            {
                0x26, 8, 9, 10, 11, 12, 13, 0, 0, 14, 0
            }));
        });
    }
}
