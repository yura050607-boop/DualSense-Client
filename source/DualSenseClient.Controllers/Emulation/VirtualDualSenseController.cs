using System.Diagnostics;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.Controllers.DualSense.Triggers;
using DualSenseClient.Hid;
using DualSenseClient.Logging;
using DualSenseClient.Settings.Sections;
using DualSenseClient.VIIPER;
using DualSenseClient.VIIPER.Callbacks;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Controllers.Emulation;

/// <summary>
/// A virtual DualSense controller. The full device (HID gamepad + audio interfaces)
/// is created so games can use the haptics and audio endpoints; the emulation service
/// owns the corresponding host-audio capture and physical-controller forwarder.
/// </summary>
public sealed class VirtualDualSenseController : VirtualControllerBase
{
    /// <summary>
    /// Connection flags that identify the emulated device's USB transport. The
    /// physical source may be Bluetooth, but the virtual controller is always
    /// presented to the host as a USB DualSense.
    /// </summary>
    private const byte VirtualUsbConnectionStatus =
        (byte)(DualSenseConnectionFlags.UsbData | DualSenseConnectionFlags.UsbPower);

    /// <summary>
    /// Audio-jack/microphone flags that may be carried from the physical controller.
    /// Reserved bits are never copied into the virtual report.
    /// </summary>
    private const byte PhysicalAudioConnectionFlags =
        (byte)(DualSenseConnectionFlags.Headphone
               | DualSenseConnectionFlags.Mic
               | DualSenseConnectionFlags.MicMuted);

    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("VirtualDualSense");

    /// <summary>
    /// Keeps the native output state callback delegate alive for the lifetime of the device.
    /// </summary>
    private readonly DSOutputStateCallback _outputStateCallback;

    /// <summary>
    /// Keeps the native realtime-haptics callback delegate alive for the lifetime of the device.
    /// </summary>
    private readonly DSRealtimeHapticsCallback _realtimeHapticsCallback;

    private readonly NativeAudioTimingDiagnostics _timingDiagnostics;
    private readonly Func<nuint, DSDeviceState, bool> _setInputState;
    private readonly Func<nuint, byte[]?, nuint, nuint> _getDeviceTelemetry;
    private byte[]? _postSubmitTelemetryBuffer;
    private bool _ownsDeviceHandle;

    /// <summary>
    /// Raised on the libVIIPER callback thread with normalized game output state.
    /// Normal output callbacks publish after a direct physical write unless the active
    /// Bluetooth audio lane accepts the state; realtime Bluetooth callbacks may publish
    /// without a standalone write. Subscribers must not block.
    /// </summary>
    public event Action<SetStateData>? OutputStateReceived;

    /// <summary>
    /// Raised before a standalone write so the existing Bluetooth audio pump can accept
    /// the latest state for its combined report lane. Return true only when the handler
    /// has retained the state and will deliver it; false preserves the direct-write path.
    /// </summary>
    public event Func<SetStateData, bool>? OutputStateCoalescingRequested;

    /// <summary>
    /// Raised on the libVIIPER callback thread with the game's low-latency rear haptics
    /// payload (the 398-byte combined Bluetooth report), after it was forwarded to the
    /// physical controller. Subscribers must not block.
    /// </summary>
    public event Action<DSOutputState>? RealtimeHapticsReceived;

    /// <summary>
    /// Whether the initial battery/connection meta state has been pushed yet.
    /// </summary>
    private bool _metaInitialized;

    /// <summary>
    /// Whether the physical controller uses the "vibration v2" rumble encoding.
    /// </summary>
    private readonly bool _vibrationV2;

    /// <summary>
    /// Whether the virtual device presents a DualSense Edge instead of the standard DualSense.
    /// </summary>
    private readonly bool _edge;

    /// <summary>
    /// One-shot diagnostic guards for proving which native callback lane received the
    /// host output report during a live bridge run.
    /// </summary>
    private int _outputCallbackDiagnosticLogged;
    private int _realtimeCallbackDiagnosticLogged;

    /// <summary>
    /// Creates and attaches a virtual DualSense device on the given USB bus.
    /// </summary>
    /// <param name="serverHandle">The USB server hosting the device.</param>
    /// <param name="busId">The bus to attach the device to.</param>
    /// <param name="outputs">The physical controller receiving host feedback.</param>
    /// <param name="vibrationV2">True when the physical controller uses the v2 rumble encoding.</param>
    /// <param name="edge">True to create a DualSense Edge instead of the standard DualSense.</param>
    public VirtualDualSenseController(nuint serverHandle, uint busId, IDualSenseOutputs outputs, bool vibrationV2, bool edge = false)
        : this(serverHandle, busId, outputs, vibrationV2, edge, NativeAudioTimingDiagnostics.Current)
    {
    }

    internal VirtualDualSenseController(
        nuint serverHandle,
        uint busId,
        IDualSenseOutputs outputs,
        bool vibrationV2,
        bool edge,
        NativeAudioTimingDiagnostics timingDiagnostics) : base(outputs)
    {
        _timingDiagnostics = timingDiagnostics;
        _setInputState = LibVIIPER.SetDualSenseDeviceState;
        _getDeviceTelemetry = LibVIIPER.GetDeviceTelemetry;
        _vibrationV2 = vibrationV2;
        _edge = edge;
        _outputStateCallback = OnOutputState;
        _realtimeHapticsCallback = OnRealtimeHaptics;
        bool created;
        nuint handle;
        // Stamp the ownership MAC so the app's own scanner can tell this virtual device
        // apart from real hardware (see VirtualDeviceFilter).
        DSMetaState meta = new DSMetaState
        {
            MACAddress = VirtualDeviceFilter.CreateOwnershipMac()
        };
        if (edge)
        {
            created = LibVIIPER.CreateDualSenseEdgeDevice(serverHandle, out handle, busId, true, 0, 0, [meta]);
        }
        else
        {
            created = LibVIIPER.CreateDualSenseDevice(serverHandle, out handle, busId, true, 0, 0, [meta]);
        }

        if (!created)
        {
            _log.Error("Failed to create the virtual DualSense device");
            return;
        }

        DeviceHandle = handle;
        _ownsDeviceHandle = true;
        bool outputCallbackRegistered = LibVIIPER.SetDualSenseOutputStateCallback(handle, _outputStateCallback);
        bool realtimeCallbackRegistered = LibVIIPER.SetDualSenseRealtimeHapticsCallback(handle, _realtimeHapticsCallback);
        if (!outputCallbackRegistered)
        {
            _log.Error($"Failed to register the virtual DualSense output-state callback (handle=0x{handle:X})");
        }

        if (!realtimeCallbackRegistered)
        {
            _log.Error($"Failed to register the virtual DualSense realtime-haptics callback (handle=0x{handle:X})");
        }

        _log.Info($"Virtual DualSense{(edge ? " Edge" : "")} created (handle=0x{handle:X}, outputCallback={outputCallbackRegistered}, realtimeCallback={realtimeCallbackRegistered})");
    }

    internal VirtualDualSenseController(
        IDualSenseOutputs outputs,
        NativeAudioTimingDiagnostics timingDiagnostics,
        Func<nuint, DSDeviceState, bool> setInputState,
        nuint testHandle = 1,
        Func<nuint, byte[]?, nuint, nuint>? getDeviceTelemetry = null) : base(outputs)
    {
        ArgumentNullException.ThrowIfNull(setInputState);
        if (testHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(testHandle));
        }

        _timingDiagnostics = timingDiagnostics;
        _setInputState = setInputState;
        _getDeviceTelemetry = getDeviceTelemetry ?? LibVIIPER.GetDeviceTelemetry;
        _vibrationV2 = false;
        _edge = false;
        _outputStateCallback = OnOutputState;
        _realtimeHapticsCallback = OnRealtimeHaptics;
        _metaInitialized = true;
        DeviceHandle = testHandle;
    }

    /// <summary>
    /// Reads the native aggregate counters for this existing device. Production callers
    /// must hold EmulationService's <c>_sync</c> lifetime lock so teardown cannot release
    /// the borrowed handle during the native call. This is invoked only by the bounded
    /// opt-in diagnostics publisher, never from input forwarding.
    /// </summary>
    internal NativeDeviceInputTelemetry CapturePostSubmitInputTelemetry()
    {
        if (DeviceHandle is not { } handle || handle == 0)
        {
            return new NativeDeviceInputTelemetry(PostSubmitTelemetryStatus.Unavailable);
        }

        try
        {
            _postSubmitTelemetryBuffer ??= new byte[NativePostSubmitTelemetryReader.BufferCapacity];
            return NativePostSubmitTelemetryReader.Read(_getDeviceTelemetry, handle, _postSubmitTelemetryBuffer);
        }
        catch
        {
            // Diagnostics must not alter controller input, output, or teardown behavior.
            return new NativeDeviceInputTelemetry(PostSubmitTelemetryStatus.NativeError);
        }
    }

    /// <inheritdoc/>
    public override EmulationMode Mode
    {
        get
        {
            return EmulationMode.DualSense;
        }
    }

    /// <summary>
    /// Translates physical input to the virtual DualSense input state and pushes it.
    /// </summary>
    public override void PushInput(InputReport report)
    {
        _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualPushCalled);
        if (DeviceHandle is not { } handle)
        {
            _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualPushNoHandle);
            return;
        }

        InputState input = report.Input;
        MappedInputResult mapped = (ButtonMappings ?? VirtualInputMapper.DualSenseDefaultTable).Evaluate(input);
        DSDeviceState state = new DSDeviceState
        {
            LX = VirtualInputMapper.DualSenseStick(input.LeftStickX),
            LY = VirtualInputMapper.DualSenseStick(input.LeftStickY),
            RX = VirtualInputMapper.DualSenseStick(input.RightStickX),
            RY = VirtualInputMapper.DualSenseStick(input.RightStickY),
            Buttons = (uint)mapped.Buttons,
            DPad = (byte)mapped.DPad,
            L2 = mapped.LeftTrigger,
            R2 = mapped.RightTrigger
        };

        TouchpadState touchpad = report.Touchpad;
        state.Touch1X = touchpad.Touch1.X;
        state.Touch1Y = touchpad.Touch1.Y;
        state.Touch1Active = touchpad.Touch1.IsActive ? (byte)1 : (byte)0;
        state.Touch1Tracking = touchpad.Touch1.TrackingId;
        state.Touch2X = touchpad.Touch2.X;
        state.Touch2Y = touchpad.Touch2.Y;
        state.Touch2Active = touchpad.Touch2.IsActive ? (byte)1 : (byte)0;
        state.Touch2Tracking = touchpad.Touch2.TrackingId;

        MotionState motion = report.Motion;
        state.GyroX = motion.GyroX;
        state.GyroY = motion.GyroY;
        state.GyroZ = motion.GyroZ;
        state.AccelX = motion.AccelX;
        state.AccelY = motion.AccelY;
        state.AccelZ = motion.AccelZ;

        _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualSubmitAttempted);
        bool submitted;
        try
        {
            submitted = _setInputState(handle, state);
        }
        catch
        {
            _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualSubmitFailed);
            throw;
        }

        if (!submitted)
        {
            _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualSubmitFailed);
            _log.Error("Failed to set the virtual DualSense device state");
        }
        else
        {
            _timingDiagnostics.RecordInputProgress(InputProgressMetric.VirtualSubmitSucceeded);
        }

        EnsureInitialMeta(report);
    }

    /// <inheritdoc/>
    public override void PushBattery(BatteryState battery) => PushMeta(battery, null);

    /// <inheritdoc/>
    public override void PushConnectionStatus(ConnectionStatus status) => PushMeta(null, status);

    /// <summary>
    /// Maps physical connection metadata onto the virtual USB presentation. VIIPER's
    /// input contract requires USB data/power bits for the virtual device even when
    /// the source report arrived over Bluetooth; only the physical audio flags are
    /// meaningful to carry across, and reserved bits must not leak into byte 54.
    /// </summary>
    internal static byte NormalizeConnectionStatusForVirtualUsb(ConnectionStatus status) =>
        (byte)(VirtualUsbConnectionStatus
               | (status.Raw & PhysicalAudioConnectionFlags));

    /// <summary>
    /// Pushes battery and/or connection meta state; zero-valued fields keep their
    /// current values on the device side.
    /// </summary>
    private void PushMeta(BatteryState? battery, ConnectionStatus? status)
    {
        if (DeviceHandle is not { } handle)
        {
            return;
        }

        DSMetaState meta = new DSMetaState();
        if (battery is { } b)
        {
            meta.BatteryStatus = b.Raw;
        }

        if (status is { } c)
        {
            meta.ConnectionStatus = NormalizeConnectionStatusForVirtualUsb(c);
        }

        if (!LibVIIPER.SetDualSenseMetaState(handle, new[]
            {
                meta
            }))
        {
            _log.Error("Failed to set the virtual DualSense meta state");
        }
    }

    /// <summary>
    /// Pushes the battery/connection meta from the first received report so the
    /// virtual device reports real values from the start.
    /// </summary>
    private void EnsureInitialMeta(InputReport report)
    {
        if (_metaInitialized)
        {
            return;
        }

        _metaInitialized = true;
        PushMeta(report.Battery, report.Connection);
    }

    /// <summary>
    /// Forwards host output (rumble, lightbar, player LEDs, triggers) to the physical
    /// controller. Invoked on the libVIIPER callback thread.
    /// </summary>
    private void OnOutputState(nuint handle, DSOutputState output)
    {
        NativeAudioTimingDiagnostics diagnostics = _timingDiagnostics.CaptureSession();
        bool measure = diagnostics.IsEnabled;
        long startedTimestamp = measure ? Stopwatch.GetTimestamp() : 0;
        try
        {
            (SetStateData payload, bool standaloneForwarded) = ForwardOutputState(
                output,
                _vibrationV2,
                Outputs,
                OutputStateReceived,
                diagnostics,
                OutputStateCoalescingRequested);
            if (!diagnostics.IsOptedIn)
            {
                LogCallbackDiagnostic("output-state", output, payload, standaloneForwarded,
                    ref _outputCallbackDiagnosticLogged);
            }
        }
        finally
        {
            if (measure)
            {
                diagnostics.RecordDurationFromTimestamp(
                    AudioTimingMetric.NativeOutputStateCallback,
                    startedTimestamp);
            }
        }
    }

    /// <summary>
    /// Forwards the host state to the physical controller. Active Bluetooth combined
    /// reports can accept the newest state without waiting for the physical writer;
    /// all other paths retain the direct write and publication order.
    /// </summary>
    internal static (SetStateData Payload, bool StandaloneForwarded) ForwardOutputState(DSOutputState output, bool vibrationV2,
        IDualSenseOutputs outputs, Action<SetStateData>? stateReceived,
        NativeAudioTimingDiagnostics? timingDiagnostics = null,
        Func<SetStateData, bool>? coalesceState = null)
    {
        NativeAudioTimingDiagnostics diagnostics = (timingDiagnostics ?? NativeAudioTimingDiagnostics.Current).CaptureSession();
        SetStateData payload = BuildOutputPayload(output, vibrationV2);
        if (coalesceState?.Invoke(payload) == true)
        {
            return (payload, false);
        }

        bool measure = diagnostics.IsEnabled;
        long startedTimestamp = measure ? Stopwatch.GetTimestamp() : 0;
        try
        {
            outputs.SendOutputState(payload);
        }
        finally
        {
            if (measure)
            {
                diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.ControlOutputWrite, startedTimestamp);
            }
        }

        stateReceived?.Invoke(payload);
        return (payload, true);
    }

    /// <summary>
    /// Rebuilds the physical controller's output payload from the game's exact USB
    /// output report (the 47 bytes following report ID 0x02), so every feature the game
    /// addresses on the virtual controller — adaptive triggers, rumble, lightbar,
    /// player LEDs, mute LED and its validity bits, volumes, audio control, motor power
    /// reduction, haptic low-pass filter, brightness/fade — arrives 1:1 over USB or
    /// Bluetooth. Two adjustments are applied:
    /// <list type="bullet">
    /// <item>The motor bytes always carry libVIIPER's retained decoded values, so
    /// partial reports that do not mention rumble still publish the last requested
    /// magnitudes to subscribers (the audio forwarder's rumble synthesis relies on
    /// this). The pad-side selector bits below decide whether they are applied.</item>
    /// <item>The rumble-mode selector bits are translated between the encodings:
    /// games select v1 (flag 0 bit 0) or v2 (flag 2 bit 2) against the virtual
    /// device's firmware; the physical pad may require the other encoding. When the
    /// game did not touch rumble at all, all selector bits are cleared so the pad
    /// retains its motors — mirroring real hardware.</item>
    /// </list>
    /// The trigger FFB allow bits pass through exactly as the game wrote them: with a
    /// bit set the pad applies the block riding the report; with a bit clear it retains
    /// its effect — the same semantics a real DualSense connected directly to the game
    /// would exhibit.
    /// </summary>
    public static SetStateData BuildOutputPayload(DSOutputState output, bool vibrationV2)
    {
        byte[] raw = output.RawOutputReport;
        if (raw is not { Length: 48 } || raw[0] != 0x02)
        {
            return BuildFallbackPayload(output, vibrationV2);
        }

        byte[] bytes = new byte[SetStateData.PayloadSize];
        Buffer.BlockCopy(raw, 1, bytes, 0, SetStateData.PayloadSize);

        // Retained magnitudes for subscribers; gated pad-side by the selector bits.
        bytes[2] = output.RumbleSmall;
        bytes[3] = output.RumbleLarge;

        SetStateData payload = new SetStateData(bytes, 0);
        bool rumbleSelected = (payload.ValidFlag0 & ValidFlags.EnableRumbleEmulation) != 0
                              || (payload.ValidFlag2 & ValidFlags.EnableImprovedRumbleEmu) != 0;
        ValidFlags flag0 = payload.ValidFlag0;
        ValidFlags flag2 = payload.ValidFlag2;
        if (rumbleSelected)
        {
            flag0 |= ValidFlags.UseRumbleNotHaptics;
            if (vibrationV2)
            {
                flag0 &= ~ValidFlags.EnableRumbleEmulation;
                flag2 |= ValidFlags.EnableImprovedRumbleEmu;
            }
            else
            {
                flag0 |= ValidFlags.EnableRumbleEmulation;
                flag2 &= ~ValidFlags.EnableImprovedRumbleEmu;
            }
        }
        else
        {
            flag0 &= ~(ValidFlags.UseRumbleNotHaptics | ValidFlags.EnableRumbleEmulation);
            flag2 &= ~ValidFlags.EnableImprovedRumbleEmu;
        }

        return payload with
        {
            ValidFlag0 = flag0,
            ValidFlag2 = flag2
        };
    }

    /// <summary>
    /// Defensive fallback for callbacks whose raw report is missing or malformed:
    /// reconstructs the previously supported subset from the decoded fields.
    /// </summary>
    private static SetStateData BuildFallbackPayload(DSOutputState output, bool vibrationV2)
    {
        return new SetStateData
        {
            ValidFlag0 = ValidFlags.UseRumbleNotHaptics
                         | (vibrationV2 ? ValidFlags.None : ValidFlags.EnableRumbleEmulation)
                         | ValidFlags.AllowRightTriggerFfb
                         | ValidFlags.AllowLeftTriggerFfb,
            ValidFlag2 = (vibrationV2 ? ValidFlags.EnableImprovedRumbleEmu : ValidFlags.None)
                         | ValidFlags.AllowBrightnessChange
                         | ValidFlags.AllowColorFadeAnim,
            ValidFlag1 = ValidFlags.AllowLedColor | ValidFlags.AllowPlayerIndicators | ValidFlags.AllowMuteLight,
            RumbleLeft = output.RumbleLarge,
            RumbleRight = output.RumbleSmall,
            MuteLedMode = output.MicLed,
            LightFadeAnimation = output.LightbarSetup,
            LightBrightness = output.LightbarBrightness,
            PlayerLeds = (PlayerLedMask)output.PlayerLeds,
            LedRed = output.LedRed,
            LedGreen = output.LedGreen,
            LedBlue = output.LedBlue,
            R2TriggerEffect = BuildDecodedTriggerEffect(
                output.TriggerR2Mode,
                output.TriggerR2StartResistance,
                output.TriggerR2EffectForce,
                output.TriggerR2RangeForce,
                output.TriggerR2NearReleaseStrength,
                output.TriggerR2NearMiddleStrength,
                output.TriggerR2PressedStrength,
                output.TriggerR2Frequency),
            L2TriggerEffect = BuildDecodedTriggerEffect(
                output.TriggerL2Mode,
                output.TriggerL2StartResistance,
                output.TriggerL2EffectForce,
                output.TriggerL2RangeForce,
                output.TriggerL2NearReleaseStrength,
                output.TriggerL2NearMiddleStrength,
                output.TriggerL2PressedStrength,
                output.TriggerL2Frequency)
        };
    }

    /// <summary>
    /// Reconstructs a trigger block from libVIIPER's decoded fields when a native
    /// callback cannot provide its raw 48-byte report. The native decoder exposes the
    /// decoded fields in their native wire order: mode, six effect parameters,
    /// two reserved bytes, frequency, and the trailing padding byte. Keeping the
    /// reserved positions intact matters because frequency is byte 9 of each 11-byte
    /// USB trigger block, not the next field after the decoded effect parameters.
    /// </summary>
    private static TriggerEffectBlock BuildDecodedTriggerEffect(byte mode,
        byte startResistance, byte effectForce, byte rangeForce,
        byte nearReleaseStrength, byte nearMiddleStrength,
        byte pressedStrength, byte frequency)
    {
        byte[] raw = new byte[11]
        {
            mode,
            startResistance,
            effectForce,
            rangeForce,
            nearReleaseStrength,
            nearMiddleStrength,
            pressedStrength,
            0,
            0,
            frequency,
            0
        };
        return new TriggerEffectBlock(raw, 0);
    }

    /// <summary>
    /// Forwards the game's low-latency rear haptics payload to subscribers. Invoked on
    /// the libVIIPER callback thread.
    /// </summary>
    private void OnRealtimeHaptics(nuint handle, DSOutputState output)
    {
        // A live Bluetooth audio stream owns the pad's standalone 0x31 output lane.
        // The native realtime callback still carries the latest raw game output report;
        // publish that state to the forwarder so it rides the stream's 0x36 carrier.
        // Wired output has no combined Bluetooth carrier, so keep a standalone fallback
        // for that transport.
        NativeAudioTimingDiagnostics diagnostics = _timingDiagnostics.CaptureSession();
        bool measure = diagnostics.IsEnabled;
        long startedTimestamp = measure ? Stopwatch.GetTimestamp() : 0;
        try
        {
            (SetStateData payload, bool standaloneForwarded) = ForwardRealtimeOutput(
                output,
                _vibrationV2,
                Outputs,
                OutputStateReceived,
                diagnostics,
                OutputStateCoalescingRequested);
            RealtimeHapticsReceived?.Invoke(output);
            if (!diagnostics.IsOptedIn)
            {
                LogCallbackDiagnostic("realtime-haptics", output, payload, standaloneForwarded,
                    ref _realtimeCallbackDiagnosticLogged);
            }
        }
        finally
        {
            if (measure)
            {
                diagnostics.RecordDurationFromTimestamp(
                    AudioTimingMetric.NativeRealtimeHapticsCallback,
                    startedTimestamp);
            }
        }
    }

    /// <summary>
    /// Routes the native realtime callback's latest output snapshot through the same
    /// state path as a normal output callback. Bluetooth retains its existing rule of
    /// not issuing standalone writes from this high-rate callback; when a combined report
    /// is already queued it accepts the latest state there, and otherwise it only
    /// publishes the snapshot. USB still writes synchronously before subscribers are
    /// notified. This seam keeps the callback contract executable in regression tests
    /// without constructing a native USB/IP device.
    /// </summary>
    internal static (SetStateData Payload, bool StandaloneForwarded) ForwardRealtimeOutput(DSOutputState output, bool vibrationV2,
        IDualSenseOutputs outputs, Action<SetStateData>? stateReceived,
        NativeAudioTimingDiagnostics? timingDiagnostics = null,
        Func<SetStateData, bool>? coalesceState = null)
    {
        NativeAudioTimingDiagnostics diagnostics = (timingDiagnostics ?? NativeAudioTimingDiagnostics.Current).CaptureSession();
        SetStateData payload = BuildOutputPayload(output, vibrationV2);
        if (coalesceState?.Invoke(payload) == true)
        {
            return (payload, false);
        }

        if (outputs is IDualSenseAudioOutputs audioOutputs
            && audioOutputs.ConnectionType == ConnectionType.Bluetooth)
        {
            stateReceived?.Invoke(payload);
            return (payload, false);
        }

        bool measure = diagnostics.IsEnabled;
        long startedTimestamp = measure ? Stopwatch.GetTimestamp() : 0;
        try
        {
            outputs.SendOutputState(payload);
        }
        finally
        {
            if (measure)
            {
                diagnostics.RecordDurationFromTimestamp(AudioTimingMetric.ControlOutputWrite, startedTimestamp);
            }
        }

        stateReceived?.Invoke(payload);
        return (payload, true);
    }

    /// <summary>
    /// Emits a bounded callback diagnostic containing only report shape and validity
    /// metadata. The first line is enough to distinguish a missing native callback from
    /// a bad payload without logging controller output at the callback rate.
    /// </summary>
    private void LogCallbackDiagnostic(string callbackName, DSOutputState output,
        SetStateData payload, bool standaloneForwarded, ref int logged)
    {
        if (Interlocked.Exchange(ref logged, 1) != 0)
        {
            return;
        }

        byte[]? raw = output.RawOutputReport;
        int rawLength = raw?.Length ?? 0;
        byte reportId = raw is { Length: > 0 } ? raw[0] : (byte)0;
        _log.Info($"First {callbackName} callback: rawLength={rawLength}, reportId=0x{reportId:X2}, "
                  + $"valid0=0x{(byte)payload.ValidFlag0:X2}, valid2=0x{(byte)payload.ValidFlag2:X2}, "
                  + $"r2Mode=0x{(byte)payload.R2TriggerEffect.Mode:X2}, l2Mode=0x{(byte)payload.L2TriggerEffect.Mode:X2}, "
                  + $"standaloneForwarded={standaloneForwarded}");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (DeviceHandle is not { } handle)
        {
            return;
        }

        if (!_ownsDeviceHandle)
        {
            DeviceHandle = null;
            return;
        }

        _log.Info("Removing virtual DualSense device");
        LibVIIPER.SetDualSenseOutputStateCallback(handle, null);
        LibVIIPER.SetDualSenseRealtimeHapticsCallback(handle, null);
        if (!LibVIIPER.RemoveDualSenseDevice(handle))
        {
            _log.Error("The native library failed to remove the virtual DualSense device");
        }

        DeviceHandle = null;
    }
}
