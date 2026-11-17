using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DualSenseClient.Controllers.DualSense.Audio;

internal enum AudioTimingMetric
{
    NativeAudioCallback,
    NativeOutputStateCallback,
    NativeRealtimeHapticsCallback,
    ForwarderOutputLockWait,
    ForwarderCompute,
    BluetoothAudioWrite,
    ControlOutputWrite,
    AudioConfigurationWrite,
    PumpLateness,
    Count
}

internal enum InputProgressMetric
{
    PhysicalReadSucceeded,
    PhysicalReadEnded,
    PhysicalReadFailed,
    PhysicalCompactReport,
    PhysicalFullReportParsed,
    PhysicalParseFailed,
    PhysicalControlStateChanged,
    EmulationCallback,
    EmulationNoTarget,
    EmulationForwarded,
    VirtualPushCalled,
    VirtualPushNoHandle,
    VirtualSubmitAttempted,
    VirtualSubmitSucceeded,
    VirtualSubmitFailed,
    Count
}

internal readonly record struct AudioTimingMetricSnapshot(
    long Samples,
    long TotalStopwatchTicks,
    long MaxStopwatchTicks,
    long OverBudgetSamples);

internal readonly record struct AudioTimingQueueSnapshot(
    long Samples,
    long MaxDepth,
    long DroppedBlocks);

internal readonly record struct NativeAudioTimingOptions(
    string LogPath,
    string BuildIdentity,
    string SessionIdentity,
    int DurationSeconds);

/// <summary>
/// Process-local, opt-in aggregate timing for the native DualSense audio path and
/// aggregate progress across physical input parsing and virtual input submission.
/// Callback writes use only fixed atomic counters; the bounded text log is owned
/// by one background publisher and contains no audio, input values, or device identity.
/// </summary>
internal sealed class NativeAudioTimingDiagnostics : IDisposable
{
    internal const string Prefix = "[NATIVE-AUDIO-TIMING-34]";
    internal const int MaximumDurationSeconds = 1800;
    internal const int SummaryIntervalSeconds = 5;
    internal const int MaximumSummaryLines = 400;
    internal const int MaximumLogBytes = 1024 * 1024;
    internal const long MaximumOwnedLogBytes = 16L * 1024 * 1024;

    internal const string EnabledEnvironmentName = "DUALSENSECLIENT_AUDIO_TIMING_ENABLED";
    internal const string DurationEnvironmentName = "DUALSENSECLIENT_AUDIO_TIMING_DURATION_SECONDS";
    internal const string LogPathEnvironmentName = "DUALSENSECLIENT_AUDIO_TIMING_LOG_PATH";
    internal const string BuildIdentityEnvironmentName = "DUALSENSECLIENT_AUDIO_TIMING_BUILD";
    internal const string SessionIdentityEnvironmentName = "DUALSENSECLIENT_AUDIO_TIMING_SESSION";
    private static readonly long OverBudgetTicks = Stopwatch.Frequency / 100;
    private static readonly string[] MetricNames =
    [
        "native_audio_callback",
        "native_output_callback",
        "native_haptics_callback",
        "forwarder_output_lock_wait",
        "forwarder_compute",
        "bluetooth_audio_write",
        "control_output_write",
        "audio_configuration_write",
        "pump_lateness"
    ];
    private static readonly string[] InputProgressMetricNames =
    [
        "physical_read_ok",
        "physical_read_end",
        "physical_read_error",
        "physical_compact_reports",
        "physical_full_parsed",
        "physical_parse_error",
        "physical_control_changes",
        "emulation_callback",
        "emulation_no_target",
        "emulation_forwarded",
        "virtual_push_calls",
        "virtual_push_no_handle",
        "virtual_submit_attempts",
        "virtual_submit_success",
        "virtual_submit_error"
    ];

    private static readonly Lazy<NativeAudioTimingDiagnosticsManager> ProcessManager = new(
        NativeAudioTimingDiagnosticsManager.CreateProcessManager,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<NativeAudioTimingDiagnostics> ProcessRouter = new(
        () => new NativeAudioTimingDiagnostics(ProcessManager.Value),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly NativeAudioTimingDiagnosticsManager? _routerManager;
    private readonly NativeAudioTimingDiagnosticsManager? _sessionTelemetryManager;
    private readonly MetricCounter[]? _metrics;
    private readonly long[]? _inputProgress;
    private readonly StreamWriter? _writer;
    private readonly Action<string>? _testSink;
    private readonly CancellationTokenSource? _stopSignal;
    private readonly object _postSubmitTelemetrySync = new();
    private readonly bool _testClock;
    private readonly string _buildIdentity;
    private readonly string _sessionIdentity;
    private readonly long _startedTimestamp;
    private readonly long _deadlineTimestamp;
    private readonly int _durationSeconds;
    private readonly string? _logPath;
    private Task? _publisherTask;
    private long _queueSamples;
    private long _queueMaxDepth;
    private long _queueDroppedBlocks;
    private PostSubmitTelemetrySnapshot _lastPostSubmitTelemetry = PostSubmitTelemetrySnapshot.NotSampled;
    private long _writtenBytes;
    private int _writtenLines;
    private int _active;
    private int _finalWritten;
    private int _writerClosed;
    private string? _stopReason;

    private NativeAudioTimingDiagnostics(NativeAudioTimingDiagnosticsManager routerManager)
    {
        _routerManager = routerManager;
        _buildIdentity = string.Empty;
        _sessionIdentity = string.Empty;
        _startedTimestamp = 0;
        _deadlineTimestamp = 0;
        _durationSeconds = 0;
    }

    private NativeAudioTimingDiagnostics()
    {
        _buildIdentity = string.Empty;
        _sessionIdentity = string.Empty;
        _startedTimestamp = 0;
        _deadlineTimestamp = 0;
        _durationSeconds = 0;
    }

    private NativeAudioTimingDiagnostics(
        string buildIdentity,
        string sessionIdentity,
        int durationSeconds,
        long startedTimestamp,
        StreamWriter? writer,
        Action<string>? testSink,
        CancellationTokenSource? stopSignal,
        bool testClock,
        string? logPath = null,
        NativeAudioTimingDiagnosticsManager? sessionTelemetryManager = null)
    {
        _buildIdentity = buildIdentity;
        _sessionIdentity = sessionIdentity;
        _durationSeconds = durationSeconds;
        _logPath = logPath;
        _startedTimestamp = startedTimestamp;
        _deadlineTimestamp = startedTimestamp + (long)(durationSeconds * (double)Stopwatch.Frequency);
        _writer = writer;
        _testSink = testSink;
        _stopSignal = stopSignal;
        _testClock = testClock;
        _sessionTelemetryManager = sessionTelemetryManager;
        _metrics = new MetricCounter[(int)AudioTimingMetric.Count];
        for (int i = 0; i < _metrics.Length; i++)
        {
            _metrics[i] = new MetricCounter();
        }

        _inputProgress = new long[(int)InputProgressMetric.Count];

        _active = 1;
    }

    private static NativeAudioTimingDiagnostics Disabled { get; } = new();

    internal static NativeAudioTimingDiagnostics Current => ProcessRouter.Value;

    internal static NativeAudioTimingDiagnosticsManager ProcessControl => ProcessManager.Value;

    internal IDisposable? RegisterPostSubmitTelemetrySource(Func<PostSubmitTelemetrySnapshot> capture) =>
        _routerManager?.RegisterPostSubmitTelemetrySource(capture);

    internal NativeAudioTimingDiagnostics CaptureSession() =>
        _routerManager is null ? this : _routerManager.CaptureSession();

    internal bool ShouldSuppressAudioContentDiagnostics => _routerManager is not null
        ? _routerManager.ShouldSuppressAudioContentDiagnostics
        : IsOptedIn;

    internal bool IsEnabled
    {
        get
        {
            if (_routerManager is not null)
            {
                return _routerManager.CaptureSession().IsEnabled;
            }

            if (Volatile.Read(ref _active) == 0)
            {
                return false;
            }

            return _testClock || Stopwatch.GetTimestamp() < _deadlineTimestamp;
        }
    }

    internal bool IsOptedIn => _routerManager is not null
        ? _routerManager.CaptureSession().IsOptedIn
        : _metrics is not null && IsEnabled;

    internal static bool TryParseOptions(
        string? enabled,
        string? durationSeconds,
        string? logPath,
        string? buildIdentity,
        string? sessionIdentity,
        string baseDirectory,
        out NativeAudioTimingOptions options)
    {
        options = default;
        if (!string.Equals(enabled, "1", StringComparison.Ordinal))
        {
            return false;
        }

        int duration = MaximumDurationSeconds;
        if (!string.IsNullOrWhiteSpace(durationSeconds)
            && (!int.TryParse(durationSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out duration)
                || duration is < 1 or > MaximumDurationSeconds))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(logPath)
            || string.IsNullOrWhiteSpace(baseDirectory)
            || string.IsNullOrWhiteSpace(buildIdentity)
            || string.IsNullOrWhiteSpace(sessionIdentity)
            || !IsSafeIdentity(buildIdentity, 48)
            || !IsSafeIdentity(sessionIdentity, 48))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathRooted(logPath))
            {
                return false;
            }

            string logsDirectory = Path.GetFullPath(Path.Combine(baseDirectory, "Logs"));
            string fullLogPath = Path.GetFullPath(logPath);
            string? parentDirectory = Path.GetDirectoryName(fullLogPath);
            string fileName = Path.GetFileName(fullLogPath);
            if (!string.Equals(parentDirectory, logsDirectory, StringComparison.OrdinalIgnoreCase)
                || !(fileName.StartsWith("native-audio-timing-attempt34-", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("native-audio-timing-attempt41-", StringComparison.OrdinalIgnoreCase))
                || !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            options = new NativeAudioTimingOptions(fullLogPath, buildIdentity, sessionIdentity, duration);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static NativeAudioTimingDiagnostics CreateForTesting(
        Action<string>? sink = null,
        int durationSeconds = MaximumDurationSeconds,
        long? startedTimestamp = null)
    {
        if (durationSeconds is < 1 or > MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        NativeAudioTimingDiagnostics diagnostics = new(
            "attempt34-test",
            "test-session",
            durationSeconds,
            startedTimestamp ?? Stopwatch.GetTimestamp(),
            null,
            sink,
            null,
            testClock: true);
        diagnostics.WriteStartLine(DateTimeOffset.UtcNow);
        return diagnostics;
    }

    internal static NativeAudioTimingDiagnostics CreateDisabledForTesting() => Disabled;

    internal void RecordDuration(AudioTimingMetric metric, long elapsedStopwatchTicks)
    {
        if (_routerManager is not null)
        {
            _routerManager.CaptureSession().RecordDuration(metric, elapsedStopwatchTicks);
            return;
        }

        try
        {
            if (!IsEnabled || _metrics is null || elapsedStopwatchTicks < 0)
            {
                return;
            }

            int index = (int)metric;
            if ((uint)index >= (uint)_metrics.Length)
            {
                return;
            }

            _metrics[index].Record(elapsedStopwatchTicks);
        }
        catch
        {
            // Diagnostics must never change callback or output behavior.
        }
    }

    internal void RecordDurationFromTimestamp(AudioTimingMetric metric, long startedTimestamp)
    {
        if (_routerManager is not null)
        {
            _routerManager.CaptureSession().RecordDurationFromTimestamp(metric, startedTimestamp);
            return;
        }

        try
        {
            if (!IsEnabled)
            {
                return;
            }

            RecordDuration(metric, Stopwatch.GetTimestamp() - startedTimestamp);
        }
        catch
        {
            // Diagnostics must never change callback or output behavior.
        }
    }

    internal void RecordQueueState(int depth, int droppedBlocks)
    {
        if (_routerManager is not null)
        {
            _routerManager.CaptureSession().RecordQueueState(depth, droppedBlocks);
            return;
        }

        try
        {
            if (!IsEnabled || depth < 0 || droppedBlocks < 0)
            {
                return;
            }

            Interlocked.Increment(ref _queueSamples);
            UpdateMaximum(ref _queueMaxDepth, depth);
            if (droppedBlocks > 0)
            {
                Interlocked.Add(ref _queueDroppedBlocks, droppedBlocks);
            }
        }
        catch
        {
            // Diagnostics must never change callback or output behavior.
        }
    }

    internal void RecordQueueDrop()
    {
        if (_routerManager is not null)
        {
            _routerManager.CaptureSession().RecordQueueDrop();
            return;
        }

        try
        {
            if (IsEnabled)
            {
                Interlocked.Increment(ref _queueDroppedBlocks);
            }
        }
        catch
        {
            // Diagnostics must never change callback or output behavior.
        }
    }

    internal void RecordInputProgress(InputProgressMetric metric)
    {
        if (_routerManager is not null)
        {
            _routerManager.CaptureSession().RecordInputProgress(metric);
            return;
        }

        try
        {
            if (!IsEnabled || _inputProgress is null)
            {
                return;
            }

            int index = (int)metric;
            if ((uint)index < (uint)_inputProgress.Length)
            {
                Interlocked.Increment(ref _inputProgress[index]);
            }
        }
        catch
        {
            // Diagnostics must never change controller read or forwarding behavior.
        }
    }

    internal long GetInputProgressSnapshot(InputProgressMetric metric)
    {
        if (_routerManager is not null)
        {
            return _routerManager.CaptureSession().GetInputProgressSnapshot(metric);
        }

        if (_inputProgress is null || (uint)metric >= (uint)_inputProgress.Length)
        {
            return 0;
        }

        return Interlocked.Read(ref _inputProgress[(int)metric]);
    }

    internal AudioTimingMetricSnapshot GetMetricSnapshot(AudioTimingMetric metric)
    {
        if (_routerManager is not null)
        {
            return _routerManager.CaptureSession().GetMetricSnapshot(metric);
        }

        if (_metrics is null || (uint)metric >= (uint)_metrics.Length)
        {
            return default;
        }

        return _metrics[(int)metric].Read();
    }

    internal AudioTimingQueueSnapshot GetQueueSnapshot() => _routerManager is not null
        ? _routerManager.CaptureSession().GetQueueSnapshot()
        : new AudioTimingQueueSnapshot(
            Interlocked.Read(ref _queueSamples),
            Interlocked.Read(ref _queueMaxDepth),
            Interlocked.Read(ref _queueDroppedBlocks));

    internal bool ExpireForTesting(long nowTimestamp)
    {
        if (!_testClock || nowTimestamp < _deadlineTimestamp)
        {
            return false;
        }

        Stop("duration");
        return true;
    }

    internal void PublishForTesting(DateTimeOffset utc, long nowTimestamp)
    {
        if (_testClock)
        {
            WriteLineSafely(BuildSummaryLine(utc, nowTimestamp));
        }
    }

    internal static int GetMaximumSummaryLines() => MaximumSummaryLines;

    internal void Stop(string reason)
    {
        if (_routerManager is not null)
        {
            _routerManager.StopCurrent(reason);
            return;
        }

        if (_metrics is null)
        {
            return;
        }

        if (!RequestStop(reason))
        {
            return;
        }

        if (_publisherTask is null)
        {
            WriteFinalAndClose();
            return;
        }

        try
        {
            _stopSignal?.Cancel();
        }
        catch
        {
            // A stop request is best effort; the bounded publisher also observes the deadline.
        }
    }

    public void Dispose() => Stop("disposed");

    internal static NativeAudioTimingDiagnostics CreateManagedSession(
        NativeAudioTimingOptions options,
        long startedTimestamp,
        bool testClock,
        bool startPublisher,
        NativeAudioTimingDiagnosticsManager? sessionTelemetryManager = null)
    {
        FileStream? file = null;
        StreamWriter? writer = null;
        CancellationTokenSource? stopSignal = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath)!);
            file = new FileStream(
                options.LogPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1024,
                FileOptions.SequentialScan);
            writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            stopSignal = new CancellationTokenSource();
            NativeAudioTimingDiagnostics diagnostics = new(
                options.BuildIdentity,
                options.SessionIdentity,
                options.DurationSeconds,
                startedTimestamp,
                writer,
                null,
                stopSignal,
                testClock,
                options.LogPath,
                sessionTelemetryManager);
            if (!diagnostics.WriteStartLine(DateTimeOffset.UtcNow))
            {
                diagnostics.CloseWriter();
                throw new IOException("The diagnostics start record could not be written.");
            }

            if (startPublisher)
            {
                diagnostics._publisherTask = Task.Run(() => diagnostics.PublishLoopAsync(stopSignal.Token));
            }

            return diagnostics;
        }
        catch
        {
            try
            {
                writer?.Dispose();
            }
            catch
            {
                // Keep invalid or unavailable diagnostics completely fail-closed.
            }

            try
            {
                if (stopSignal is not null)
                {
                    stopSignal.Dispose();
                }
                else
                {
                    file?.Dispose();
                }
            }
            catch
            {
                // Best-effort cleanup for failed session creation.
            }

            throw;
        }
    }

    internal static NativeAudioTimingDiagnostics CreateRouterForTesting(NativeAudioTimingDiagnosticsManager manager) =>
        new(manager);

    internal Task CompletionTask => _publisherTask ?? Task.CompletedTask;
    internal string? LogPath => _logPath;
    internal int DurationSeconds => _durationSeconds;
    internal long DeadlineTimestamp => _deadlineTimestamp;
    internal string? StopReason => Volatile.Read(ref _stopReason);
    internal bool IsSessionActive => Volatile.Read(ref _active) != 0;
    internal bool HasMetrics => _metrics is not null;
    internal bool HasReachedDeadline(long nowTimestamp) => nowTimestamp >= _deadlineTimestamp;
    internal TimeSpan RemainingAt(long nowTimestamp) => TimeSpan.FromSeconds(
        Math.Max(0, (_deadlineTimestamp - nowTimestamp) / (double)Stopwatch.Frequency));

    internal void PublishForTestingAt(DateTimeOffset utc, long nowTimestamp)
    {
        if (_testClock)
        {
            WriteLineSafely(BuildSummaryLine(utc, nowTimestamp));
        }
    }

    internal PostSubmitTelemetrySnapshot LastPostSubmitTelemetry =>
        Volatile.Read(ref _lastPostSubmitTelemetry);

    internal object PostSubmitTelemetrySync => _postSubmitTelemetrySync;

    internal void StorePostSubmitTelemetry(PostSubmitTelemetrySnapshot snapshot) =>
        Volatile.Write(ref _lastPostSubmitTelemetry, snapshot);

    private static bool IsSafeIdentity(string value, int maximumLength)
    {
        if (value.Length is < 1 || value.Length > maximumLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (Volatile.Read(ref _active) != 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(SummaryIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);

                if (Volatile.Read(ref _active) == 0)
                {
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                if (now >= _deadlineTimestamp)
                {
                    RequestStop("duration");
                    break;
                }

                if (!WriteLineSafely(BuildSummaryLine(DateTimeOffset.UtcNow, now)))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Explicit disposal wakes the publisher so it can finalize promptly.
        }
        catch
        {
            RequestStop("writer-error");
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
            WriteFinalAndClose();
        }
    }

    private bool RequestStop(string reason)
    {
        lock (_postSubmitTelemetrySync)
        {
            if (Interlocked.CompareExchange(ref _stopReason, SanitizeReason(reason), null) is not null)
            {
                return false;
            }

            Interlocked.Exchange(ref _active, 0);
            return true;
        }
    }

    private bool WriteStartLine(DateTimeOffset utc)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix} event=start utc={utc.ToUniversalTime():O} build={_buildIdentity} session={_sessionIdentity} duration_s={_durationSeconds} cadence_s={SummaryIntervalSeconds} metric_format=count/avg_ms/max_ms/over10ms");
        return WriteLineSafely(line);
    }

    private void WriteFinalAndClose()
    {
        if (Interlocked.Exchange(ref _finalWritten, 1) != 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        WriteLineSafely(BuildSummaryLine(DateTimeOffset.UtcNow, now));
        string reason = Volatile.Read(ref _stopReason) ?? "stopped";
        string stopLine = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix} event=stop utc={DateTimeOffset.UtcNow:O} elapsed_ms={GetElapsedMilliseconds(now)} reason={reason}");
        WriteLineSafely(stopLine);
        CloseWriter();
    }

    private string BuildSummaryLine(DateTimeOffset utc, long nowTimestamp)
    {
        PostSubmitTelemetrySnapshot postSubmit = _metrics is null
            ? PostSubmitTelemetrySnapshot.NotSampled
            : _sessionTelemetryManager?.CapturePostSubmitTelemetry(this)
                ?? PostSubmitTelemetrySnapshot.NotSampled;

        StringBuilder line = new(1024);
        line.Append(Prefix)
            .Append(" event=summary utc=")
            .Append(utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append(" elapsed_ms=")
            .Append(GetElapsedMilliseconds(nowTimestamp));

        if (_metrics is not null)
        {
            for (int i = 0; i < _metrics.Length; i++)
            {
                AudioTimingMetricSnapshot snapshot = _metrics[i].Read();
                double averageMilliseconds = snapshot.Samples == 0
                    ? 0
                    : TicksToMilliseconds(snapshot.TotalStopwatchTicks) / snapshot.Samples;
                line.Append(' ')
                    .Append(MetricNames[i])
                    .Append('=')
                    .Append(snapshot.Samples)
                    .Append('/')
                    .Append(averageMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(TicksToMilliseconds(snapshot.MaxStopwatchTicks).ToString("F3", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(snapshot.OverBudgetSamples);
            }

            if (_inputProgress is not null)
            {
                for (int i = 0; i < _inputProgress.Length; i++)
                {
                    line.Append(' ')
                        .Append(InputProgressMetricNames[i])
                        .Append('=')
                        .Append(Interlocked.Read(ref _inputProgress[i]));
                }
            }
        }

        AudioTimingQueueSnapshot queue = GetQueueSnapshot();
        line.Append(" queue_samples=")
            .Append(queue.Samples)
            .Append(" queue_max_depth=")
            .Append(queue.MaxDepth)
            .Append(" queue_dropped=")
            .Append(queue.DroppedBlocks);

        line.Append(" post_submit_status=").Append(postSubmit.StatusToken);
        if (postSubmit.Generation is { } generation)
        {
            line.Append(" post_submit_generation=").Append(generation);
        }

        if (postSubmit.TargetCount is { } targetCount)
        {
            line.Append(" post_submit_targets=").Append(targetCount);
        }

        if (postSubmit.Status == PostSubmitTelemetryStatus.Ok)
        {
            line.Append(" native_input_received=").Append(postSubmit.Received!.Value)
                .Append(" native_input_selected=").Append(postSubmit.Selected!.Value)
                .Append(" native_input_transition_overflows=").Append(postSubmit.TransitionOverflows!.Value)
                .Append(" native_input_presented_samples=").Append(postSubmit.PresentedSamples!.Value);
        }

        return line.ToString();
    }

    private long GetElapsedMilliseconds(long nowTimestamp) =>
        Math.Max(0, (long)((nowTimestamp - _startedTimestamp) * 1000.0 / Stopwatch.Frequency));

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private bool WriteLineSafely(string line)
    {
        try
        {
            if (Interlocked.Increment(ref _writtenLines) > MaximumSummaryLines)
            {
                RequestStop("summary-limit");
                return false;
            }

            long nextByteCount = Interlocked.Add(
                ref _writtenBytes,
                Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
            if (nextByteCount > MaximumLogBytes)
            {
                RequestStop("summary-limit");
                return false;
            }

            _writer?.WriteLine(line);
            _writer?.Flush();
            _testSink?.Invoke(line);
            return true;
        }
        catch
        {
            // A final flush can fail after Stop already recorded "user" or
            // "duration". Keep the write failure visible to the control surface.
            Interlocked.Exchange(ref _stopReason, "writer-error");
            Interlocked.Exchange(ref _active, 0);
            return false;
        }
    }

    private void CloseWriter()
    {
        if (Interlocked.Exchange(ref _writerClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Finalization is best effort and must not escape app teardown.
        }
        finally
        {
            _stopSignal?.Dispose();
        }
    }

    private static string SanitizeReason(string reason) =>
        IsSafeIdentity(reason, 24) ? reason : "other";

    private static void UpdateMaximum(ref long target, long value)
    {
        long observed = Interlocked.Read(ref target);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref target, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private sealed class MetricCounter
    {
        private long _samples;
        private long _totalStopwatchTicks;
        private long _maxStopwatchTicks;
        private long _overBudgetSamples;

        public void Record(long elapsedStopwatchTicks)
        {
            Interlocked.Increment(ref _samples);
            Interlocked.Add(ref _totalStopwatchTicks, elapsedStopwatchTicks);
            UpdateMaximum(ref _maxStopwatchTicks, elapsedStopwatchTicks);
            if (elapsedStopwatchTicks > OverBudgetTicks)
            {
                Interlocked.Increment(ref _overBudgetSamples);
            }
        }

        public AudioTimingMetricSnapshot Read() => new(
            Interlocked.Read(ref _samples),
            Interlocked.Read(ref _totalStopwatchTicks),
            Interlocked.Read(ref _maxStopwatchTicks),
            Interlocked.Read(ref _overBudgetSamples));
    }
}
