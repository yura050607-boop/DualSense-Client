using System.Diagnostics;

using DualSenseClient.Controllers.Devices;
using DualSenseClient.Logging;
using DualSenseClient.VIIPER;

namespace DualSenseClient.Controllers.DualSense.Audio;

/// <summary>
/// Source of ephemeral, already-decoded 10 ms stereo microphone frames. The source
/// owns the backing array and invokes subscribers synchronously; subscribers must not
/// retain or mutate it.
/// </summary>
public interface IDualSenseMicrophoneSource
{
    /// <summary>
    /// Raised on the physical HID read thread for each validated frame.
    /// </summary>
    event Action<byte[]>? MicrophonePcmReceived;

    /// <summary>
    /// Raised when the physical mute state changes.
    /// </summary>
    event Action<bool>? MicrophoneMuteChanged;

    /// <summary>
    /// Current physical mute state.
    /// </summary>
    bool MicrophoneMuted { get; }
}

/// <summary>
/// Forwards the physical DualSense's decoded Bluetooth microphone frames into the
/// existing libVIIPER virtual DualSense microphone queue. It is a synchronous,
/// bounded handoff: there is no speech backlog or second audio clock.
/// </summary>
public sealed class ViiperDualSenseMicrophoneForwarder : IDisposable
{
    private static readonly DualSenseClientLogger _log =
        DualSenseClientLogger.For("ViiperDualSenseMicrophone");

    /// <summary>
    /// Number of 10 ms silence frames needed to displace the maintained VIIPER
    /// DualSense microphone queue's 20-frame emergency ceiling. The native API only
    /// queues frames and exposes no flush operation, so lifecycle barriers use this
    /// bounded tail instead of claiming that one zero frame clears queued speech.
    /// </summary>
    internal const int SilenceTailFrames = 20;

    private readonly IDualSenseMicrophoneSource _source;
    private readonly Func<byte[], bool> _submit;
    private readonly BluetoothMicrophoneDiagnostics _diagnostics;
    private readonly Lock _sync = new Lock();
    private readonly byte[] _silence = new byte[BluetoothMicFrameDecoder.PcmFrameBytes];
    private bool _active;
    private bool _failed;
    private long _generation;
    private Subscription? _subscription;

    /// <summary>
    /// Per-start event delegates. A callback captured from an older start cannot
    /// match the current subscription even if it runs after Stop has unsubscribed
    /// and a later Start has attached a new pair of handlers.
    /// </summary>
    private sealed class Subscription
    {
        public Subscription(ViiperDualSenseMicrophoneForwarder owner, long generation)
        {
            Generation = generation;
            PcmHandler = frame => owner.OnPcm(this, frame);
            MuteHandler = muted => owner.OnMuteChanged(this, muted);
        }

        public long Generation { get; }
        public Action<byte[]> PcmHandler { get; }
        public Action<bool> MuteHandler { get; }
    }

    /// <summary>
    /// Creates a production forwarder targeting one virtual DualSense handle.
    /// </summary>
    public ViiperDualSenseMicrophoneForwarder(DualSenseDevice source, nuint virtualDeviceHandle)
        : this(source, frame => LibVIIPER.SetDualSenseMicrophonePCM(
            virtualDeviceHandle, frame, (nuint)frame.Length), source.MicrophoneDiagnostics)
    {
    }

    /// <summary>
    /// Internal constructor used by deterministic lifecycle tests.
    /// </summary>
    internal ViiperDualSenseMicrophoneForwarder(
        IDualSenseMicrophoneSource source,
        Func<byte[], bool> submit,
        BluetoothMicrophoneDiagnostics? diagnostics = null)
    {
        _source = source;
        _submit = submit;
        _diagnostics = diagnostics ?? new BluetoothMicrophoneDiagnostics(
            false, Stopwatch.GetTimestamp, Stopwatch.Frequency, TimeSpan.Zero);
    }

    /// <summary>
    /// Whether this forwarder is actively subscribed to the physical source.
    /// </summary>
    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// Subscribes to the physical source and seeds the virtual endpoint with silence.
    /// </summary>
    public bool Start()
    {
        Subscription? failedSubscription = null;
        lock (_sync)
        {
            if (_active)
            {
                return !_failed;
            }

            Subscription subscription = new Subscription(this, ++_generation);
            _subscription = subscription;
            _failed = false;
            _active = true;
            _diagnostics.ObserveSessionStart();
            _source.MicrophonePcmReceived += subscription.PcmHandler;
            _source.MicrophoneMuteChanged += subscription.MuteHandler;
            if (!TrySubmitLocked(_silence))
            {
                _active = false;
                _subscription = null;
                _generation++;
                failedSubscription = subscription;
            }
        }

        // A failed initial native handoff must not leave callbacks attached to a
        // dead virtual endpoint. Unsubscribe outside the state lock so a source
        // whose event accessors perform their own synchronization cannot deadlock.
        if (failedSubscription is not null)
        {
            Unsubscribe(failedSubscription);
        }

        lock (_sync)
        {
            return _active;
        }
    }

    /// <summary>
    /// Stops the subscription and submits a bounded silence tail sized to the
    /// virtual capture queue. A late source callback observes the inactive
    /// generation and is discarded.
    /// </summary>
    public void Stop()
    {
        bool shouldSilence;
        Subscription? subscription;
        lock (_sync)
        {
            shouldSilence = _active || _failed;
            _active = false;
            subscription = _subscription;
            _subscription = null;
            _generation++;
            if (shouldSilence)
            {
                // Keep the silence tail in the stopped generation. A later Start
                // must not have its first frame clobbered by a stale Stop call.
                TrySubmitSilenceTailLocked();
            }
            _diagnostics.ObserveSessionStop();
        }

        _diagnostics.LogFinal();

        if (subscription is not null)
        {
            Unsubscribe(subscription);
        }

    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    private void OnPcm(Subscription subscription, byte[] frame)
    {
        bool unsubscribe = false;
        lock (_sync)
        {
            if (!_active || _failed || !ReferenceEquals(_subscription, subscription)
                || subscription.Generation != _generation || _source.MicrophoneMuted)
            {
                return;
            }

            if (frame is null || frame.Length != BluetoothMicFrameDecoder.PcmFrameBytes)
            {
                unsubscribe = !TrySubmitSilenceTailLocked();
                if (unsubscribe)
                {
                    _failed = true;
                    _active = false;
                    _subscription = null;
                    _generation++;
                }
            }
            else if (!TrySubmitLocked(frame))
            {
                _active = false;
                _subscription = null;
                _generation++;
                unsubscribe = true;
            }
        }

        if (unsubscribe)
        {
            Unsubscribe(subscription);
        }
    }

    private void OnMuteChanged(Subscription subscription, bool muted)
    {
        if (!muted)
        {
            return;
        }

        bool unsubscribe = false;
        lock (_sync)
        {
            if (_active && ReferenceEquals(_subscription, subscription)
                && subscription.Generation == _generation)
            {
                if (!TrySubmitSilenceTailLocked())
                {
                    _failed = true;
                    _active = false;
                    _subscription = null;
                    _generation++;
                    unsubscribe = true;
                }
            }
        }

        if (unsubscribe)
        {
            Unsubscribe(subscription);
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        _source.MicrophonePcmReceived -= subscription.PcmHandler;
        _source.MicrophoneMuteChanged -= subscription.MuteHandler;
    }

    private bool TrySubmitLocked(byte[] frame)
    {
        bool success = TrySubmitOnceLocked(frame, true);

        if (!success)
        {
            _failed = true;
            // The bounded zero tail is best-effort: on a removed virtual device the
            // native queue may already be gone, but no user PCM is retained here.
            if (!ReferenceEquals(frame, _silence))
            {
                TrySubmitSilenceTailLocked();
            }
        }

        return success;
    }

    private bool TrySubmitOnceLocked(byte[] frame, bool logWarning)
    {
        bool success;
        long started = _diagnostics.Timestamp();
        try
        {
            success = _submit(frame);
        }
        catch (Exception ex)
        {
            success = false;
            if (logWarning)
            {
                _log.Warning($"Virtual microphone submission failed: {ex.Message}");
            }
            else
            {
                _log.Debug($"Virtual microphone silence submission failed: {ex.Message}");
            }
        }

        _diagnostics.ObserveSinkSubmission(
            frame,
            success,
            ReferenceEquals(frame, _silence),
            _diagnostics.Timestamp() - started);
        return success;
    }

    private bool TrySubmitSilenceTailLocked()
    {
        for (int frame = 0; frame < SilenceTailFrames; frame++)
        {
            if (!TrySubmitOnceLocked(_silence, false))
            {
                return false;
            }
        }

        return true;
    }

}
