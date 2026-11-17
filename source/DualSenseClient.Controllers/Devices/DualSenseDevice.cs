using DualSenseClient.Controllers.DualSense.Events;
using DualSenseClient.Controllers.DualSense.Feature;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Hid;
using DualSenseClient.Logging;
using DualSenseClient.Settings.Sections;
using DualSenseClient.Controllers.DualSense.Enum;
using DualSenseClient.Controllers.DualSense.Triggers;
using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.Controllers.Devices;

/// <summary>
/// Concrete controller implementation for the Sony DualSense (PS5) controller.
/// Opens and communicates with the DualSense over USB or Bluetooth via SDL3 HID.
/// </summary>
public class DualSenseDevice : ControllerDevice, IDualSenseMicrophoneSource
{
    /// <summary>
    /// Largest Bluetooth input report accepted by the optional microphone stream.
    /// Normal controller reports remain 78 bytes.
    /// </summary>
    private const int BluetoothInputReportBufferSize = BluetoothMicFrameDecoder.MaxReportBytes;

    // Fields
    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("DualSenseDevice");

    /// <summary>
    /// Cancellation token source used to stop the background read loop.
    /// </summary>
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    /// <summary>
    /// The dedicated background thread running the read loop. A real thread (rather
    /// than <c>Task.Run</c>) keeps per-read overhead off the ThreadPool: reads are
    /// long-running blocking calls arriving 250-1000 times per second.
    /// </summary>
    private readonly Thread _readThread;

    /// <summary>
    /// Stable process router for optional aggregate timing and input-progress capture.
    /// It resolves the active manual session for each record, so existing devices can
    /// join a capture started later and do not retain a closed session.
    /// </summary>
    private readonly NativeAudioTimingDiagnostics _timingDiagnostics;

    /// <summary>
    /// Previous input report snapshot for change detection. Null until the first
    /// report is received, so no events fire on the initial read.
    /// </summary>
    private InputReport? _previousInputReport;

    /// <summary>
    /// Bluetooth output report sequence tag; only the low nibble is transmitted.
    /// </summary>
    private byte _outputSequence;

    /// <summary>
    /// Serializes output-state sends and guards the Bluetooth dedupe state.
    /// </summary>
    private readonly Lock _outputSync = new Lock();

    /// <summary>
    /// Stateful decoder for the optional Bluetooth built-in microphone lane.
    /// </summary>
    private readonly BluetoothMicFrameDecoder _microphoneDecoder = new BluetoothMicFrameDecoder();

    /// <summary>
    /// Disabled-by-default aggregate telemetry for the physical microphone path.
    /// </summary>
    private readonly BluetoothMicrophoneDiagnostics _microphoneDiagnostics =
        BluetoothMicrophoneDiagnostics.CreateFromEnvironment();

    /// <summary>
    /// Reused 10 ms stereo S16LE frame delivered synchronously to the virtual sink.
    /// Subscribers must consume it before returning and must not retain it.
    /// </summary>
    private readonly byte[] _microphonePcmFrame = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];

    /// <summary>
    /// Latest physical mute state reported by the controller.
    /// </summary>
    private volatile bool _microphoneMuted;

    /// <summary>
    /// The last payload written over Bluetooth, used to skip duplicate reports: games
    /// tend to rewrite an unchanged state every frame, and each suppressed report
    /// frees interrupt-channel bandwidth for input (lower latency) on Bluetooth.
    /// </summary>
    private readonly byte[] _lastBluetoothPayload = new byte[SetStateData.PayloadSize];

    /// <summary>
    /// Whether <see cref="_lastBluetoothPayload"/> holds a successfully sent payload.
    /// </summary>
    private bool _hasLastBluetoothPayload;

    /// <summary>
    /// Builds the Bluetooth-only audio (<c>0x35</c>) and haptics (<c>0x32</c>) reports.
    /// The rolling sequence and packet counters live here, so the builder must only be
    /// touched while the per-device output writer lock is held. The same lock also
    /// guards normal output reports, preventing direct Audio Player/special-action
    /// paths from racing the emulation adapter.
    /// </summary>
    private readonly BluetoothAudioReportBuilder _bluetoothAudioReports = new BluetoothAudioReportBuilder();

    /// <summary>
    /// 1 once <see cref="Dispose"/> has run, guarding against double disposal.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Set when the dedicated input thread exits, whether from a fault or normal
    /// disposal. The scanner uses the public health state to close the constructor
    /// subscription race.
    /// </summary>
    private int _readLoopEnded;

    /// <summary>
    /// Set only while the scanner's current path entry owns this DualSense wrapper.
    /// Physical progress counts are application-qualified, not proof of absolute
    /// hardware provenance.
    /// </summary>
    private int _scannerOwnedPhysicalDiagnostics;

    /// <inheritdoc/>
    public override ControllerType ControllerType
    {
        get
        {
            return ControllerType.DualSense;
        }
    }

    /// <summary>
    /// Whether this controller is a DualSense Edge, which has the extra Fn
    /// buttons and back paddles. Base DualSense hardware returns <c>false</c>;
    /// <see cref="DualSenseEdgeDevice"/> overrides this.
    /// </summary>
    public virtual bool IsEdge
    {
        get
        {
            return false;
        }
    }

    /// <summary>
    /// Enables physical input progress counts only after scanner registration accepts
    /// this exact wrapper, and disables them before ownership is retired.
    /// </summary>
    internal void SetScannerPhysicalDiagnosticsOwnership(bool owned) =>
        Volatile.Write(ref _scannerOwnedPhysicalDiagnostics, owned ? 1 : 0);

    private void RecordPhysicalInputProgress(InputProgressMetric metric)
    {
        if (Volatile.Read(ref _scannerOwnedPhysicalDiagnostics) != 0)
        {
            _timingDiagnostics.RecordInputProgress(metric);
        }
    }

    /// <summary>
    /// Whether this controller uses the "vibration v2" rumble encoding: firmware update
    /// version >= 2.21 for a base DualSense, always for a DualSense Edge. Mirrors the
    /// kernel's <c>dualsense_use_vibration_v2</c> gate. When <c>false</c>, the v1
    /// encoding (flag 0 bit 0) is used instead.
    /// </summary>
    public bool UsesVibrationV2
    {
        get
        {
            return IsEdge ||
                   (FirmwareInfo?.IsValid == true && FirmwareInfo.Value.UpdateVersionValue >= ((2 << 8) | 21));
        }
    }

    /// <inheritdoc/>
    public override int MaxOutputReportLength
    {
        get
        {
            return ConnectionType switch
            {
                ConnectionType.Bluetooth => 78,
                ConnectionType.Usb => 63,
                ConnectionType.Unknown => throw new ArgumentOutOfRangeException($"Unknown connection type: {ConnectionType}"),
                _ => throw new ArgumentOutOfRangeException($"Unknown connection type: {ConnectionType}")
            };
        }
    }

    /// <summary>
    /// Firmware and hardware information read from feature report 0x20 on connect.
    /// Null when the report could not be read.
    /// </summary>
    public FirmwareInfo? FirmwareInfo { get; private set; }

    /// <summary>
    /// Pairing information read from feature report 0x09 on connect.
    /// Null when the report could not be read.
    /// </summary>
    public PairingInfo? PairingInfo { get; private set; }

    /// <inheritdoc/>
    protected override string? BluetoothMacAddress
    {
        get
        {
            return PairingInfo?.ClientMac;
        }
    }

    /// <summary>
    /// Current state of input, or null before the first report is received.
    /// </summary>
    public InputReport? InputReport { get; private set; }

    /// <summary>
    /// Raised when any input state field changes (sticks, triggers, or buttons).
    /// Fires once per report regardless of how many individual fields changed.
    /// </summary>
    public event EventHandler<InputStateEventArgs>? InputStateChanged;

    /// <summary>
    /// Raised once for every received input report, including the first one and
    /// reports in which nothing changed. Carries the parsed report so consumers
    /// always see the latest motion, touchpad, battery, and connection data even
    /// when the button/stick bytes are unchanged. Fires on the read thread.
    /// </summary>
    public event EventHandler<InputReport>? InputReportReceived;

    /// <summary>
    /// Raised once when the blocking input reader ends without being cancelled by
    /// <see cref="Dispose"/>. Runs on the reader thread; subscribers must return
    /// quickly and must not synchronously join or dispose this device.
    /// </summary>
    public event EventHandler? InputReaderEndedUnexpectedly;

    /// <summary>
    /// Whether the input reader is still alive and has not been disposed.
    /// </summary>
    public bool IsInputReaderHealthy => Volatile.Read(ref _readLoopEnded) == 0
        && Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public override bool IsConnected => IsInputReaderHealthy && base.IsConnected;

    /// <summary>
    /// Raised synchronously on the physical HID read thread for each validated
    /// Bluetooth microphone PCM frame. The byte array is reused for the next frame;
    /// subscribers must consume it before returning and must not retain it.
    /// </summary>
    public event Action<byte[]>? MicrophonePcmReceived;

    /// <summary>
    /// Raised when the physical microphone mute state changes.
    /// </summary>
    public event Action<bool>? MicrophoneMuteChanged;

    /// <summary>
    /// Whether the physical controller currently reports its microphone muted.
    /// </summary>
    public bool MicrophoneMuted
    {
        get
        {
            return _microphoneMuted;
        }
    }

    /// <summary>
    /// Aggregate physical microphone diagnostics. The seam is disabled unless
    /// <c>DUALSENSECLIENT_MIC_DIAGNOSTICS_SECONDS</c> was set before process start.
    /// </summary>
    public BluetoothMicrophoneDiagnostics MicrophoneDiagnostics
    {
        get
        {
            return _microphoneDiagnostics;
        }
    }

    /// <summary>
    /// Raised when a button transitions from released to pressed.
    /// </summary>
    public event EventHandler<ButtonEventArgs>? ButtonPressed;

    /// <summary>
    /// Raised when a button transitions from pressed to released.
    /// </summary>
    public event EventHandler<ButtonEventArgs>? ButtonReleased;

    /// <summary>
    /// Raised when either analog stick changes position.
    /// </summary>
    public event EventHandler<StickEventArgs>? StickMoved;

    /// <summary>
    /// Raised when either analog trigger changes value.
    /// </summary>
    public event EventHandler<TriggerEventArgs>? TriggerMoved;

    /// <summary>
    /// Raised when battery level or power state changes.
    /// </summary>
    public event EventHandler<BatteryStateEventArgs>? BatteryStateChanged;

    /// <summary>
    /// Raised when headphone, mic, or USB connection status changes.
    /// </summary>
    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

    /// <summary>
    /// Raised when gyroscope or accelerometer data changes.
    /// </summary>
    public event EventHandler<MotionEventArgs>? MotionChanged;

    /// <summary>
    /// Raised when touchpad touch state changes.
    /// </summary>
    public event EventHandler<TouchpadEventArgs>? TouchpadChanged;

    /// <summary>
    /// Raised when the lightbar color is (re)sent to the controller via an output
    /// report carrying the LED color flag. Fires on the sender's thread.
    /// </summary>
    public event EventHandler? LightbarColorChanged;

    /// <summary>
    /// Raised when the player LED layout is (re)sent to the controller via an output
    /// report carrying the player indicator flag. Fires on the sender's thread.
    /// </summary>
    public event EventHandler? PlayerLedsChanged;

    /// <summary>
    /// Raised when the mute LED mode is (re)sent to the controller via an output report
    /// carrying the mute light flag. Fires on the sender's thread.
    /// </summary>
    public event EventHandler? MuteLedModeChanged;

    /// <summary>
    /// The last lightbar color sent to the controller, or the controller's default
    /// PS-blue (0, 87, 255) before any color output report has been sent.
    /// </summary>
    public (byte Red, byte Green, byte Blue) CurrentLightbarColor { get; private set; } = (0, 87, 255);

    /// <summary>
    /// The last player LED layout sent to the controller (bits 0-4, LED 1 = leftmost),
    /// or 0 before any player indicator output report has been sent.
    /// </summary>
    public byte CurrentPlayerLeds { get; private set; }

    /// <summary>
    /// The last mute LED mode sent to the controller (0 = off, 1 = on, 2 = pulse),
    /// or 0 before any mute light output report has been sent.
    /// </summary>
    public byte CurrentMuteLedMode { get; private set; }

    /// <summary>
    /// Creates a new DualSense controller wrapper around an already-opened HID device.
    /// Profiles are not applied here; the owning application applies a profile later via
    /// <see cref="ApplyProfile"/> once the device is connected.
    /// </summary>
    /// <param name="device">The opened HID device for this controller.</param>
    /// <param name="info">The device info that was used to discover and open the device.</param>
    public DualSenseDevice(IHidDevice device, IHidDeviceInfo info)
        : this(device, info, NativeAudioTimingDiagnostics.Current)
    {
    }

    internal DualSenseDevice(
        IHidDevice device,
        IHidDeviceInfo info,
        NativeAudioTimingDiagnostics timingDiagnostics) : base(device, info)
    {
        _timingDiagnostics = timingDiagnostics;
        if (ConnectionType == ConnectionType.Bluetooth)
        {
            // A DualSense starts Bluetooth connections in its 10-byte simplified
            // report mode. Reading calibration feature 0x05 is the controller's
            // enhanced-report handshake; do it before the input reader starts so
            // motion, touch, status, and the full output-capable mode are available
            // from the first steady-state report.
            FeatureReader.PrimeBluetoothInputMode(this);
        }

        FirmwareInfo = FeatureReader.ReadFirmwareInfo(this);
        PairingInfo = FeatureReader.ReadPairingInfo(this);
        _readThread = new Thread(() => ReadLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "DualSense Input Reader",
            Priority = ThreadPriority.AboveNormal
        };
        _readThread.Start();
    }

    /// <summary>
    /// Background loop that continuously reads HID input reports from the controller.
    /// Runs on a dedicated background thread for the lifetime of the controller
    /// connection, blocking on each read instead of yielding to the ThreadPool.
    /// </summary>
    /// <param name="ct">Cancellation token to signal when the loop should stop.</param>
    private void ReadLoop(CancellationToken ct)
    {
        _log.Debug("Read Loop Start");
        byte[] buffer = new byte[ConnectionType == ConnectionType.Bluetooth
            ? BluetoothInputReportBufferSize
            : MaxOutputReportLength];
        bool endedUnexpectedly = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool readCompleted = false;
                try
                {
                    int result = ReadInput(buffer, 0, buffer.Length, -1);
                    readCompleted = true;

                    // This production reader deliberately uses SDL's blocking timeout
                    // (-1). SDL documents zero as the no-packet result for a finite timed
                    // read; that quiet-interval case is not used here. A zero from this
                    // blocking read therefore means the HID stream ended, while a
                    // cancellation during Dispose is a normal lifecycle stop.
                    if (result <= 0)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            RecordPhysicalInputProgress(InputProgressMetric.PhysicalReadEnded);
                            _log.Warning($"Blocking read returned {result} bytes, disconnecting");
                            endedUnexpectedly = true;
                        }

                        break;
                    }

                    RecordPhysicalInputProgress(InputProgressMetric.PhysicalReadSucceeded);
                    ProcessInputReportCore(buffer, result);
                }
                catch (HidException ex)
                {
                    // A failed read is the normal symptom of the controller being
                    // unplugged or the link dropping, so log it as a disconnect
                    // rather than an error.
                    if (!readCompleted && !ct.IsCancellationRequested)
                    {
                        RecordPhysicalInputProgress(InputProgressMetric.PhysicalReadFailed);
                    }

                    _log.Warning($"Read failed, controller disconnected: {ex.Message}");
                    endedUnexpectedly = !ct.IsCancellationRequested;
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogExceptionDetails(ex);
                    if (!readCompleted && !ct.IsCancellationRequested)
                    {
                        RecordPhysicalInputProgress(InputProgressMetric.PhysicalReadFailed);
                    }

                    endedUnexpectedly = !ct.IsCancellationRequested;
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readLoopEnded, 1);
            _log.Debug("Read Loop End");

            if (endedUnexpectedly
                && !ct.IsCancellationRequested
                && Volatile.Read(ref _disposed) == 0)
            {
                RaiseInputReaderEndedUnexpectedly();
            }
        }
    }

    /// <summary>
    /// Notifies every reader-lifecycle subscriber without allowing one callback to
    /// suppress cleanup notifications for the remaining subscribers.
    /// </summary>
    private void RaiseInputReaderEndedUnexpectedly()
    {
        EventHandler? handlers = InputReaderEndedUnexpectedly;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                _log.Warning("An input-reader lifecycle subscriber failed");
            }
        }
    }

    /// <summary>
    /// Routes a raw HID report to the correct parser based on connection type and report ID.
    /// Strips the protocol header bytes before forwarding to the input parser.
    /// </summary>
    /// <param name="data">Raw HID input report buffer.</param>
    private void ProcessInputReport(byte[] data) => ProcessInputReportCore(data, data.Length);

    /// <summary>
    /// Parses one raw HID report using only the bytes returned by the transport.
    /// Bluetooth microphone candidates are classified from the maintained 0x31
    /// header lane bit before normal state parsing, including malformed/truncated
    /// candidates, so Opus bytes cannot become phantom gamepad input.
    /// </summary>
    /// <param name="data">Raw HID buffer.</param>
    /// <param name="length">Number of bytes returned by the transport.</param>
    private void ProcessInputReportCore(byte[] data, int length)
    {
        if (data is null || length <= 0)
        {
            return;
        }

        length = Math.Min(length, data.Length);
        ReadOnlySpan<byte> reportBytes = data.AsSpan(0, length);
        _microphoneDiagnostics.ObserveInputReport(length);
        if (ConnectionType == ConnectionType.Bluetooth
            && BluetoothMicFrameDecoder.LooksLikeMicCandidate(reportBytes))
        {
            // A candidate is consumed even when validation fails. This is an
            // intentional safety boundary for truncated/malformed mic reports.
            // Do not advance the stateful Opus decoder while the physical mute bit is
            // asserted. A muted report must not leak PCM and the next unmuted packet
            // must remain the first packet decoded for the resumed capture generation.
            bool hasEnvelope = BluetoothMicFrameDecoder.HasMicEnvelope(reportBytes);
            _microphoneDiagnostics.ObserveMicrophoneCandidate(length, hasEnvelope);
            if (hasEnvelope)
            {
                _microphoneDiagnostics.ObserveOpusPacketMetadata(
                    reportBytes[3],
                    reportBytes.Length - 3 - 4);
            }

            if (_microphoneMuted)
            {
                _microphoneDiagnostics.ObserveMutedFrame();
                return;
            }

            long decodeStarted = _microphoneDiagnostics.Timestamp();
            bool decoded = _microphoneDecoder.TryDecode(
                reportBytes, _microphonePcmFrame, out int decodedSamples);
            _microphoneDiagnostics.ObserveDecodeResult(
                _microphonePcmFrame,
                decoded,
                _microphoneDiagnostics.Timestamp() - decodeStarted,
                decodedSamples);
            try
            {
                if (decoded)
                {
                    MicrophonePcmReceived?.Invoke(_microphonePcmFrame);
                }
            }
            finally
            {
                if (decoded)
                {
                    _microphoneDiagnostics.ObservePipelineDuration(
                        _microphoneDiagnostics.Timestamp() - decodeStarted);
                }
            }

            return;
        }

        byte reportId = reportBytes[0];
        int offset;
        if (ConnectionType == ConnectionType.Bluetooth)
        {
            switch (reportId)
            {
                case 0x31:
                    offset = 2;
                    break;
                case 0x01:
                    RecordPhysicalInputProgress(InputProgressMetric.PhysicalCompactReport);
                    _log.Warning("Controller is in simple Bluetooth state");
                    // Older DualSense firmware starts the link with the compact 0x01
                    // report and only promotes it to the full 0x31 report after it sees
                    // an output transaction. Keep the first wake report side-effect free
                    // (all validity flags clear); the next input report then carries the
                    // complete sticks, triggers, touch, motion, and status payload.
                    try
                    {
                        SendOutputState(new SetStateData());
                    }
                    catch (HidException ex)
                    {
                        // A link can disappear between the simple report and this wake
                        // write. The read loop will observe the disconnect normally; do
                        // not turn it into an unhandled callback-thread exception.
                        _log.Warning($"Failed to wake full Bluetooth input mode: {ex.Message}");
                    }
                    return;
                default:
                    _log.Warning($"Unknown Bluetooth report ID: 0x{reportId:X2}");
                    return;
            }
        }
        else
        {
            if (reportId != 0x01)
            {
                _log.Warning($"Invalid USB report ID: 0x{reportId:X2} (expected 0x01)");
                return;
            }

            offset = 1;
        }

        InputReport report;
        try
        {
            report = new InputReport(data, offset);
        }
        catch
        {
            RecordPhysicalInputProgress(InputProgressMetric.PhysicalParseFailed);
            throw;
        }

        RecordPhysicalInputProgress(InputProgressMetric.PhysicalFullReportParsed);
        _microphoneDiagnostics.ObserveNormalReport();
        bool microphoneMuted = report.Connection.MicMuted;
        if (_microphoneMuted != microphoneMuted)
        {
            _microphoneMuted = microphoneMuted;
            _microphoneDecoder.Reset();
            _microphoneDiagnostics.ObserveDecoderReset();
            _microphoneDiagnostics.ObserveMuteChange(microphoneMuted);
            MicrophoneMuteChanged?.Invoke(microphoneMuted);
        }
        _log.Trace($"Input report 0x{reportId:X2} ({length} byte(s)): {BitConverter.ToString(data, 0, length)}");
        if (_previousInputReport is { } prev)
        {
            if (DetectChanges(prev, report))
            {
                RecordPhysicalInputProgress(InputProgressMetric.PhysicalControlStateChanged);
            }
        }

        _previousInputReport = report;
        InputReport = report;
        TrackPollingRate();
        InputReportReceived?.Invoke(this, report);
    }

    /// <summary>
    /// Sends output state (rumble, lightbar, player LEDs, trigger effects) to the
    /// controller, framed for the active transport (USB report ID 0x02 or Bluetooth
    /// report ID 0x31 with a rolling sequence tag and CRC32). Over Bluetooth a payload
    /// byte-identical to the previously sent one is skipped: the controller retains its
    /// output state, so resending unchanged state only consumes interrupt-channel
    /// bandwidth.
    /// </summary>
    /// <param name="payload">The output state to send.</param>
    public void SendOutputState(SetStateData payload)
    {
        if ((payload.ValidFlag1 & ValidFlags.AllowLedColor) != 0
            && (payload.LedRed, payload.LedGreen, payload.LedBlue) != CurrentLightbarColor)
        {
            CurrentLightbarColor = (payload.LedRed, payload.LedGreen, payload.LedBlue);
            LightbarColorChanged?.Invoke(this, EventArgs.Empty);
        }

        if ((payload.ValidFlag1 & ValidFlags.AllowPlayerIndicators) != 0)
        {
            byte leds = (byte)((byte)payload.PlayerLeds & (byte)PlayerLedMask.All);
            if (leds != CurrentPlayerLeds)
            {
                CurrentPlayerLeds = leds;
                PlayerLedsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        if ((payload.ValidFlag1 & ValidFlags.AllowMuteLight) != 0)
        {
            byte mode = (byte)(payload.MuteLedMode & 0x03);
            if (mode != CurrentMuteLedMode)
            {
                CurrentMuteLedMode = mode;
                MuteLedModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        lock (_outputSync)
        {
            bool bluetooth = ConnectionType == ConnectionType.Bluetooth;
            if (!bluetooth || !IsDuplicateBluetoothPayload(payload))
            {
                OutputReport report = bluetooth
                    ? OutputReport.ForBluetooth(payload, (byte)(_outputSequence & 0x0F))
                    : OutputReport.ForUsb(payload);
                _outputSequence = (byte)((_outputSequence + 1) & 0x0F);

                _log.Debug($"Sending output report 0x{report.Raw[0]:X2} ({report.Length} byte(s))");
                SendOutput(report.Raw, 0, report.Length);

                // Recorded only after a successful write so a failed send is retried
                // on the next identical payload instead of being deduped away.
                if (bluetooth)
                {
                    RememberBluetoothPayload(payload);
                }
            }
            else
            {
                _log.Trace("Skipping duplicate Bluetooth output report");
            }
        }
    }

    /// <summary>
    /// Reports whether the given payload is byte-identical to the previously sent
    /// Bluetooth payload. Caller must hold <see cref="_outputSync"/>.
    /// </summary>
    private bool IsDuplicateBluetoothPayload(SetStateData payload) => _hasLastBluetoothPayload && payload.AsSpan().SequenceEqual(_lastBluetoothPayload);

    /// <summary>
    /// Stores the given payload as the last successfully sent Bluetooth payload.
    /// Caller must hold <see cref="_outputSync"/>.
    /// </summary>
    private void RememberBluetoothPayload(SetStateData payload)
    {
        payload.CopyTo(_lastBluetoothPayload, 0);
        _hasLastBluetoothPayload = true;
    }

    /// <summary>
    /// Applies the given profile (lightbar color, microphone LED mode, and player LEDs)
    /// to the controller immediately.
    /// </summary>
    /// <param name="profile">The profile to apply.</param>
    public void ApplyProfile(Profile profile)
    {
        SetStateData payload = new SetStateData
        {
            // The RGB bytes are gated by ValidFlag1.AllowLedColor, but taking over the
            // lightbar from the controller's default (BT-connect blue) additionally requires
            // ValidFlag2.AllowColorFadeAnim plus the lightbar-setup byte (payload offset 41)
            // written to 0x02 ("light out"). This mirrors the hid-playstation driver.
            ValidFlag1 = ValidFlags.AllowMuteLight | ValidFlags.AllowLedColor | ValidFlags.AllowPlayerIndicators,
            ValidFlag2 = ValidFlags.AllowColorFadeAnim,
            MuteLedMode = profile.MicLed.Mode,
            LightFadeAnimation = 0x02,
            PlayerLeds = (PlayerLedMask)profile.PlayerLeds.Mask,
            LedRed = profile.Lightbar.Red,
            LedGreen = profile.Lightbar.Green,
            LedBlue = profile.Lightbar.Blue
        };

        _log.Debug(
            $"Applying profile: RGB({profile.Lightbar.Red}, {profile.Lightbar.Green}, {profile.Lightbar.Blue}), mic LED {profile.MicLed.Mode}, player LEDs {profile.PlayerLeds.Mask}");

        try
        {
            SendOutputState(payload);
        }
        catch (HidException ex)
        {
            _log.Error($"Failed to apply profile '{profile.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the classic (DS4-style) rumble motors. Sends a minimal output report carrying
    /// only the rumble flags and motor values, so no other controller state (lightbar,
    /// trigger effects, audio) is disturbed.
    /// </summary>
    /// <remarks>
    /// Follows the hid-playstation driver's encoding: HAPTICS_SELECT is always set, and
    /// either flag 0 bit 0 (v1) or flag 2 bit 2 (v2) selects the rumble encoding
    /// depending on <see cref="UsesVibrationV2"/>. A strength of 0 turns that motor off.
    /// </remarks>
    /// <param name="left">Left (low-frequency) motor strength (0-255).</param>
    /// <param name="right">Right (high-frequency) motor strength (0-255).</param>
    public void SetVibration(byte left, byte right)
    {
        SetStateData payload = new SetStateData
        {
            ValidFlag0 = ValidFlags.UseRumbleNotHaptics
                         | (UsesVibrationV2 ? ValidFlags.None : ValidFlags.EnableRumbleEmulation),
            ValidFlag2 = UsesVibrationV2 ? ValidFlags.EnableImprovedRumbleEmu : ValidFlags.None,
            RumbleLeft = left,
            RumbleRight = right
        };

        try
        {
            SendOutputState(payload);
        }
        catch (HidException ex)
        {
            _log.Error($"Failed to set vibration (left={left}, right={right}): {ex.Message}");
        }
    }

    /// <summary>
    /// Applies adaptive trigger effects to both triggers. Pass <see cref="TriggerEffectBuilder.Off"/>
    /// to clear an effect.
    /// </summary>
    /// <param name="left">Effect block for the L2 (left) trigger.</param>
    /// <param name="right">Effect block for the R2 (right) trigger.</param>
    public void SetTriggerEffects(TriggerEffectBlock left, TriggerEffectBlock right)
    {
        SetStateData payload = new SetStateData
        {
            ValidFlag0 = ValidFlags.AllowLeftTriggerFfb | ValidFlags.AllowRightTriggerFfb,
            L2TriggerEffect = left,
            R2TriggerEffect = right
        };

        try
        {
            SendOutputState(payload);
        }
        catch (HidException ex)
        {
            _log.Error($"Failed to set trigger effects: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns off the rumble motors and clears the adaptive trigger effects.
    /// </summary>
    public void ResetOutputs()
    {
        SetVibration(0, 0);
        SetTriggerEffects(TriggerEffectBuilder.Off(), TriggerEffectBuilder.Off());
    }

    /// <summary>
    /// Restarts the Bluetooth audio/haptics report sequence and packet counters.
    /// Call this before starting a stream.
    /// </summary>
    public void ResetBluetoothAudioStream()
    {
        lock (_outputSync)
        {
            _bluetoothAudioReports.Reset();
        }
    }

    /// <summary>
    /// Selects whether the next Bluetooth audio/haptics session requests the
    /// controller's built-in microphone duplex lane. The audio forwarder owns the
    /// cadence; this method only updates the serialized report builder state.
    /// </summary>
    public void SetBluetoothMicrophoneEnabled(bool enabled)
    {
        lock (_outputSync)
        {
            _bluetoothAudioReports.SetMicrophoneEnabled(enabled);
        }
    }

    /// <summary>
    /// Sends the report <c>0x32</c> init-prime that opens the Bluetooth audio/haptics
    /// stream. The stream must be primed before sending audio or haptics reports.
    /// </summary>
    /// <param name="state">The output state embedded in the prime (routing/volume).</param>
    public void SendBluetoothAudioPrime(SetStateData state)
    {
        EnsureBluetoothAudio();
        lock (_outputSync)
        {
            TrySendAudioReport(_bluetoothAudioReports.BuildInitPrime(state));
        }
    }

    /// <summary>
    /// Sends a report <c>0x32</c> voice-coil haptics frame (64 bytes of s8 stereo PCM).
    /// </summary>
    /// <param name="hapticsPcm">64 bytes of interleaved s8 stereo material.</param>
    public void SendBluetoothHaptics(ReadOnlySpan<byte> hapticsPcm)
    {
        EnsureBluetoothAudio();
        lock (_outputSync)
        {
            TrySendAudioReport(_bluetoothAudioReports.BuildHapticsReport(hapticsPcm));
        }
    }

    /// <summary>
    /// Sends a combined report <c>0x36</c> carrying the output state, one 200-byte Opus
    /// frame and the 64-byte voice-coil haptics frame in a single report. Audio and
    /// haptics must share one report per tick; interleaving a separate <c>0x32</c>
    /// haptics report with the <c>0x35</c> audio lane breaks the controller's audio
    /// stream.
    /// </summary>
    /// <param name="state">The 47-byte output state embedded in the report.</param>
    /// <param name="opusFrame">A 200-byte Opus frame.</param>
    /// <param name="hapticsPcm">64 bytes of interleaved s8 stereo material.</param>
    /// <param name="route">Which output the audio frame targets.</param>
    public void SendBluetoothAudioAndHaptics(SetStateData state, ReadOnlySpan<byte> opusFrame, ReadOnlySpan<byte> hapticsPcm, BluetoothAudioRoute route)
    {
        EnsureBluetoothAudio();
        lock (_outputSync)
        {
            TrySendAudioReport(_bluetoothAudioReports.BuildCombinedReport(state, opusFrame, hapticsPcm, route));
        }
    }

    /// <summary>
    /// Sends a report <c>0x35</c> speaker/headset audio frame (one 200-byte Opus frame).
    /// </summary>
    /// <param name="opusFrame">A 200-byte Opus frame.</param>
    /// <param name="route">Which output the frame targets.</param>
    public void SendBluetoothAudio(ReadOnlySpan<byte> opusFrame, BluetoothAudioRoute route)
    {
        EnsureBluetoothAudio();
        lock (_outputSync)
        {
            TrySendAudioReport(_bluetoothAudioReports.BuildAudioReport(opusFrame, route));
        }
    }

    /// <summary>
    /// Routes audio output and applies speaker/headphone volume over either transport.
    /// Uses the output-state report (USB <c>0x02</c> / Bluetooth <c>0x31</c>), which is
    /// the authoritative switch for which destination is actually driven.
    /// </summary>
    /// <param name="outputControl">Output path selection: speaker, headphones, or both.</param>
    /// <param name="speakerVolume">Speaker volume (0-255; the controller accepts roughly 0x3D-0x64).</param>
    /// <param name="headphoneVolume">Headphone volume (0-255).</param>
    /// <param name="preampGain">Speaker preamp gain, bits [2:0] of audio_control2 (<c>0x2</c> = +6 dB).</param>
    public void SetAudioOutput(AudioControl outputControl, byte speakerVolume, byte headphoneVolume, byte preampGain = 0x02)
    {
        SetStateData payload = new SetStateData
        {
            ValidFlag0 = ValidFlags.AllowSpeakerVolume | ValidFlags.AllowHeadphoneVolume | ValidFlags.AllowAudioControl,
            ValidFlag1 = ValidFlags.AllowAudioControl2,
            SpeakerVolume = speakerVolume,
            HeadphoneVolume = headphoneVolume,
            AudioControl = outputControl,
            AudioControl2 = preampGain
        };

        try
        {
            SendOutputState(payload);
        }
        catch (HidException ex)
        {
            _log.Error($"Failed to set audio output: {ex.Message}");
        }
    }

    /// <summary>
    /// Throws if the current transport cannot carry the <c>0x35</c>/<c>0x32</c> reports.
    /// </summary>
    private void EnsureBluetoothAudio()
    {
        if (ConnectionType != ConnectionType.Bluetooth)
        {
            throw new InvalidOperationException("The 0x35/0x32 audio reports require a Bluetooth connection.");
        }
    }

    /// <summary>
    /// Writes an audio/haptics report, logging rather than throwing on HID failures so a
    /// dropped link stops the stream instead of crashing the writer thread.
    /// </summary>
    private void TrySendAudioReport(byte[] report)
    {
        try
        {
            if (DualSenseClientLogger.MinimumLevel <= LogLevel.Trace)
            {
                _log.Trace($"Sending audio report 0x{report[0]:X2} ({report.Length} byte(s)): {BitConverter.ToString(report, 0, report.Length)}");
            }

            SendOutput(report, 0, report.Length);
        }
        catch (HidException ex)
        {
            _log.Error($"Failed to send audio report 0x{report[0]:X2}: {ex.Message}");
        }
    }

    /// <summary>
    /// Compares the current and previous input reports and fires events for
    /// any detected changes (buttons, sticks, triggers, battery, connection,
    /// motion, touchpad).
    /// </summary>
    /// <param name="prev">Previous report snapshot.</param>
    /// <param name="cur">Current report.</param>
    private bool DetectChanges(InputReport prev, InputReport cur)
    {
        bool controlStateChanged = false;

        // Button press/release
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Cross, static s => s.Cross);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Circle, static s => s.Circle);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Square, static s => s.Square);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Triangle, static s => s.Triangle);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.DPadUp, static s => s.DPadUp);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.DPadDown, static s => s.DPadDown);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.DPadLeft, static s => s.DPadLeft);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.DPadRight, static s => s.DPadRight);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.L1, static s => s.L1);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.R1, static s => s.R1);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.L2, static s => s.L2Click);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.R2, static s => s.R2Click);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.L3, static s => s.L3);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.R3, static s => s.R3);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Create, static s => s.Create);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Options, static s => s.Options);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.PS, static s => s.PS);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.TouchPad, static s => s.TouchPad);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Mute, static s => s.Mute);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Edge_LeftFunction, static s => s.EdgeFunctionLeft);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Edge_RightFunction, static s => s.EdgeFunctionRight);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Edge_LeftPaddle, static s => s.EdgePaddleLeft);
        controlStateChanged |= CheckButton(prev.Input, cur.Input, ButtonType.Edge_RightPaddle, static s => s.EdgePaddleRight);

        // Stick movement
        if (prev.Input.LeftStickX != cur.Input.LeftStickX || prev.Input.LeftStickY != cur.Input.LeftStickY)
        {
            controlStateChanged = true;
            _log.Trace($"Left stick moved to ({cur.Input.LeftStickX}, {cur.Input.LeftStickY})");
            StickMoved?.Invoke(this,
                new StickEventArgs(StickType.Left, cur.Input.LeftStickX, cur.Input.LeftStickY, prev.Input.LeftStickX, prev.Input.LeftStickY));
        }

        if (prev.Input.RightStickX != cur.Input.RightStickX || prev.Input.RightStickY != cur.Input.RightStickY)
        {
            controlStateChanged = true;
            _log.Trace($"Right stick moved to ({cur.Input.RightStickX}, {cur.Input.RightStickY})");
            StickMoved?.Invoke(this,
                new StickEventArgs(StickType.Right, cur.Input.RightStickX, cur.Input.RightStickY, prev.Input.RightStickX, prev.Input.RightStickY));
        }

        // Trigger movement
        if (prev.Input.L2 != cur.Input.L2)
        {
            controlStateChanged = true;
            _log.Trace($"L2 trigger moved to {cur.Input.L2}");
            TriggerMoved?.Invoke(this, new TriggerEventArgs(TriggerType.L2, cur.Input.L2, prev.Input.L2));
        }

        if (prev.Input.R2 != cur.Input.R2)
        {
            controlStateChanged = true;
            _log.Trace($"R2 trigger moved to {cur.Input.R2}");
            TriggerMoved?.Invoke(this, new TriggerEventArgs(TriggerType.R2, cur.Input.R2, prev.Input.R2));
        }

        // Full-state events (invoke on any change)
        if (prev.Battery != cur.Battery)
        {
            _log.Trace($"Battery changed from {prev.Battery.DisplayPercentage}% to {cur.Battery.DisplayPercentage}% (power state: {cur.Battery.PowerState})");
            BatteryStateChanged?.Invoke(this, new BatteryStateEventArgs(cur.Battery, prev.Battery));
        }

        if (prev.Connection != cur.Connection)
        {
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(cur.Connection, prev.Connection));
        }

        if (prev.Input != cur.Input)
        {
            InputStateChanged?.Invoke(this, new InputStateEventArgs(cur.Input, prev.Input));
        }

        if (prev.Motion != cur.Motion)
        {
            MotionChanged?.Invoke(this, new MotionEventArgs(cur.Motion, prev.Motion));
        }

        if (prev.Touchpad != cur.Touchpad)
        {
            TouchpadChanged?.Invoke(this, new TouchpadEventArgs(cur.Touchpad, prev.Touchpad));
        }

        return controlStateChanged;
    }

    /// <summary>
    /// Fires ButtonPressed or ButtonReleased based on the transition detected by
    /// the selector predicate.
    /// </summary>
    private bool CheckButton(InputState prev, InputState cur, ButtonType button, Func<InputState, bool> selector)
    {
        bool wasPressed = selector(prev);
        bool isPressed = selector(cur);
        if (wasPressed == isPressed)
        {
            return false;
        }

        if (isPressed)
        {
            _log.Trace($"{button} pressed");
            ButtonPressed?.Invoke(this, new ButtonEventArgs(button));
        }
        else
        {
            _log.Trace($"{button} released");
            ButtonReleased?.Invoke(this, new ButtonEventArgs(button));
        }

        return true;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SetScannerPhysicalDiagnosticsOwnership(false);
        _cts.Cancel();
        base.Dispose();

        if (!_readThread.Join(TimeSpan.FromSeconds(2)))
        {
            _log.Warning("Read loop did not stop cleanly on dispose");
        }

        _cts.Dispose();
        _microphoneDecoder.Dispose();
        _microphoneDiagnostics.LogFinal();
    }
}
