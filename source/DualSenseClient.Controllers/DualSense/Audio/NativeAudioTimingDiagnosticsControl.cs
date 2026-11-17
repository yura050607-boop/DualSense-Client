using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace DualSenseClient.Controllers.DualSense.Audio;

public enum NativeAudioTimingDiagnosticsState
{
    Off,
    Starting,
    Running,
    Stopping,
    Expired,
    Error
}

public sealed record NativeAudioTimingDiagnosticsStatus(
    NativeAudioTimingDiagnosticsState State,
    string? LogPath,
    TimeSpan Remaining,
    string? ErrorMessage);

public interface INativeAudioTimingDiagnosticsControl
{
    NativeAudioTimingDiagnosticsStatus GetStatus();
    Task<NativeAudioTimingDiagnosticsStatus> StartAsync(int durationMinutes);
    Task<NativeAudioTimingDiagnosticsStatus> StopAsync();
}

/// <summary>
/// User controls for the process-wide aggregate audio-timing and input-progress capture.
/// Sessions contain timing summaries and boundary counts only, without audio, input
/// values, or device identity.
/// </summary>
public sealed class NativeAudioTimingDiagnosticsControl : INativeAudioTimingDiagnosticsControl
{
    private readonly NativeAudioTimingDiagnosticsManager _manager;

    public NativeAudioTimingDiagnosticsControl()
        : this(NativeAudioTimingDiagnostics.ProcessControl)
    {
    }

    internal NativeAudioTimingDiagnosticsControl(NativeAudioTimingDiagnosticsManager manager)
    {
        _manager = manager;
    }

    public NativeAudioTimingDiagnosticsStatus GetStatus() => _manager.GetStatus();

    public Task<NativeAudioTimingDiagnosticsStatus> StartAsync(int durationMinutes) =>
        _manager.StartAsync(durationMinutes);

    public Task<NativeAudioTimingDiagnosticsStatus> StopAsync() => _manager.StopAsync();
}

internal sealed class NativeAudioTimingDiagnosticsManager
{
    private const string ManualLogPrefix = "native-audio-timing-attempt41-";
    private const string ManualBuildPrefix = "attempt41-";
    private const string QuotaError = "The timing-log quota for this stage is full (16 MiB). Remove older timing logs from this stage's Logs folder, then retry. No logs were deleted.";
    private const string StartError = "Could not start diagnostics. Check that the stage Logs folder is writable and has free disk space.";
    private const string WriterError = "Writing the timing log failed. The capture stopped; check that the stage Logs folder is writable and has free disk space.";
    private const string LimitError = "The timing capture reached its safety limit and stopped. Start a new capture if more data is needed.";

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _baseDirectory;
    private readonly string _logsDirectory;
    private readonly Func<long> _getTimestamp;
    private readonly Func<string> _buildIdentityFactory;
    private readonly Func<string> _sessionIdentityFactory;
    private readonly bool _testClock;
    private readonly bool _startPublisher;
    private readonly long _ownedLogQuotaBytes;
    private readonly object _telemetrySourceLock = new();
    private NativeAudioTimingDiagnostics? _session;
    private Func<PostSubmitTelemetrySnapshot>? _postSubmitTelemetrySource;
    private object? _postSubmitTelemetryRegistration;
    private string? _lastLogPath;
    private string? _failureMessage;
    private int _starting;
    private int _audioContentDiagnosticsSuppressed;

    private NativeAudioTimingDiagnosticsManager(
        string baseDirectory,
        Func<long> getTimestamp,
        Func<string> buildIdentityFactory,
        Func<string> sessionIdentityFactory,
        bool testClock,
        bool startPublisher,
        long ownedLogQuotaBytes)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _logsDirectory = Path.Combine(_baseDirectory, "Logs");
        _getTimestamp = getTimestamp;
        _buildIdentityFactory = buildIdentityFactory;
        _sessionIdentityFactory = sessionIdentityFactory;
        _testClock = testClock;
        _startPublisher = startPublisher;
        _ownedLogQuotaBytes = ownedLogQuotaBytes;
    }

    internal static NativeAudioTimingDiagnosticsManager CreateProcessManager()
    {
        NativeAudioTimingDiagnosticsManager manager = new(
            AppContext.BaseDirectory,
            Stopwatch.GetTimestamp,
            CreateBuildIdentity,
            CreateSessionIdentity,
            testClock: false,
            startPublisher: true,
            NativeAudioTimingDiagnostics.MaximumOwnedLogBytes);
        manager.TryStartLauncherSession();
        return manager;
    }

    internal static NativeAudioTimingDiagnosticsManager CreateForTesting(
        string baseDirectory,
        Func<long> getTimestamp,
        Func<string>? buildIdentityFactory = null,
        Func<string>? sessionIdentityFactory = null,
        bool testClock = true,
        bool startPublisher = false,
        long? ownedLogQuotaBytes = null) =>
        new(
            baseDirectory,
            getTimestamp,
            buildIdentityFactory ?? (() => "attempt41-test"),
            sessionIdentityFactory ?? CreateSessionIdentity,
            testClock,
            startPublisher,
            ownedLogQuotaBytes ?? NativeAudioTimingDiagnostics.MaximumOwnedLogBytes);

    internal NativeAudioTimingDiagnostics CaptureSession() =>
        Volatile.Read(ref _session) ?? NativeAudioTimingDiagnostics.CreateDisabledForTesting();

    internal IDisposable RegisterPostSubmitTelemetrySource(Func<PostSubmitTelemetrySnapshot> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        object registration = new();
        lock (_telemetrySourceLock)
        {
            _postSubmitTelemetryRegistration = registration;
            _postSubmitTelemetrySource = capture;
        }

        return new TelemetrySourceRegistration(this, registration);
    }

    internal PostSubmitTelemetrySnapshot CapturePostSubmitTelemetry(NativeAudioTimingDiagnostics session)
    {
        lock (session.PostSubmitTelemetrySync)
        {
            return CapturePostSubmitTelemetryUnderSessionLock(session);
        }
    }

    private PostSubmitTelemetrySnapshot CapturePostSubmitTelemetryUnderSessionLock(
        NativeAudioTimingDiagnostics session)
    {
        PostSubmitTelemetrySnapshot last = session.LastPostSubmitTelemetry;
        if (!IsCurrentActiveSession(session))
        {
            return last;
        }

        Func<PostSubmitTelemetrySnapshot>? source;
        object? registration;
        lock (_telemetrySourceLock)
        {
            source = _postSubmitTelemetrySource;
            registration = _postSubmitTelemetryRegistration;
        }

        PostSubmitTelemetrySnapshot snapshot;
        if (source is null)
        {
            snapshot = new(PostSubmitTelemetryStatus.NoSource);
        }
        else
        {
            try
            {
                snapshot = source();
                if (snapshot.Status == PostSubmitTelemetryStatus.Ok
                    && (snapshot.Generation is not >= 0
                        || snapshot.TargetCount is not > 0
                        || snapshot.Received is null
                        || snapshot.Selected is null
                        || snapshot.TransitionOverflows is null
                        || snapshot.PresentedSamples is null))
                {
                    snapshot = new(PostSubmitTelemetryStatus.InvalidSchema, snapshot.Generation, snapshot.TargetCount);
                }
            }
            catch
            {
                snapshot = new(PostSubmitTelemetryStatus.NativeError);
            }
        }

        if (!IsCurrentActiveSession(session))
        {
            return session.LastPostSubmitTelemetry;
        }

        lock (_telemetrySourceLock)
        {
            if (!ReferenceEquals(registration, _postSubmitTelemetryRegistration)
                || !ReferenceEquals(source, _postSubmitTelemetrySource))
            {
                snapshot = new(PostSubmitTelemetryStatus.Stale);
            }

            if (IsCurrentActiveSession(session))
            {
                session.StorePostSubmitTelemetry(snapshot);
                return snapshot;
            }
        }

        return session.LastPostSubmitTelemetry;
    }

    internal PostSubmitTelemetrySnapshot CapturePostSubmitTelemetryForTesting(NativeAudioTimingDiagnostics session) =>
        CapturePostSubmitTelemetry(session);

    private bool IsCurrentActiveSession(NativeAudioTimingDiagnostics session) =>
        ReferenceEquals(Volatile.Read(ref _session), session) && session.IsSessionActive;

    private void UnregisterPostSubmitTelemetrySource(object registration)
    {
        lock (_telemetrySourceLock)
        {
            if (!ReferenceEquals(registration, _postSubmitTelemetryRegistration))
            {
                return;
            }

            _postSubmitTelemetrySource = null;
            _postSubmitTelemetryRegistration = null;
        }
    }

    internal bool ShouldSuppressAudioContentDiagnostics =>
        Volatile.Read(ref _audioContentDiagnosticsSuppressed) != 0;

    internal NativeAudioTimingDiagnosticsStatus GetStatus()
    {
        if (Volatile.Read(ref _starting) != 0)
        {
            return new(NativeAudioTimingDiagnosticsState.Starting, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, null);
        }

        string? failureMessage = Volatile.Read(ref _failureMessage);
        if (failureMessage is not null)
        {
            return new(NativeAudioTimingDiagnosticsState.Error, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, failureMessage);
        }

        NativeAudioTimingDiagnostics? session = Volatile.Read(ref _session);
        if (session is null || !session.HasMetrics)
        {
            return new(NativeAudioTimingDiagnosticsState.Off, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, null);
        }

        long now = _getTimestamp();
        if (session.IsSessionActive && session.HasReachedDeadline(now))
        {
            session.Stop("duration");
        }

        string? stopReason = session.StopReason;
        if (string.Equals(stopReason, "duration", StringComparison.Ordinal))
        {
            return new(NativeAudioTimingDiagnosticsState.Expired, session.LogPath, TimeSpan.Zero, null);
        }

        if (string.Equals(stopReason, "writer-error", StringComparison.Ordinal))
        {
            return new(NativeAudioTimingDiagnosticsState.Error, session.LogPath, TimeSpan.Zero, WriterError);
        }

        if (string.Equals(stopReason, "summary-limit", StringComparison.Ordinal))
        {
            return new(NativeAudioTimingDiagnosticsState.Error, session.LogPath, TimeSpan.Zero, LimitError);
        }

        if (session.IsSessionActive)
        {
            return new(
                NativeAudioTimingDiagnosticsState.Running,
                session.LogPath,
                session.RemainingAt(now),
                null);
        }

        if (!session.CompletionTask.IsCompleted)
        {
            return new(NativeAudioTimingDiagnosticsState.Stopping, session.LogPath, TimeSpan.Zero, null);
        }

        return new(NativeAudioTimingDiagnosticsState.Off, session.LogPath, TimeSpan.Zero, null);
    }

    internal async Task<NativeAudioTimingDiagnosticsStatus> StartAsync(int durationMinutes)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Keep later-started captures from allowing the legacy raw-byte audio
            // head diagnostic through existing capture instances.
            Volatile.Write(ref _audioContentDiagnosticsSuppressed, 1);
            NativeAudioTimingDiagnostics? previous = Volatile.Read(ref _session);
            if (previous is not null && previous.IsSessionActive)
            {
                long now = _getTimestamp();
                if (!previous.HasReachedDeadline(now))
                {
                    return GetStatus();
                }

                previous.Stop("duration");
            }

            if (durationMinutes is not (3 or 10 or 30))
            {
                const string message = "Choose a 3, 10, or 30 minute capture duration.";
                Volatile.Write(ref _failureMessage, message);
                return new(NativeAudioTimingDiagnosticsState.Error, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, message);
            }

            Volatile.Write(ref _starting, 1);
            Volatile.Write(ref _failureMessage, null);

            if (previous is not null)
            {
                await previous.CompletionTask.ConfigureAwait(false);
            }

            NativeAudioTimingDiagnostics session;
            try
            {
                session = await Task.Run(() => CreateManualSession(durationMinutes * 60)).ConfigureAwait(false);
            }
            catch (CaptureStartException ex)
            {
                Volatile.Write(ref _failureMessage, ex.UserMessage);
                return new(NativeAudioTimingDiagnosticsState.Error, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, ex.UserMessage);
            }
            catch
            {
                Volatile.Write(ref _failureMessage, StartError);
                return new(NativeAudioTimingDiagnosticsState.Error, Volatile.Read(ref _lastLogPath), TimeSpan.Zero, StartError);
            }

            Volatile.Write(ref _lastLogPath, session.LogPath);
            Volatile.Write(ref _session, session);
            Volatile.Write(ref _starting, 0);
            return GetStatus();
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
            _operationGate.Release();
        }
    }

    internal async Task<NativeAudioTimingDiagnosticsStatus> StopAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NativeAudioTimingDiagnostics? session = Volatile.Read(ref _session);
            if (session is not null && session.IsSessionActive)
            {
                if (session.HasReachedDeadline(_getTimestamp()))
                {
                    session.Stop("duration");
                }
                else
                {
                    session.Stop("user");
                }
            }

            return GetStatus();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal void StopCurrent(string reason)
    {
        NativeAudioTimingDiagnostics? session = Volatile.Read(ref _session);
        session?.Stop(reason);
    }

    internal NativeAudioTimingDiagnostics? GetSessionForTesting() => Volatile.Read(ref _session);

    private NativeAudioTimingDiagnostics CreateManualSession(int durationSeconds)
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            EnsureQuotaAvailable();

            string buildIdentity = _buildIdentityFactory();
            string sessionIdentity = _sessionIdentityFactory();
            if (!IsSafeIdentity(buildIdentity) || !IsSafeIdentity(sessionIdentity))
            {
                throw new CaptureStartException(StartError);
            }

            string fileName = ManualLogPrefix + sessionIdentity + ".log";
            string logPath = Path.GetFullPath(Path.Combine(_logsDirectory, fileName));
            if (!string.Equals(Path.GetDirectoryName(logPath), _logsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new CaptureStartException(StartError);
            }

            Volatile.Write(ref _lastLogPath, logPath);
            NativeAudioTimingOptions options = new(logPath, buildIdentity, sessionIdentity, durationSeconds);
            return NativeAudioTimingDiagnostics.CreateManagedSession(
                options,
                _getTimestamp(),
                _testClock,
                _startPublisher,
                this);
        }
        catch (CaptureStartException)
        {
            throw;
        }
        catch (QuotaExceededException)
        {
            throw new CaptureStartException(QuotaError);
        }
        catch
        {
            throw new CaptureStartException(StartError);
        }
    }

    private void TryStartLauncherSession()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.EnabledEnvironmentName),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(ref _audioContentDiagnosticsSuppressed, 1);

        if (!NativeAudioTimingDiagnostics.TryParseOptions(
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.EnabledEnvironmentName),
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.DurationEnvironmentName),
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.LogPathEnvironmentName),
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.BuildIdentityEnvironmentName),
                Environment.GetEnvironmentVariable(NativeAudioTimingDiagnostics.SessionIdentityEnvironmentName),
                _baseDirectory,
                out NativeAudioTimingOptions options))
        {
            Volatile.Write(ref _failureMessage, "The startup timing options were invalid; diagnostics stayed off.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_logsDirectory);
            EnsureQuotaAvailable();
            NativeAudioTimingDiagnostics session = NativeAudioTimingDiagnostics.CreateManagedSession(
                options,
                _getTimestamp(),
                testClock: false,
                startPublisher: true,
                sessionTelemetryManager: this);
            Volatile.Write(ref _lastLogPath, session.LogPath);
            Volatile.Write(ref _session, session);
        }
        catch (QuotaExceededException)
        {
            Volatile.Write(ref _failureMessage, QuotaError);
        }
        catch
        {
            Volatile.Write(ref _lastLogPath, options.LogPath);
            Volatile.Write(ref _failureMessage, StartError);
        }
    }

    private void EnsureQuotaAvailable()
    {
        long existingBytes = 0;
        if (Directory.Exists(_logsDirectory))
        {
            foreach (string filePath in Directory.EnumerateFiles(
                         _logsDirectory,
                         "native-audio-timing-attempt*.log",
                         SearchOption.TopDirectoryOnly))
            {
                long fileLength = new FileInfo(filePath).Length;
                if (existingBytes > _ownedLogQuotaBytes - fileLength)
                {
                    existingBytes = _ownedLogQuotaBytes;
                    break;
                }

                existingBytes += fileLength;
            }
        }

        if (_ownedLogQuotaBytes < NativeAudioTimingDiagnostics.MaximumLogBytes
            || existingBytes > _ownedLogQuotaBytes - NativeAudioTimingDiagnostics.MaximumLogBytes)
        {
            throw new QuotaExceededException();
        }
    }

    private static bool IsSafeIdentity(string value) =>
        value.Length is >= 1 and <= 48
        && value.All(static c => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '_' or '.');

    private static string CreateBuildIdentity()
    {
        string assemblyPath = typeof(NativeAudioTimingDiagnostics).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            throw new IOException("The loaded controller assembly path is unavailable.");
        }

        using FileStream assembly = File.OpenRead(assemblyPath);
        byte[] hash = SHA256.HashData(assembly);
        return ManualBuildPrefix + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string CreateSessionIdentity() =>
        DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
        + "-"
        + Guid.NewGuid().ToString("N")[..8];

    private sealed class CaptureStartException(string userMessage) : Exception
    {
        public string UserMessage { get; } = userMessage;
    }

    private sealed class QuotaExceededException : Exception
    {
    }

    private sealed class TelemetrySourceRegistration(
        NativeAudioTimingDiagnosticsManager manager,
        object registration) : IDisposable
    {
        private NativeAudioTimingDiagnosticsManager? _manager = manager;

        public void Dispose() =>
            Interlocked.Exchange(ref _manager, null)?.UnregisterPostSubmitTelemetrySource(registration);
    }
}
