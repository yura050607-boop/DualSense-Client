using System.Diagnostics;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Core.Utilities;
using DualSenseClient.Hid;
using DualSenseClient.Logging;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Controllers.DualSense.Audio;

/// <summary>
/// Forwards host audio to the physical DualSense while VIIPER emulation is active, so
/// the pad's own speaker plays the host's audio. The host loopback capture feeds one
/// 512-frame (10.667 ms) stereo block per <see cref="FeedPcm"/> call into a bounded
/// ring; a pump thread paced at the DualSense Bluetooth audio-clock cadence pops blocks
/// and fans them out to the active transport: Bluetooth HID reports (<c>0x35</c>/<c>0x36</c>)
/// or the USB UAC render endpoint via <see cref="DualSenseUsbAudioTarget"/>.
/// </summary>
/// <remarks>
/// <para>
/// The ring absorbs the capture-callback jitter and the difference between the host
/// loopback period and the 512/48000 s controller cadence. Underruns send silence so
/// the stream stays paced; when the ring fills beyond the target latency the pump
/// discards one extra block per tick until it settles, keeping end-to-end latency
/// bounded. Haptics are carried as a separate, paired source: the game's rear
/// haptics channels (or the realtime 3 kHz payload) are forwarded byte-for-byte in
/// kind, while an absent source is silence. Host speaker audio is never promoted to
/// haptics by this bridge.
/// </para>
/// <para>
/// The Bluetooth audio lane is only opened once audio is actually being fed, unless
/// the built-in microphone is explicitly enabled: a microphone session primes the
/// lane once so the pad keeps its duplex request active, then idles without sending
/// recurring empty speaker reports. The first block of a speaker session primes the
/// stream (reset, init-prime and silence preroll) and, after the lane has been idle
/// long enough to underrun, the next session re-primes. An idle forwarder therefore
/// never occupies the Bluetooth audio lane â€” which would make the controller ignore
/// the virtual controller's output-state reports (rumble, adaptive triggers, LEDs)
/// â€” and never feeds the USB endpoint.
/// </para>
/// <para>
/// While the lane is open the pad still ignores standalone output-state reports, so
/// the game's output state is embedded into the combined reports instead: the
/// <see cref="UpdateGameOutputState"/> snapshot rides the <c>0x36</c> state block
/// and the <c>0x32</c> init-prime (triggers, lightbar and player LEDs apply
/// normally, with the audio bytes overridden), and the game's classic rumble is
/// synthesized into the haptics PCM as 60/180 Hz sine waves.
/// </para>
/// <para>
/// When libVIIPER's realtime-haptics callback delivers a fresh payload (the game's
/// own voice-coil data extracted from the 398-byte combined report via
/// <see cref="UpdateGameHaptics"/>), it replaces the paired capture haptics. A fresh
/// all-zero payload is intentional silence, not an invitation to derive vibration
/// from speaker audio; stale or absent native haptics likewise remain silent.
/// </para>
/// </remarks>
public sealed class ViiperDualSenseAudioForwarder : IDisposable
{
    /// <summary>
    /// Interleaved float samples in one 512-frame stereo block.
    /// </summary>
    private const int BlockSamples = DualSenseBtAudioPipeline.FramesPerBlock * DualSenseBtAudioPipeline.Channels;

    /// <summary>
    /// Interleaved samples in one 480-frame Opus block.
    /// </summary>
    private const int OpusBlockSamples = DualSenseBtAudioPipeline.OpusFrameSamples * DualSenseBtAudioPipeline.Channels;

    /// <summary>
    /// Interleaved samples in one 512-frame quad block fed to the USB render target.
    /// </summary>
    private const int QuadBlockSamples = DualSenseBtAudioPipeline.FramesPerBlock * 4;

    /// <summary>
    /// Silence audio/haptics reports sent after priming the Bluetooth stream, so the
    /// controller's speaker path and Opus decoder warm up before real audio. 8 packets
    /// â‰ˆ 85 ms at the 10.667 ms cadence.
    /// </summary>
    private const int BtPrerollPackets = 8;

    /// <summary>
    /// Ring capacity in blocks (32 Ã— 10.667 ms â‰ˆ 341 ms of buffered audio).
    /// </summary>
    private const int RingBlocks = 32;

    /// <summary>
    /// Steady-state latency the pump targets (3 blocks â‰ˆ 32 ms). Above it, the pump
    /// discards one extra block per tick until the ring drains back down.
    /// </summary>
    private const int TargetBlocks = 3;

    /// <summary>
    /// Offset of the haptics payload within the 398-byte combined Bluetooth report
    /// delivered by libVIIPER's realtime-haptics callback. The report is the vDS-style
    /// <c>0x36</c> transport used by the virtual-device feedback stream: <c>[0]</c> is
    /// the report id, <c>[2..10]</c> the session block (<c>0x91 0x07 0xFE</c> + codec
    /// flags), <c>[11..73]</c> the <c>0x90 0x3F</c> state block carrying the native
    /// 47-byte output state at <c>[13..59]</c>, <c>[74..77]</c> the audio sub-packet
    /// header, and then <see cref="DualSenseBtAudioPipeline.HapticsBytes"/> of
    /// interleaved s8 stereo PCM at 3 kHz. This matches DS4Windows' consumption of the
    /// same feedback (<c>DualSenseBluetoothAudioPacer.RealtimeHapticsDataOffset = 78</c>).
    /// </summary>
    private const int BluetoothCombinedHapticsPcmOffset = 78;

    /// <summary>
    /// Output-state bit that mutes the physical DualSense microphone. Only this bit
    /// is owned by the microphone bridge; the remaining power-save bits belong to
    /// the virtual controller's game state.
    /// </summary>
    private const byte MicrophoneMutePowerSaveBit = 0x10;

    /// <summary>
    /// How long an update from the realtime-haptics callback stays fresh before the
    /// paired capture haptics (or silence when no paired source exists) is used. The
    /// callback fires every rear-haptics interval (~10.667 ms), so this window (~9
    /// intervals) tolerates scheduling gaps without reviving speaker audio as haptics.
    /// </summary>
    private const int GameHapticsFreshWindowMs = 100;

    /// <summary>
    /// Target interval between pump ticks: 512/48000 s = 10.667 ms.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(DualSenseBtAudioPipeline.FramesPerBlock / (double)DualSenseBtAudioPipeline.SampleRate);

    /// <summary>
    /// <see cref="GameHapticsFreshWindowMs"/> in stopwatch ticks.
    /// </summary>
    private static readonly long GameHapticsFreshWindowTicks = (long)(Stopwatch.Frequency * GameHapticsFreshWindowMs / 1000.0);

    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("ViiperDualSenseAudioForwarder");

    /// <summary>
    /// The physical controller's Bluetooth audio lane, or <c>null</c> when the pad is
    /// not Bluetooth-connected.
    /// </summary>
    private readonly IDualSenseAudioOutputs? _outputs;

    /// <summary>
    /// The physical controller's USB render target, or <c>null</c> when the pad is not
    /// USB-connected or no render endpoint exists.
    /// </summary>
    private readonly DualSenseUsbAudioTarget? _usbTarget;

    /// <summary>
    /// Process-local opt-in timing session; disabled by default.
    /// </summary>
    private readonly NativeAudioTimingDiagnostics _timingDiagnostics;

    /// <summary>
    /// Whether the caller explicitly opted into forwarding the physical Bluetooth
    /// microphone to the virtual capture endpoint. The existing audio pump owns the
    /// duplex report cadence; no second Bluetooth writer is created.
    /// </summary>
    private volatile bool _microphoneEnabled;

    /// <summary>
    /// Whether the current Bluetooth session has sent at least one duplex report.
    /// </summary>
    private bool _microphoneSessionActive;

    /// <summary>
    /// Whether this capture generation has observed any non-zero speaker or native
    /// haptics sample. Host capture callbacks can legally deliver an initial block of
    /// digital silence while a microphone-only session is active; that startup silence
    /// must not turn the Bluetooth audio lane into a recurring speaker clock. Once a
    /// real source value has appeared, later zero blocks are retained as truthful
    /// silent intervals and as native-haptics clear-to-neutral blocks.
    /// </summary>
    private bool _captureActivitySeen;

    /// <summary>
    /// Shared Bluetooth audio conversion pipeline (resample, Opus encode, haptics).
    /// </summary>
    private readonly DualSenseBtAudioPipeline _pipeline = new DualSenseBtAudioPipeline();

    /// <summary>
    /// Ring holding the captured stereo blocks between the capture callback and the pump.
    /// </summary>
    private readonly PcmBlockRing _audioRing = new PcmBlockRing(RingBlocks);

    /// <summary>
    /// Serializes start/stop and the pump's per-tick work with feed/stop calls.
    /// </summary>
    private readonly Lock _sync = new Lock();

    /// <summary>
    /// Serializes physical output calls and protects the per-block output scratch
    /// buffers after the pump releases <see cref="_sync"/>. A HID write can block on
    /// the transport, so it must not hold the lock used by capture and VIIPER
    /// callbacks.
    /// </summary>
    private readonly Lock _outputIoSync = new Lock();

    /// <summary>
    /// Cancels the running pump loop; <c>null</c> while the forwarder is stopped.
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The background task running the pump loop.
    /// </summary>
    private Task? _loopTask;

    /// <summary>
    /// Scratch buffer for one block popped from the ring.
    /// </summary>
    private readonly float[] _floatBlock = new float[BlockSamples];

    /// <summary>
    /// Scratch buffer holding the block converted to signed 16-bit PCM.
    /// </summary>
    private readonly short[] _pcmBlock = new short[BlockSamples];

    /// <summary>
    /// Paired rear haptics channels from the virtual DualSense's native four-channel
    /// audio endpoint. This is kept at 48 kHz for the USB route and decimated for BT.
    /// </summary>
    private readonly short[] _nativeHapticsBlock = new short[BlockSamples];

    /// <summary>
    /// Scratch buffer for one 480-frame Opus block.
    /// </summary>
    private readonly short[] _opusBlock = new short[OpusBlockSamples];

    /// <summary>
    /// Scratch buffer for the fixed 200-byte Opus frame sent to the controller.
    /// </summary>
    private readonly byte[] _opusFrame = new byte[DualSenseBtAudioPipeline.OpusBytes];

    /// <summary>
    /// Scratch buffer for the 64-byte Bluetooth haptics payload.
    /// </summary>
    private readonly byte[] _hapticsPcm = new byte[DualSenseBtAudioPipeline.HapticsBytes];

    /// <summary>
    /// Latest haptics payload delivered by libVIIPER's realtime-haptics callback (the
    /// game's own voice-coil data), valid while <see cref="_lastGameHapticsTimestamp"/>
    /// is within <see cref="GameHapticsFreshWindowTicks"/>. Guarded by <see cref="_sync"/>.
    /// </summary>
    private readonly byte[] _gameHapticsPcm = new byte[DualSenseBtAudioPipeline.HapticsBytes];

    /// <summary>
    /// Stopwatch timestamp of the last accepted realtime-haptics update, or 0 when none
    /// was accepted yet. Guarded by <see cref="_sync"/>.
    /// </summary>
    private long _lastGameHapticsTimestamp;

    /// <summary>
    /// One-shot diagnostics for the realtime-haptics combined report validation.
    /// </summary>
    private int _gameHapticsAcceptedLogged;

    private int _gameHapticsRejectedLogged;

    /// <summary>
    /// Scratch buffer for the interleaved quad (S16) block fed to the USB render target.
    /// </summary>
    private readonly short[] _usbQuadBlock = new short[QuadBlockSamples];

    /// <summary>
    /// Scratch buffer used to discard catch-up blocks.
    /// </summary>
    private readonly float[] _discardBlock = new float[BlockSamples];

    /// <summary>
    /// Idle period after which the Bluetooth audio lane is considered dead (the pad's
    /// shallow receive buffer has underrun), so the next audio session re-primes the
    /// stream. Long enough to ignore capture-jitter gaps, short enough to release the
    /// lane promptly when audio stops.
    /// </summary>
    private static readonly long RePrimeIdleTicks =
        (long)(Stopwatch.Frequency * TimeSpan.FromMilliseconds(150).TotalSeconds);

    /// <summary>
    /// Speaker volume (0-255) applied to the controller's speaker/headset hardware.
    /// </summary>
    private volatile byte _speakerVolume = 0x50;

    /// <summary>
    /// Whether native game haptics are forwarded to the physical controller.
    /// </summary>
    private volatile bool _hapticsEnabled = true;

    /// <summary>
    /// Haptic vibration strength multiplier (1.0 = full).
    /// </summary>
    private volatile float _hapticStrength = 1f;

    /// <summary>
    /// Whether audio is routed to the headset jack instead of the internal speaker.
    /// </summary>
    private volatile bool _playToHeadset;

    /// <summary>
    /// Last volume/route signature applied to the controller, so the pump only issues
    /// <see cref="IDualSenseAudioOutputs.SetAudioOutput"/> when the configuration changed.
    /// </summary>
    private int _appliedConfigSignature = -1;

    /// <summary>
    /// Whether the Bluetooth audio lane is currently open on the controller. Guarded by
    /// <see cref="_sync"/>; only the pump (and <see cref="Start"/>) mutate it.
    /// </summary>
    private bool _btStreamPrimed;

    /// <summary>
    /// The game's latest output state (rumble, lightbar, player LEDs, trigger effects),
    /// embedded into the Bluetooth audio-lane reports while the lane is open. Guarded
    /// by <see cref="_sync"/>; set from the libVIIPER output-state callback thread.
    /// </summary>
    private SetStateData? _gameState;

    /// <summary>
    /// Phase (in cycles) of the rumble-emulation haptics oscillators, advanced once per
    /// 3 kHz haptics sample so the waveform stays continuous across report boundaries.
    /// </summary>
    private double _rumbleLeftPhase;

    private double _rumbleRightPhase;

    /// <summary>
    /// Smoothed magnitude (0-1) of each rumble-emulation oscillator, approached
    /// exponentially per sample so strength changes ramp instead of clicking.
    /// </summary>
    private float _rumbleLeftMagnitude;

    private float _rumbleRightMagnitude;

    /// <summary>
    /// Whether the pump is currently running.
    /// </summary>
    public bool IsActive
    {
        get
        {
            return _cts is not null;
        }
    }

    /// <summary>
    /// Speaker volume (0-255) applied to the controller's speaker/headset hardware.
    /// </summary>
    public byte SpeakerVolume
    {
        get
        {
            return _speakerVolume;
        }
        set
        {
            _speakerVolume = value;
        }
    }

    /// <summary>
    /// Whether native game haptics are forwarded to the physical controller.
    /// </summary>
    public bool HapticsEnabled
    {
        get
        {
            return _hapticsEnabled;
        }
        set
        {
            _hapticsEnabled = value;
        }
    }

    /// <summary>
    /// Haptic vibration strength multiplier (1.0 = full, 2.0 = 200%).
    /// </summary>
    public float HapticStrength
    {
        get
        {
            return _hapticStrength;
        }
        set
        {
            _hapticStrength = Math.Clamp(value, 0f, 2f);
        }
    }

    /// <summary>
    /// Whether audio is routed to the headset jack instead of the internal speaker.
    /// </summary>
    public bool PlayToHeadset
    {
        get
        {
            return _playToHeadset;
        }
        set
        {
            _playToHeadset = value;
        }
    }

    /// <summary>
    /// Creates the forwarder for the given physical controller lanes. At least one lane
    /// must be non-<c>null</c>; the forwarder takes ownership of <paramref name="usbTarget"/>.
    /// </summary>
    public ViiperDualSenseAudioForwarder(IDualSenseAudioOutputs? outputs, DualSenseUsbAudioTarget? usbTarget)
        : this(outputs, usbTarget, NativeAudioTimingDiagnostics.Current)
    {
    }

    internal ViiperDualSenseAudioForwarder(
        IDualSenseAudioOutputs? outputs,
        DualSenseUsbAudioTarget? usbTarget,
        NativeAudioTimingDiagnostics timingDiagnostics)
    {
        _outputs = outputs;
        _usbTarget = usbTarget;
        _timingDiagnostics = timingDiagnostics;
    }

    /// <summary>
    /// Starts the pump and opens the USB render endpoint (when available). The
    /// Bluetooth audio lane is intentionally left closed until the first audio block
    /// is actually fed, so an idle forwarder never occupies the lane â€” which would
    /// make the controller ignore the virtual controller's output-state reports.
    /// Returns <c>false</c> when no transport is available.
    /// </summary>
    public bool Start()
    {
        lock (_sync)
        {
            if (_cts is not null)
            {
                return true;
            }

            bool bluetooth = _outputs is not null && _outputs.ConnectionType == ConnectionType.Bluetooth;
            bool usb = _usbTarget is not null && _usbTarget.Start();
            if (!bluetooth && !usb)
            {
                _log.Info("No forwarding transport available (neither Bluetooth audio lane nor USB render endpoint)");
                return false;
            }

            _btStreamPrimed = false;
            _appliedConfigSignature = -1;
            _captureActivitySeen = false;
            ClearTransientHapticsLocked();
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => PumpLoop(_cts.Token));
            _log.Info($"Audio forwarding started ({(bluetooth ? "bluetooth" : "usb")})");
            return true;
        }
    }

    /// <summary>
    /// Stops the pump and releases the transports. Safe to call repeatedly.
    /// </summary>
    public void Stop()
    {
        NativeAudioTimingDiagnostics diagnostics = _timingDiagnostics.CaptureSession();
        CancellationTokenSource? cts;
        Task? loopTask;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            loopTask = _loopTask;
            _loopTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        loopTask?.Wait(TimeSpan.FromSeconds(2));
        _usbTarget?.Stop();

        bool disableMicrophone;
        SetStateData disableState = new SetStateData();
        BluetoothAudioRoute disableRoute = BluetoothAudioRoute.Speaker;
        lock (_sync)
        {
            _audioRing.Clear();
            _btStreamPrimed = false;
            _captureActivitySeen = false;
            disableMicrophone = _microphoneSessionActive
                                && _outputs is not null
                                && _outputs.ConnectionType == ConnectionType.Bluetooth;
            if (disableMicrophone)
            {
                // Snapshot the final ordered state while the callbacks are excluded,
                // then release the state lock before the synchronous HID write below.
                // A disconnect or a slow controller must not stall the native output
                // and capture callback paths during shutdown.
                disableState = CreateAudioState(false);
                disableRoute = _playToHeadset
                    ? BluetoothAudioRoute.Headset
                    : BluetoothAudioRoute.Speaker;
            }

            _microphoneSessionActive = false;
            ClearTransientHapticsLocked();
            if (disableMicrophone)
            {
                // Preserve the installed fixed17 teardown ordering: callbacks remain
                // excluded while the final duplex-off report is written.
                DisableBluetoothMicrophone(disableState, disableRoute, diagnostics);
            }
        }

        _log.Info("Audio forwarding stopped");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _pipeline.Dispose();
        }

        _usbTarget?.Dispose();
    }

    /// <summary>
    /// Feeds one chunk of interleaved 48 kHz stereo float PCM captured from the host.
    /// Blocks are accumulated and pushed to the ring whole; a partially filled tail
    /// block is kept for the next call. This overload has no paired native haptics
    /// source, so the haptics channels for these blocks are intentional silence.
    /// Safe to call from the capture callback.
    /// </summary>
    public void FeedPcm(ReadOnlySpan<float> stereo)
    {
        FeedPcm(stereo, ReadOnlySpan<short>.Empty);
    }

    /// <summary>
    /// Whether the built-in physical microphone should be requested while the
    /// forwarder is running. This is an explicit per-controller opt-in.
    /// </summary>
    public bool MicrophoneEnabled
    {
        get
        {
            lock (_sync)
            {
                return _microphoneEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                _microphoneEnabled = value && _outputs?.ConnectionType == ConnectionType.Bluetooth;
                if (_cts is not null)
                {
                    _btStreamPrimed = false;
                }
            }
        }
    }

    /// <summary>
    /// Feeds one chunk of interleaved 48 kHz stereo speaker PCM together with its
    /// paired interleaved 48 kHz rear-haptics PCM. The two spans must contain the same
    /// number of samples. A non-empty haptics span is marked present even when every
    /// sample is zero, preserving native silence as a real value.
    /// </summary>
    /// <param name="stereo">Front speaker channels, interleaved L/R.</param>
    /// <param name="nativeHaptics">Rear voice-coil channels, interleaved L/R.</param>
    public void FeedPcm(ReadOnlySpan<float> stereo, ReadOnlySpan<short> nativeHaptics)
    {
        FeedPcm(stereo, nativeHaptics, _timingDiagnostics.CaptureSession());
    }

    internal void FeedPcm(
        ReadOnlySpan<float> stereo,
        ReadOnlySpan<short> nativeHaptics,
        NativeAudioTimingDiagnostics diagnostics)
    {
        if (!nativeHaptics.IsEmpty && nativeHaptics.Length != stereo.Length)
        {
            throw new ArgumentException("Native haptics must have the same interleaved sample count as stereo PCM.", nameof(nativeHaptics));
        }

        lock (_sync)
        {
            if (_cts is null)
            {
                return;
            }

            // A microphone-only Bluetooth session must remain an idle lane until
            // capture has observed a real speaker or native-haptics sample.  The
            // VIIPER callback can otherwise feed all-zero host-render blocks into
            // the ring indefinitely, making PumpLoop treat capture silence as active
            // speaker audio and consuming the same lane needed by mic reports. This
            // is an exact digital-zero startup gate, not VAD: after the first non-zero
            // value every subsequent zero block is preserved with its original timing.
            if (ShouldSuppressInitialCaptureSilenceLocked(stereo, nativeHaptics))
            {
                return;
            }

            if (!_captureActivitySeen
                && (ContainsNonZero(stereo) || ContainsNonZero(nativeHaptics)))
            {
                _captureActivitySeen = true;
            }

            bool timing = diagnostics.IsEnabled;
            int droppedBlocks = 0;
            int offset = 0;
            int partial = _audioRing.PartialCount;
            if (partial > 0)
            {
                int take = Math.Min(BlockSamples - partial, stereo.Length - offset);
                _audioRing.AppendPartial(stereo.Slice(offset, take),
                    nativeHaptics.IsEmpty ? ReadOnlySpan<short>.Empty : nativeHaptics.Slice(offset, take));
                offset += take;
                if (_audioRing.PartialCount == BlockSamples)
                {
                    if (_audioRing.PushPartial() && timing)
                    {
                        droppedBlocks++;
                    }
                }
            }

            while (offset + BlockSamples <= stereo.Length)
            {
                if (_audioRing.Push(
                    stereo.Slice(offset, BlockSamples),
                    nativeHaptics.IsEmpty ? ReadOnlySpan<short>.Empty : nativeHaptics.Slice(offset, BlockSamples))
                    && timing)
                {
                    droppedBlocks++;
                }

                offset += BlockSamples;
            }

            if (offset < stereo.Length)
            {
                _audioRing.AppendPartial(stereo.Slice(offset),
                    nativeHaptics.IsEmpty ? ReadOnlySpan<short>.Empty : nativeHaptics.Slice(offset));
            }

            if (timing)
            {
                diagnostics.RecordQueueState(_audioRing.Count, droppedBlocks);
            }
        }
    }

    /// <summary>
    /// Stores the game's latest output state so the pump can embed it into the
    /// Bluetooth audio-lane reports (combined <c>0x36</c> and <c>0x32</c> init-prime)
    /// and drive the classic rumble motors through the haptics PCM. Called from the
    /// libVIIPER output-state callback thread; safe to call while the forwarder is
    /// stopped.
    /// </summary>
    public void UpdateGameOutputState(SetStateData state)
    {
        lock (_sync)
        {
            _gameState = state;
        }
    }

    /// <summary>
    /// Retains the latest game state for the next already-queued Bluetooth combined
    /// audio report. Returns false when no near-term combined carrier is available so
    /// the caller can preserve the direct output path (USB, idle audio, or disabled
    /// haptics/microphone).
    /// </summary>
    public bool TryCoalesceGameOutputState(SetStateData state)
    {
        lock (_sync)
        {
            if (_outputs is null
                || _outputs.ConnectionType != ConnectionType.Bluetooth
                || _cts is null
                || (!_hapticsEnabled && !_microphoneEnabled)
                || _audioRing.Count == 0)
            {
                return false;
            }

            _gameState = state;
            return true;
        }
    }

    /// <summary>
    /// Stores the game's actual haptics payload from libVIIPER's realtime-haptics
    /// callback, so the pump can embed it into the Bluetooth audio-lane reports. The
    /// haptics are extracted from the 398-byte combined Bluetooth report carried by
    /// the callback (<c>0x36</c> header with the 64-byte haptics PCM at offset
    /// <see cref="BluetoothCombinedHapticsPcmOffset"/>); reports that do not match the
    /// expected shape are rejected. A valid all-zero report is retained as explicit
    /// native silence. Called from the libVIIPER callback thread; safe to call while
    /// the forwarder is stopped.
    /// </summary>
    public void UpdateGameHaptics(DSOutputState output)
    {
        byte[] combined = output.BluetoothCombinedOutputReport;
        if (combined is not { Length: 398 } || combined[0] != 0x36
                                            || combined[11] != 0x90 || combined[12] != 0x3F)
        {
            if (Interlocked.Exchange(ref _gameHapticsRejectedLogged, 1) == 0)
            {
                string head = combined is null
                    ? "(null)"
                    : string.Join(" ", combined.AsSpan(0, Math.Min(16, combined.Length)).ToArray().Select(b => b.ToString("X2")));
                _log.Info(
                    $"Realtime-haptics combined report rejected (expected 0x36 report with 0x90 0x3F state block and 64-byte haptics at offset 78); head {head}");
            }

            return;
        }

        lock (_sync)
        {
            combined.AsSpan(BluetoothCombinedHapticsPcmOffset, DualSenseBtAudioPipeline.HapticsBytes)
                .CopyTo(_gameHapticsPcm);
            if (!_captureActivitySeen
                && ContainsNonZero(combined.AsSpan(
                    BluetoothCombinedHapticsPcmOffset,
                    DualSenseBtAudioPipeline.HapticsBytes)))
            {
                _captureActivitySeen = true;
            }
            _lastGameHapticsTimestamp = Stopwatch.GetTimestamp();
        }

        if (Interlocked.Exchange(ref _gameHapticsAcceptedLogged, 1) == 0)
        {
            _log.Info("Game haptics active: the pad now reproduces the game's own haptics payload");
        }
    }

    /// <summary>
    /// Discards all buffered audio and marks the Bluetooth audio lane as closed, so the
    /// next audio block re-primes the stream. Called when the virtual controller's
    /// audio interface resets or changes alternate setting (a stream-generation
    /// barrier), so stale PCM from the previous generation cannot burst into the pad's
    /// shallow receive buffer. Safe to call from the libVIIPER speaker-reset callback
    /// thread.
    /// </summary>
    public void Flush()
    {
        lock (_outputIoSync)
        {
            lock (_sync)
            {
                _audioRing.Clear();
                _btStreamPrimed = false;
                _captureActivitySeen = false;
                ClearTransientHapticsLocked();
            }
        }
    }

    /// <summary>
    /// Runs the pump loop until canceled: waits for audio to appear in the ring, then
    /// pops one block per tick and fans it out to every active transport. Ticks are
    /// paced against an accumulated deadline so the average cadence matches
    /// <see cref="TickInterval"/> exactly (a per-tick restart would drift). While the
    /// ring is empty the pump idles without sending, so an idle forwarder never
    /// occupies the Bluetooth audio lane (which would corrupt <see cref="DualSenseAudioPlayer"/>
    /// playback and make the controller ignore the virtual controller's output-state
    /// reports). A microphone opt-in primes the lane once but does not turn empty
    /// speaker blocks into recurring output reports. The first speaker block of a
    /// session also primes the lane, and after the lane has been idle beyond
    /// <see cref="RePrimeIdleTicks"/> the next session re-primes.
    /// When audio resumes, the deadline is re-anchored to now.
    /// </summary>
    private void PumpLoop(CancellationToken ct)
    {
        _log.Debug("Pump loop started");
        TimerResolution.AddRef();
        try
        {
            long tickPeriodTicks = (long)(Stopwatch.Frequency * TickInterval.TotalSeconds);
            long nextTick = Stopwatch.GetTimestamp() + tickPeriodTicks;
            long idleSinceTicks = long.MinValue;
            while (!ct.IsCancellationRequested)
            {
                bool wasIdle = false;
                // A microphone session needs one priming transaction, but it does not
                // need a speaker-silence report on every 10.667 ms tick. The standalone
                // Bluetooth probe keeps the duplex request active after one activation
                // report; continuously sending empty 0x36 reports here consumes the same
                // Bluetooth output lane as real audio and can starve microphone input.
                // Keep the pump asleep until speaker audio arrives (or a reset explicitly
                // invalidates the primed stream).
                while (!ct.IsCancellationRequested
                       && _audioRing.Count == 0
                       && !BluetoothMicrophoneNeedsPrime())
                {
                    if (!wasIdle)
                    {
                        wasIdle = true;
                        idleSinceTicks = Stopwatch.GetTimestamp();
                    }

                    Thread.Sleep(2);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                NativeAudioTimingDiagnostics diagnostics = _timingDiagnostics.CaptureSession();

                if (wasIdle)
                {
                    // After a long enough empty gap the pad's audio buffer has
                    // underrun, so the next session must open the lane again. A
                    // microphone-only idle period is intentionally not treated as an
                    // audio-session loss: re-priming would reintroduce periodic output
                    // bursts and interrupt the otherwise continuous mic lane. When audio
                    // resumes, the pending block below re-primes if the gap was long.
                    if (_audioRing.Count > 0
                        && _outputs is not null && _outputs.ConnectionType == ConnectionType.Bluetooth
                        && Stopwatch.GetTimestamp() - idleSinceTicks >= RePrimeIdleTicks)
                    {
                        lock (_sync)
                        {
                            _btStreamPrimed = false;
                        }
                    }

                    // Re-anchor after an idle gap: the accumulated deadline would be far
                    // in the past, so the loop would burst catch-up frames into the pad's
                    // shallow receive buffer (which drops audio and crackles).
                    nextTick = Stopwatch.GetTimestamp();
                }

                bool microphoneOnlyIdle = false;
                try
                {
                    bool needsBluetoothPrime;
                    lock (_sync)
                    {
                        needsBluetoothPrime = _outputs is not null
                                              && _outputs.ConnectionType == ConnectionType.Bluetooth
                                              && !_btStreamPrimed;
                    }

                    // Priming performs synchronous Bluetooth I/O and an ~85 ms
                    // preroll. Do not hold the state lock across it: the native
                    // libVIIPER audio callback runs on the virtual-USB transfer
                    // path and must be able to enqueue the next PCM block while
                    // the physical lane is being opened.
                    if (needsBluetoothPrime)
                    {
                        PrimeBluetoothStream(ct, diagnostics);
                    }

                    lock (_sync)
                    {
                        microphoneOnlyIdle = _audioRing.Count == 0
                                              && _microphoneEnabled
                                              && _outputs is not null
                                              && _outputs.ConnectionType == ConnectionType.Bluetooth
                                              && _btStreamPrimed;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogExceptionDetails(ex);
                    break;
                }

                if (!microphoneOnlyIdle)
                {
                    try
                    {
                        ProcessBlock(ct, diagnostics);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.LogExceptionDetails(ex);
                        break;
                    }
                }

                long scheduledTick = nextTick + tickPeriodTicks;
                nextTick = Math.Max(scheduledTick, Stopwatch.GetTimestamp());
                WaitUntil(nextTick);
                if (diagnostics.IsEnabled)
                {
                    long lateness = Stopwatch.GetTimestamp() - scheduledTick;
                    diagnostics.RecordDuration(AudioTimingMetric.PumpLateness, Math.Max(0, lateness));
                }
            }
        }
        finally
        {
            TimerResolution.Release();
        }

        _log.Debug("Pump loop ended");
    }

    /// <summary>
    /// Returns whether the Bluetooth microphone session still needs its one-time
    /// activation. A primed microphone-only stream is deliberately not a clock source:
    /// empty speaker blocks must not become repeated physical output reports.
    /// </summary>
    private bool BluetoothMicrophoneNeedsPrime()
    {
        lock (_sync)
        {
            return _microphoneEnabled
                   && _outputs is not null
                   && _outputs.ConnectionType == ConnectionType.Bluetooth
                   && !_btStreamPrimed;
        }
    }

    /// <summary>
    /// Suppresses only leading all-zero host-capture blocks for an opted-in Bluetooth
    /// microphone session. Speaker silence after activity, native-haptics-only blocks,
    /// and any non-Bluetooth/disabled-microphone route remain untouched.
    /// Caller must hold <see cref="_sync"/>.
    /// </summary>
    private bool ShouldSuppressInitialCaptureSilenceLocked(
        ReadOnlySpan<float> stereo,
        ReadOnlySpan<short> nativeHaptics)
    {
        return !_captureActivitySeen
               && _microphoneEnabled
               && _outputs is not null
               && _outputs.ConnectionType == ConnectionType.Bluetooth
               && !ContainsNonZero(stereo)
               && !ContainsNonZero(nativeHaptics);
    }

    private static bool ContainsNonZero(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            if (sample != 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNonZero(ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
        {
            if (sample != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> samples)
    {
        foreach (byte sample in samples)
        {
            if (sample != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Consumes one block from the ring and feeds every active transport.
    /// </summary>
    private void ProcessBlock(CancellationToken ct, NativeAudioTimingDiagnostics diagnostics)
    {
        ct.ThrowIfCancellationRequested();
        bool timing = diagnostics.IsEnabled;
        long outputLockWaitStarted = timing ? Stopwatch.GetTimestamp() : 0;
        long computeStarted = 0;
        bool queueDiscardedBlock = false;

        bool bluetooth;
        bool usb;
        bool applyAudioConfiguration = false;
        bool combinedReport = false;
        AudioControl audioControl = default;
        byte speakerVolume = 0;
        byte headphoneVolume = 0;
        SetStateData state = new SetStateData();
        BluetoothAudioRoute route = BluetoothAudioRoute.Speaker;

        lock (_outputIoSync)
        {
            if (timing)
            {
                diagnostics.RecordDurationFromTimestamp(
                    AudioTimingMetric.ForwarderOutputLockWait,
                    outputLockWaitStarted);
                computeStarted = Stopwatch.GetTimestamp();
            }

            // The output lock intentionally covers preparation as well as the physical
            // send: Flush uses the same I/O-before-state order, so it cannot clear the
            // shared scratch buffers between preparation and send while a reset waits.
            // The synchronous physical I/O itself remains outside _sync, keeping the
            // capture and VIIPER callback paths responsive while a HID write is slow.
            lock (_sync)
            {
                if (!_audioRing.TryPop(_floatBlock, _nativeHapticsBlock, out bool nativeHapticsPresent))
                {
                    _floatBlock.AsSpan().Clear();
                    _nativeHapticsBlock.AsSpan().Clear();
                    nativeHapticsPresent = false;
                }
                else if (_audioRing.Count > TargetBlocks)
                {
                    queueDiscardedBlock = _audioRing.TryPop(_discardBlock);
                }

                DualSenseBtAudioPipeline.ConvertToPcm16(_floatBlock, _pcmBlock);

                bluetooth = _outputs is not null && _outputs.ConnectionType == ConnectionType.Bluetooth;
                if (bluetooth)
                {
                    applyAudioConfiguration = PrepareAudioStateIfChanged(
                        out audioControl,
                        out speakerVolume,
                        out headphoneVolume);
                    DualSenseBtAudioPipeline.ResampleToOpusBlock(_pcmBlock, _opusBlock);
                    _pipeline.EncodeOpus(_opusBlock, _opusFrame);
                    if (_hapticsEnabled)
                    {
                        FillHapticsPcm(nativeHapticsPresent);
                    }
                    else
                    {
                        // Microphone duplex reports still carry the combined haptics
                        // section even when user haptics are disabled. Do not replay
                        // the previous block when the speaker ring is empty.
                        _hapticsPcm.AsSpan().Clear();
                    }

                    combinedReport = _hapticsEnabled || _microphoneEnabled;
                    if (combinedReport)
                    {
                        MixRumbleIntoHaptics();
                    }

                    state = CreateAudioState();
                    route = _playToHeadset ? BluetoothAudioRoute.Headset : BluetoothAudioRoute.Speaker;
                }

                if (timing && queueDiscardedBlock)
                {
                    diagnostics.RecordQueueDrop();
                }

                usb = _usbTarget is { IsActive: true };
                if (usb)
                {
                    ToUsbQuadS16(_pcmBlock, _nativeHapticsBlock, nativeHapticsPresent, _usbQuadBlock);
                }
            }

            if (!bluetooth && !usb)
            {
                if (timing)
                {
                    diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.ForwarderCompute, computeStarted);
                }

                return;
            }

            if (timing)
            {
                diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.ForwarderCompute, computeStarted);
            }

            if (bluetooth)
            {
                if (applyAudioConfiguration)
                {
                    SetAudioOutputTimed(audioControl, speakerVolume, headphoneVolume, diagnostics);
                }

                SendBluetoothReports(state, combinedReport, route, diagnostics);
            }

            if (usb)
            {
                _usbTarget!.Feed(_usbQuadBlock);
            }
        }
    }

    /// <summary>
    /// Fills <see cref="_hapticsPcm"/> for the current block. The game's own realtime
    /// payload takes priority while it is fresh; otherwise paired rear haptics from
    /// the native four-channel endpoint are decimated. When neither native source is
    /// present, the output is silence. Speaker audio is never used as a fallback.
    /// Caller must hold <see cref="_sync"/>.
    /// </summary>
    private void FillHapticsPcm(bool nativeHapticsPresent)
    {
        if (nativeHapticsPresent)
        {
            DualSenseBtAudioPipeline.ToHapticsPcm(_nativeHapticsBlock, _hapticsPcm, _hapticStrength);
        }
        else
        {
            _hapticsPcm.AsSpan().Clear();
        }

        // A valid realtime all-zero payload is still authoritative silence.
        TryGetFreshGameHaptics(_hapticsPcm);
    }

    /// <summary>
    /// Copies the freshest realtime-haptics payload into <paramref name="destination"/>
    /// if one was delivered recently. A payload is fresh while its timestamp is within
    /// <see cref="GameHapticsFreshWindowTicks"/>. The return value indicates presence,
    /// not signal energy: an all-zero payload is an explicit native silence and must
    /// suppress every other haptics source. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private bool TryGetFreshGameHaptics(Span<byte> destination)
    {
        if (_lastGameHapticsTimestamp == 0
            || Stopwatch.GetTimestamp() - _lastGameHapticsTimestamp > GameHapticsFreshWindowTicks)
        {
            return false;
        }

        _gameHapticsPcm.CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Sends the Bluetooth audio frame to the configured route, folding the haptics
    /// packet into the combined <c>0x36</c> report when haptics are enabled.
    /// </summary>
    private void SendBluetoothReports()
    {
        NativeAudioTimingDiagnostics diagnostics = _timingDiagnostics.CaptureSession();
        SetStateData state = CreateAudioState();
        BluetoothAudioRoute route = _playToHeadset ? BluetoothAudioRoute.Headset : BluetoothAudioRoute.Speaker;
        bool combinedReport = _hapticsEnabled || _microphoneEnabled;
        if (combinedReport)
        {
            MixRumbleIntoHaptics();
        }

        SendBluetoothReports(state, combinedReport, route, diagnostics);
    }

    /// <summary>
    /// Sends already-prepared Bluetooth data. The caller must hold
    /// <see cref="_outputIoSync"/>; no forwarder state is read here so the physical
    /// write can safely happen after <see cref="_sync"/> is released.
    /// </summary>
    private void SendBluetoothReports(
        SetStateData state,
        bool combinedReport,
        BluetoothAudioRoute route,
        NativeAudioTimingDiagnostics diagnostics)
    {
        bool timing = diagnostics.IsEnabled;
        long started = timing ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (combinedReport)
            {
                _outputs!.SendBluetoothAudioAndHaptics(state, _opusFrame, _hapticsPcm, route);
            }
            else
            {
                _outputs!.SendBluetoothAudio(_opusFrame, route);
            }
        }
        finally
        {
            if (timing)
            {
                diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.BluetoothAudioWrite, started);
            }
        }
    }

    private void SetAudioOutputTimed(
        AudioControl audioControl,
        byte speakerVolume,
        byte headphoneVolume,
        NativeAudioTimingDiagnostics diagnostics)
    {
        bool timing = diagnostics.IsEnabled;
        long started = timing ? Stopwatch.GetTimestamp() : 0;
        try
        {
            _outputs!.SetAudioOutput(audioControl, speakerVolume, headphoneVolume);
        }
        finally
        {
            if (timing)
            {
                diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.AudioConfigurationWrite, started);
            }
        }
    }

    /// <summary>
    /// Applies the volume/route configuration to the controller's speaker/headset
    /// hardware when it changed since the last tick.
    /// </summary>
    private bool PrepareAudioStateIfChanged(
        out AudioControl audioControl,
        out byte speakerVolume,
        out byte headphoneVolume)
    {
        bool headset = _playToHeadset;
        byte volume = _speakerVolume;
        int signature = volume | (headset ? 1 << 8 : 0);
        if (signature == _appliedConfigSignature)
        {
            audioControl = default;
            speakerVolume = 0;
            headphoneVolume = 0;
            return false;
        }

        _appliedConfigSignature = signature;
        audioControl = headset ? AudioControl.OutputPathHeadphones : AudioControl.OutputPathSpeaker;
        speakerVolume = volume;
        headphoneVolume = headset ? volume : (byte)0x3F;
        return true;
    }

    /// <summary>
    /// The controller's output path selected by <see cref="PlayToHeadset"/>.
    /// </summary>
    private AudioControl OutputControl
    {
        get
        {
            return _playToHeadset ? AudioControl.OutputPathHeadphones : AudioControl.OutputPathSpeaker;
        }
    }

    /// <summary>
    /// Headphone volume for the active route: the headset gets the slider value while
    /// the speaker route keeps the hardware default.
    /// </summary>
    private byte HeadphoneVolume
    {
        get
        {
            return _playToHeadset ? _speakerVolume : (byte)0x3F;
        }
    }

    /// <summary>
    /// Builds the 47-byte output state embedded in both the init-prime and the per-tick
    /// combined <c>0x36</c> report: the game's output state overridden by the
    /// forwarder's audio configuration. The rumble-mode bits are cleared — while the
    /// audio lane is open the pad ignores the motor bytes (and switching modes
    /// mid-stream breaks audio), so rumble is driven through the haptics PCM instead.
    /// Every other field rides through exactly as the game wrote it, including the
    /// trigger FFB allow bits: with a bit set the pad applies the embedded block each
    /// tick; with a bit clear the pad retains its effect, matching real hardware.
    /// Caller must hold <see cref="_sync"/>.
    /// </summary>
    private SetStateData CreateAudioState(bool? microphoneOverride = null)
    {
        // SetStateData is a readonly struct over a mutable byte[]; a `with` expression
        // otherwise aliases _gameState's buffer and clears the game's rumble selectors
        // in place on the first combined report. Keep the callback snapshot immutable
        // while merging the audio-lane fields for this report.
        SetStateData game = _gameState is { } state
            ? new SetStateData(state.AsSpan().ToArray(), 0)
            : new SetStateData();

        // Recompute the flags from the game's bytes every tick so the merged report
        // cannot accumulate audio-lane overrides across callbacks.
        ValidFlags flag0 = (game.ValidFlag0 & ~(ValidFlags.EnableRumbleEmulation | ValidFlags.UseRumbleNotHaptics))
                           | ValidFlags.AllowSpeakerVolume | ValidFlags.AllowHeadphoneVolume
                           | ValidFlags.AllowAudioControl;
        ValidFlags flag1 = game.ValidFlag1 | ValidFlags.AllowAudioControl2;
        ValidFlags flag2 = game.ValidFlag2 & ~ValidFlags.EnableImprovedRumbleEmu;

        bool microphone = microphoneOverride ?? _microphoneEnabled;
        if (microphone)
        {
            flag0 |= ValidFlags.AllowMicVolume;
            flag1 |= ValidFlags.AllowMuteLight | ValidFlags.AllowAudioMute;
        }

        return game with
        {
            ValidFlag0 = flag0,
            ValidFlag1 = flag1,
            ValidFlag2 = flag2,
            SpeakerVolume = _speakerVolume,
            HeadphoneVolume = HeadphoneVolume,
            AudioControl = microphone
                ? OutputControl | AudioControl.MicSelectInternal
                : OutputControl,
            MicVolume = microphone ? (byte)0x40 : game.MicVolume,
            PowerSaveControl = microphone
                ? GetMicrophonePowerSaveControl(game)
                : game.PowerSaveControl,
            AudioControl2 = 0x02
        };
    }

    /// <summary>
    /// Combines the physical mute status with the game's own microphone-mute request
    /// while preserving every other power-save bit. A physical mute is additive and
    /// cannot be undone by a host tick; a host mute remains in force after the
    /// physical button is released. The input connection status is authoritative for
    /// the physical button; no button-edge event is required because Bluetooth audio
    /// ticks repeatedly publish this state.
    /// </summary>
    private byte GetMicrophonePowerSaveControl(SetStateData game)
    {
        byte powerSave = game.PowerSaveControl;
        if (_outputs?.MicrophoneMuted == true)
        {
            powerSave |= MicrophoneMutePowerSaveBit;
        }

        return powerSave;
    }

    /// <summary>
    /// Mixes the game's classic rumble motors into the haptics PCM: each motor is
    /// synthesized as a sine wave (60 Hz for the left/strong motor, 180 Hz for the
    /// right/weak motor, matching the reference Gamepad haptics-PCM polyfill) and
    /// summed into both haptics channels, clamped to the s8 range. The magnitudes are
    /// exponentially smoothed (about 50 ms) so strength changes ramp instead of
    /// clicking. While the audio lane is open the pad ignores the state block's motor
    /// bytes, so this is the only rumble path during streaming. Caller must hold
    /// <see cref="_sync"/>.
    /// </summary>
    private void MixRumbleIntoHaptics()
    {
        if (_gameState is not { } game)
        {
            return;
        }

        bool rumbleSelected = (game.ValidFlag0 & ValidFlags.EnableRumbleEmulation) != 0
                              || (game.ValidFlag2 & ValidFlags.EnableImprovedRumbleEmu) != 0;
        byte leftTarget = rumbleSelected ? game.RumbleLeft : (byte)0;
        byte rightTarget = rumbleSelected ? game.RumbleRight : (byte)0;

        // Haptics sample rate: 32 frames per 512/48000 s tick = 3 kHz.
        const double rate = DualSenseBtAudioPipeline.HapticsFrames
                            / (DualSenseBtAudioPipeline.FramesPerBlock / (double)DualSenseBtAudioPipeline.SampleRate);
        double smoothPerSample = 1.0 - Math.Exp(-1.0 / (0.05 * rate));
        const double twoPi = 2.0 * Math.PI;
        double leftStep = 60.0 / rate;
        double rightStep = 180.0 / rate;

        Span<byte> haptics = _hapticsPcm;
        for (int i = 0; i < DualSenseBtAudioPipeline.HapticsFrames; i++)
        {
            _rumbleLeftMagnitude += (float)((leftTarget / 255.0 - _rumbleLeftMagnitude) * smoothPerSample);
            _rumbleRightMagnitude += (float)((rightTarget / 255.0 - _rumbleRightMagnitude) * smoothPerSample);

            _rumbleLeftPhase += leftStep;
            _rumbleRightPhase += rightStep;

            double wave = _rumbleLeftMagnitude * Math.Sin(twoPi * _rumbleLeftPhase)
                          + _rumbleRightMagnitude * Math.Sin(twoPi * _rumbleRightPhase);
            int sample = (int)Math.Round(wave * 127.0);

            int left = (sbyte)haptics[i * 2] + sample;
            int right = (sbyte)haptics[i * 2 + 1] + sample;
            haptics[i * 2] = (byte)Math.Clamp(left, -128, 127);
            haptics[i * 2 + 1] = (byte)Math.Clamp(right, -128, 127);
        }
    }

    /// <summary>
    /// Expands a stereo PCM block into the quad S16 USB representation: channels 1/2
    /// carry the speaker audio and channels 3/4 carry the paired native rear haptics.
    /// An authoritative fresh realtime payload is expanded to the 48 kHz USB cadence
    /// only when no paired rear source exists. Otherwise absent/disabled haptics are
    /// silence; front audio is never copied into channels 3/4.
    /// </summary>
    private void ToUsbQuadS16(ReadOnlySpan<short> stereo, ReadOnlySpan<short> nativeHaptics,
        bool nativeHapticsPresent, Span<short> quad)
    {
        bool useRealtime = _hapticsEnabled && !nativeHapticsPresent && TryGetFreshGameHaptics(_hapticsPcm);
        int framesPerHapticsSample = DualSenseBtAudioPipeline.FramesPerBlock / DualSenseBtAudioPipeline.HapticsFrames;
        for (int f = 0; f < DualSenseBtAudioPipeline.FramesPerBlock; f++)
        {
            int o = f * 4;
            short left = stereo[f * 2];
            short right = stereo[f * 2 + 1];
            quad[o] = left;
            quad[o + 1] = right;
            if (!_hapticsEnabled || (!nativeHapticsPresent && !useRealtime))
            {
                quad[o + 2] = 0;
                quad[o + 3] = 0;
            }
            else if (nativeHapticsPresent)
            {
                quad[o + 2] = ScaleHaptics(nativeHaptics[f * 2], _hapticStrength);
                quad[o + 3] = ScaleHaptics(nativeHaptics[f * 2 + 1], _hapticStrength);
            }
            else
            {
                int hapticFrame = f / framesPerHapticsSample;
                quad[o + 2] = ScaleHaptics((short)((sbyte)_hapticsPcm[hapticFrame * 2] * 256), _hapticStrength);
                quad[o + 3] = ScaleHaptics((short)((sbyte)_hapticsPcm[hapticFrame * 2 + 1] * 256), _hapticStrength);
            }
        }
    }

    /// <summary>
    /// Applies the user haptics strength without allowing integer overflow or a
    /// float-to-short wrap to corrupt the physical USB stream.
    /// </summary>
    private static short ScaleHaptics(short sample, float strength)
    {
        return (short)Math.Clamp(sample * strength, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Clears native haptics and rumble synthesis state at a stream-generation or
    /// lifecycle barrier. The latest game output snapshot is retained so an explicit
    /// conventional rumble request survives a capture reset and can be synthesized
    /// again on the next block. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private void ClearTransientHapticsLocked()
    {
        _gameHapticsPcm.AsSpan().Clear();
        _nativeHapticsBlock.AsSpan().Clear();
        _hapticsPcm.AsSpan().Clear();
        _lastGameHapticsTimestamp = 0;
        _rumbleLeftMagnitude = 0;
        _rumbleRightMagnitude = 0;
        _rumbleLeftPhase = 0;
        _rumbleRightPhase = 0;
    }

    /// <summary>
    /// Opens the Bluetooth audio lane on the controller: resets the report sequence and
    /// Opus encoder, applies the audio configuration, sends the <c>0x32</c> init-prime
    /// and warms the stream with <see cref="SendBtPreroll"/>. Blocks ~85 ms. Called from
    /// the pump right before the first report of an audio session, so an idle forwarder
    /// never occupies the lane. Physical I/O is serialized with the normal output
    /// path, but the state lock is held only while taking a short immutable snapshot.
    /// </summary>
    private void PrimeBluetoothStream(CancellationToken ct, NativeAudioTimingDiagnostics diagnostics)
    {
        lock (_outputIoSync)
        {
            bool microphone;
            bool headset;
            byte volume;
            bool combinedReport;
            BluetoothAudioRoute route;
            SetStateData state;
            byte[] haptics;

            lock (_sync)
            {
                if (_outputs is null || _outputs.ConnectionType != ConnectionType.Bluetooth
                    || _btStreamPrimed || _cts is null)
                {
                    return;
                }

                microphone = _microphoneEnabled;
                headset = _playToHeadset;
                volume = _speakerVolume;
                combinedReport = _hapticsEnabled || microphone;
                route = headset ? BluetoothAudioRoute.Headset : BluetoothAudioRoute.Speaker;
                state = CreateAudioState(microphone);

                // The preroll is intentionally silent. Preserve only the current
                // rumble request in its haptics lane, then let the first real block
                // rebuild the mutable scratch buffers after the prime completes.
                _hapticsPcm.AsSpan().Clear();
                if (combinedReport)
                {
                    MixRumbleIntoHaptics();
                }

                haptics = _hapticsPcm.ToArray();
            }

            _outputs.SetBluetoothMicrophoneEnabled(microphone);
            _outputs.ResetBluetoothAudioStream();
            _pipeline.ResetEncoder();
            SetAudioOutputTimed(
                headset ? AudioControl.OutputPathHeadphones : AudioControl.OutputPathSpeaker,
                volume,
                headset ? volume : (byte)0x3F,
                diagnostics);
            bool timing = diagnostics.IsEnabled;
            long primeStarted = timing ? Stopwatch.GetTimestamp() : 0;
            try
            {
                _outputs.SendBluetoothAudioPrime(state);
            }
            finally
            {
                if (timing)
                {
                    diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.BluetoothAudioWrite, primeStarted);
                }
            }
            SendBtPreroll(ct, state, combinedReport, route, haptics, diagnostics);

            lock (_sync)
            {
                // A setting callback may have changed the requested lane while the
                // physical prime was in flight. Keep the completed hardware prime
                // discoverable for Stop, but force the next block to re-prime when
                // the requested microphone/configuration no longer matches it.
                _btStreamPrimed = _microphoneEnabled == microphone;
                _appliedConfigSignature = _playToHeadset == headset && _speakerVolume == volume
                    ? volume | (headset ? 1 << 8 : 0)
                    : -1;
                _microphoneSessionActive = microphone;
            }
        }

        _log.Debug("Bluetooth audio stream primed");
    }

    /// <summary>
    /// Sends one final combined silence report with the duplex request cleared before
    /// the pump releases the physical Bluetooth audio lane. The fixed17 Stop path calls
    /// this while holding <see cref="_sync"/> to preserve its installed teardown order;
    /// this method serializes the physical write with the pump.
    /// </summary>
    private void DisableBluetoothMicrophone(
        SetStateData state,
        BluetoothAudioRoute route,
        NativeAudioTimingDiagnostics diagnostics)
    {
        if (_outputs is null || _outputs.ConnectionType != ConnectionType.Bluetooth)
        {
            return;
        }

        lock (_outputIoSync)
        {
            try
            {
                _outputs.SetBluetoothMicrophoneEnabled(false);
                _opusBlock.AsSpan().Clear();
                _pipeline.EncodeOpus(_opusBlock, _opusFrame);
                _hapticsPcm.AsSpan().Clear();
                bool timing = diagnostics.IsEnabled;
                long started = timing ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    _outputs.SendBluetoothAudioAndHaptics(
                        state,
                        _opusFrame,
                        _hapticsPcm,
                        route);
                }
                finally
                {
                    if (timing)
                    {
                        diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.BluetoothAudioWrite, started);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to disable the Bluetooth microphone session cleanly: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sends <see cref="BtPrerollPackets"/> of silence audio/haptics reports at the
    /// 10.667 ms cadence so the controller's speaker path and Opus decoder are warmed up
    /// before real audio arrives. Blocks the caller for ~85 ms; call right after priming
    /// the stream. The caller must hold <see cref="_outputIoSync"/>; no state lock is
    /// held while the paced physical writes and waits run.
    /// </summary>
    private void SendBtPreroll(
        CancellationToken ct,
        SetStateData state,
        bool combinedReport,
        BluetoothAudioRoute route,
        ReadOnlySpan<byte> haptics,
        NativeAudioTimingDiagnostics diagnostics)
    {
        if (_outputs is null)
        {
            return;
        }

        _log.Debug($"Sending {BtPrerollPackets} silence packets to warm up the Bluetooth audio stream");
        _opusBlock.AsSpan().Clear();
        try
        {
            _pipeline.EncodeOpus(_opusBlock, _opusFrame);
        }
        catch (InvalidOperationException ex)
        {
            _log.Error("Failed to encode the Bluetooth preroll silence packet; skipping warmup");
            _log.LogExceptionDetails(ex);
            return;
        }

        long prerollTicks = (long)(Stopwatch.Frequency * TickInterval.TotalSeconds);
        long next = Stopwatch.GetTimestamp() + prerollTicks;
        TimerResolution.AddRef();
        try
        {
            for (int i = 0; i < BtPrerollPackets && !ct.IsCancellationRequested; i++)
            {
                if (combinedReport)
                {
                    bool timing = diagnostics.IsEnabled;
                    long started = timing ? Stopwatch.GetTimestamp() : 0;
                    try
                    {
                        _outputs.SendBluetoothAudioAndHaptics(state, _opusFrame, haptics, route);
                    }
                    finally
                    {
                        if (timing)
                        {
                            diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.BluetoothAudioWrite, started);
                        }
                    }
                }
                else
                {
                    bool timing = diagnostics.IsEnabled;
                    long started = timing ? Stopwatch.GetTimestamp() : 0;
                    try
                    {
                        _outputs.SendBluetoothAudio(_opusFrame, route);
                    }
                    finally
                    {
                        if (timing)
                        {
                            diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.BluetoothAudioWrite, started);
                        }
                    }
                }

                next += prerollTicks;
                WaitUntil(next);
            }
        }
        finally
        {
            TimerResolution.Release();
        }
    }

    /// <summary>
    /// Blocks until the stopwatch reaches <paramref name="deadlineTimestamp"/>, sleeping
    /// in whole milliseconds and spinning out the last sub-millisecond.
    /// </summary>
    private static void WaitUntil(long deadlineTimestamp)
    {
        while (Stopwatch.GetTimestamp() < deadlineTimestamp)
        {
            double remainingMs = (deadlineTimestamp - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 1.0)
            {
                Thread.Sleep(Math.Max(1, (int)(remainingMs - 1.0)));
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>
    /// Bounded ring of fixed 512-frame stereo blocks, shared between the capture
    /// callback (producer) and the pump (consumer). Full pushes drop the oldest block;
    /// the pump handles draining back to the target latency.
    /// </summary>
    private sealed class PcmBlockRing
    {
        private readonly Lock _lock = new Lock();
        private readonly float[][] _audioBlocks;
        private readonly short[][] _hapticsBlocks;
        private readonly bool[] _hapticsPresent;
        private readonly int _capacity;
        private int _write;
        private int _read;
        private int _count;

        /// <summary>
        /// Creates a ring holding <paramref name="capacity"/> blocks.
        /// </summary>
        public PcmBlockRing(int capacity)
        {
            _capacity = capacity;
            _audioBlocks = new float[capacity][];
            _hapticsBlocks = new short[capacity][];
            _hapticsPresent = new bool[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _audioBlocks[i] = new float[BlockSamples];
                _hapticsBlocks[i] = new short[BlockSamples];
            }
        }

        /// <summary>
        /// Number of complete blocks currently in the ring.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// Number of samples buffered in the partial-block accumulator.
        /// </summary>
        public int PartialCount
        {
            get
            {
                lock (_lock)
                {
                    return _partialCount;
                }
            }
        }

        /// <summary>
        /// Partial-block accumulator; holds the tail of the speaker channels.
        /// </summary>
        private int _partialCount;

        /// <summary>
        /// Partial-block accumulator for the speaker channels.
        /// </summary>
        private readonly float[] _partialAudio = new float[BlockSamples];

        /// <summary>
        /// Partial-block accumulator for the paired rear haptics channels.
        /// </summary>
        private readonly short[] _partialHaptics = new short[BlockSamples];

        /// <summary>
        /// Whether the current partial block has an authoritative native haptics source.
        /// </summary>
        private bool _partialHapticsPresent;

        /// <summary>
        /// Appends speaker samples and, optionally, their paired native haptics samples.
        /// An empty haptics span records intentional absence; a non-empty span records
        /// presence even when its values are all zero.
        /// </summary>
        public void AppendPartial(ReadOnlySpan<float> audio, ReadOnlySpan<short> haptics)
        {
            lock (_lock)
            {
                audio.CopyTo(_partialAudio.AsSpan(_partialCount, audio.Length));
                if (!haptics.IsEmpty)
                {
                    haptics.CopyTo(_partialHaptics.AsSpan(_partialCount, haptics.Length));
                    _partialHapticsPresent = true;
                }
                else
                {
                    _partialHaptics.AsSpan(_partialCount, audio.Length).Clear();
                }

                _partialCount += audio.Length;
            }
        }

        /// <summary>
        /// Pushes the completed partial block into the ring, dropping the oldest block
        /// when full, and resets the accumulator.
        /// </summary>
        public bool PushPartial()
        {
            lock (_lock)
            {
                if (_partialCount == BlockSamples)
                {
                    _partialAudio.CopyTo(_audioBlocks[_write], 0);
                    _partialHaptics.CopyTo(_hapticsBlocks[_write], 0);
                    _hapticsPresent[_write] = _partialHapticsPresent;
                    bool dropped = AdvanceWrite();
                    _partialCount = 0;
                    _partialHapticsPresent = false;
                    return dropped;
                }

                return false;
            }
        }

        /// <summary>
        /// Pushes one complete block, dropping the oldest block when the ring is full.
        /// </summary>
        public bool Push(ReadOnlySpan<float> audio, ReadOnlySpan<short> haptics)
        {
            lock (_lock)
            {
                audio.CopyTo(_audioBlocks[_write].AsSpan());
                if (haptics.IsEmpty)
                {
                    _hapticsBlocks[_write].AsSpan().Clear();
                    _hapticsPresent[_write] = false;
                }
                else
                {
                    haptics.CopyTo(_hapticsBlocks[_write].AsSpan());
                    _hapticsPresent[_write] = true;
                }

                return AdvanceWrite();
            }
        }

        /// <summary>
        /// Pops the oldest block into the speaker and haptics destinations. Returns
        /// <c>false</c> when the ring is empty; <paramref name="hapticsPresent"/> is
        /// meaningful only when the return value is <c>true</c>.
        /// </summary>
        public bool TryPop(float[] audioDestination, short[] hapticsDestination, out bool hapticsPresent)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    hapticsPresent = false;
                    return false;
                }

                _audioBlocks[_read].CopyTo(audioDestination, 0);
                _hapticsBlocks[_read].CopyTo(hapticsDestination, 0);
                hapticsPresent = _hapticsPresent[_read];
                _read = (_read + 1) % _capacity;
                _count--;
                return true;
            }
        }

        /// <summary>
        /// Pops and discards the oldest block when the caller only needs to catch up
        /// the audio queue. The paired haptics block is discarded atomically with it.
        /// </summary>
        public bool TryPop(float[] audioDestination)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    return false;
                }

                _read = (_read + 1) % _capacity;
                _count--;
                return true;
            }
        }

        /// <summary>
        /// Drops every buffered block and the partial-block accumulator.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _write = 0;
                _read = 0;
                _count = 0;
                _partialCount = 0;
                _partialHapticsPresent = false;
            }
        }

        /// <summary>
        /// Writes one block at the write cursor, dropping the oldest when full. Caller
        /// must hold <see cref="_lock"/>.
        /// </summary>
        private bool AdvanceWrite()
        {
            bool dropped = _count == _capacity;
            if (_count == _capacity)
            {
                _read = (_read + 1) % _capacity;
            }
            else
            {
                _count++;
            }

            _write = (_write + 1) % _capacity;
            return dropped;
        }
    }
}
