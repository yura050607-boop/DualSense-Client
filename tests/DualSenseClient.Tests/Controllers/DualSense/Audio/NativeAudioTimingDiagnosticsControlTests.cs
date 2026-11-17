using System.Diagnostics;
using System.Text;
using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public class NativeAudioTimingDiagnosticsControlTests
{
    [Test]
    public async Task StopStartAndExpiredStart_IsolateCountersAndCloseEachLocalLog()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            NativeAudioTimingDiagnosticsControl control = new(manager);

            NativeAudioTimingDiagnosticsStatus firstStart = await control.StartAsync(3);
            NativeAudioTimingDiagnostics firstSession = manager.GetSessionForTesting()!;
            router.RecordDuration(AudioTimingMetric.NativeAudioCallback, 100);
            NativeAudioTimingDiagnosticsStatus firstStop = await control.StopAsync();
            string firstText = File.ReadAllText(firstSession.LogPath!);
            NativeAudioTimingDiagnostics staleCallbackSession = firstSession;

            NativeAudioTimingDiagnosticsStatus secondStart = await control.StartAsync(10);
            NativeAudioTimingDiagnostics secondSession = manager.GetSessionForTesting()!;
            staleCallbackSession.RecordDuration(AudioTimingMetric.NativeAudioCallback, 900);
            router.RecordDuration(AudioTimingMetric.NativeAudioCallback, 250);
            AudioTimingMetricSnapshot secondSnapshot = secondSession.GetMetricSnapshot(AudioTimingMetric.NativeAudioCallback);
            NativeAudioTimingDiagnosticsStatus secondStop = await control.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(firstStart.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
                Assert.That(firstStop.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Off));
                Assert.That(secondStart.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
                Assert.That(secondStop.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Off));
                Assert.That(firstSession.LogPath, Is.Not.EqualTo(secondSession.LogPath));
                Assert.That(firstSession.GetMetricSnapshot(AudioTimingMetric.NativeAudioCallback).Samples, Is.EqualTo(1));
                Assert.That(secondSnapshot.Samples, Is.EqualTo(1), "a stale callback session must not write into the new capture");
                Assert.That(firstText, Does.Contain("event=stop"));
                Assert.That(firstText, Does.Contain("reason=user"));
                Assert.That(File.ReadAllText(secondSession.LogPath!), Does.Contain("reason=user"));
                Assert.That(Directory.GetFiles(Path.Combine(root, "Logs"), "native-audio-timing-attempt41-*.log").Length, Is.EqualTo(2));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ConcurrentStartRequests_CreateOnlyOnePublisherAndFile()
    {
        string root = CreateTemporaryDirectory();
        NativeAudioTimingDiagnosticsManager? manager = null;
        try
        {
            manager = CreateManager(root, startPublisher: true);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            NativeAudioTimingDiagnosticsStatus[] results = await Task.WhenAll(
                control.StartAsync(3),
                control.StartAsync(30));

            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;
            Assert.Multiple(() =>
            {
                Assert.That(results.All(result => result.State == NativeAudioTimingDiagnosticsState.Running), Is.True);
                Assert.That(results.Select(result => result.LogPath).Distinct().Count(), Is.EqualTo(1));
                Assert.That(Directory.GetFiles(Path.Combine(root, "Logs"), "native-audio-timing-attempt41-*.log").Length, Is.EqualTo(1));
                Assert.That(session.DurationSeconds, Is.AnyOf(180, 1800));
            });

            await control.StopAsync();
            await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await StopManagerForCleanupAsync(manager);
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ThirtyMinuteCapture_CompletesEveryFiveSecondSummaryWithinFiniteBudgets()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            long now = 50_000;
            NativeAudioTimingDiagnosticsManager manager = NativeAudioTimingDiagnosticsManager.CreateForTesting(
                root,
                () => now,
                () => "attempt41-test",
                () => "thirty-minute-session",
                testClock: true,
                startPublisher: false);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            NativeAudioTimingDiagnosticsStatus started = await control.StartAsync(30);
            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;
            DateTimeOffset origin = DateTimeOffset.UnixEpoch;

            for (int summary = 1; summary <= 360; summary++)
            {
                now = 50_000 + (long)(summary * NativeAudioTimingDiagnostics.SummaryIntervalSeconds * Stopwatch.Frequency);
                session.PublishForTestingAt(
                    origin.AddSeconds(summary * NativeAudioTimingDiagnostics.SummaryIntervalSeconds),
                    now);
            }

            NativeAudioTimingDiagnosticsStatus expired = control.GetStatus();
            string[] lines = File.ReadAllLines(session.LogPath!, Encoding.UTF8);
            long byteCount = new FileInfo(session.LogPath!).Length;

            Assert.Multiple(() =>
            {
                Assert.That(started.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
                Assert.That(session.DurationSeconds, Is.EqualTo(1800));
                Assert.That(expired.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Expired));
                Assert.That(session.StopReason, Is.EqualTo("duration"));
                Assert.That(lines.Length, Is.EqualTo(363), "start, all 360 cadence summaries, final summary, and stop must fit");
                Assert.That(lines.Length, Is.LessThanOrEqualTo(NativeAudioTimingDiagnostics.MaximumSummaryLines));
                Assert.That(byteCount, Is.LessThanOrEqualTo(NativeAudioTimingDiagnostics.MaximumLogBytes));
                Assert.That(lines[^1], Does.Contain("event=stop").And.Contain("reason=duration"));
                Assert.That(string.Join('\n', lines), Does.Not.Contain("reason=summary-limit"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task UnwritableLogRootAndFullQuota_ReturnActionableErrorsWithoutDeletingLogs()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string fileAsDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(fileAsDirectory, "preserve");
            NativeAudioTimingDiagnosticsManager unwritableManager = CreateManager(fileAsDirectory);
            NativeAudioTimingDiagnosticsStatus unwritable = await new NativeAudioTimingDiagnosticsControl(unwritableManager).StartAsync(10);

            string quotaRoot = Path.Combine(root, "quota-stage");
            string logs = Path.Combine(quotaRoot, "Logs");
            Directory.CreateDirectory(logs);
            string preservedLog = Path.Combine(logs, "native-audio-timing-attempt34-existing.log");
            using (FileStream existing = File.Create(preservedLog))
            {
                existing.SetLength(NativeAudioTimingDiagnostics.MaximumOwnedLogBytes);
            }

            NativeAudioTimingDiagnosticsManager quotaManager = CreateManager(quotaRoot);
            NativeAudioTimingDiagnosticsStatus quota = await new NativeAudioTimingDiagnosticsControl(quotaManager).StartAsync(3);

            Assert.Multiple(() =>
            {
                Assert.That(unwritable.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Error));
                Assert.That(unwritable.ErrorMessage, Does.Contain("writable"));
                Assert.That(unwritableManager.GetSessionForTesting(), Is.Null);
                Assert.That(quota.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Error));
                Assert.That(quota.ErrorMessage, Does.Contain("quota"));
                Assert.That(new FileInfo(preservedLog).Length, Is.EqualTo(NativeAudioTimingDiagnostics.MaximumOwnedLogBytes));
                Assert.That(Directory.GetFiles(logs, "native-audio-timing-attempt41-*.log"), Is.Empty);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CreateNewCollision_DoesNotOverwriteAnExistingTimingLog()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string logs = Path.Combine(root, "Logs");
            Directory.CreateDirectory(logs);
            string existingPath = Path.Combine(logs, "native-audio-timing-attempt41-fixed-session.log");
            const string contents = "preserve existing diagnostic";
            File.WriteAllText(existingPath, contents);
            NativeAudioTimingDiagnosticsManager manager = NativeAudioTimingDiagnosticsManager.CreateForTesting(
                root,
                Stopwatch.GetTimestamp,
                () => "attempt41-test",
                () => "fixed-session",
                testClock: false,
                startPublisher: false);

            NativeAudioTimingDiagnosticsStatus result = await new NativeAudioTimingDiagnosticsControl(manager).StartAsync(3);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Error));
                Assert.That(result.ErrorMessage, Does.Contain("writable"));
                Assert.That(File.ReadAllText(existingPath), Is.EqualTo(contents));
                Assert.That(manager.GetSessionForTesting(), Is.Null);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task InvalidDuration_IsRejectedWithoutCreatingOrEnablingCapture()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnosticsControl control = new(manager);
            NativeAudioTimingDiagnosticsStatus result = await control.StartAsync(5);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Error));
                Assert.That(result.ErrorMessage, Does.Contain("3, 10, or 30"));
                Assert.That(manager.GetSessionForTesting(), Is.Null);
                Assert.That(Directory.Exists(Path.Combine(root, "Logs")), Is.False);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static NativeAudioTimingDiagnosticsManager CreateManager(string root, bool startPublisher = false) =>
        NativeAudioTimingDiagnosticsManager.CreateForTesting(
            root,
            Stopwatch.GetTimestamp,
            () => "attempt41-test",
            testClock: false,
            startPublisher: startPublisher);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "native-audio-timing-attempt41-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task StopManagerForCleanupAsync(NativeAudioTimingDiagnosticsManager? manager)
    {
        if (manager is null)
        {
            return;
        }

        NativeAudioTimingDiagnostics? session = manager.GetSessionForTesting();
        if (session is null)
        {
            return;
        }

        if (session.IsSessionActive)
        {
            await new NativeAudioTimingDiagnosticsControl(manager).StopAsync();
        }

        await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
