using System.Diagnostics;
using System.Text;
using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public class NativeAudioTimingDiagnosticsTests
{
    [Test]
    public void TryParseOptions_RequiresExactOptInBoundedDurationSafeIdentityAndStageLogPath()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "attempt34-stage");
        string logPath = Path.Combine(baseDirectory, "Logs", "native-audio-timing-attempt34-test.log");

        Assert.Multiple(() =>
        {
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "true", "10", logPath, "fixed17-attempt34", "run-1", baseDirectory, out _), Is.False);
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "0", logPath, "fixed17-attempt34", "run-1", baseDirectory, out _), Is.False);
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "1801", logPath, "attempt41-build", "run-1", baseDirectory, out _), Is.False);
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "10", Path.Combine(baseDirectory, "Logs", "other.log"), "fixed17-attempt34", "run-1", baseDirectory, out _), Is.False);
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "10", logPath, "fixed17-attempt34\\unsafe", "run-1", baseDirectory, out _), Is.False);
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "10", logPath, "fixed17-attempt34", "run-1", baseDirectory, out NativeAudioTimingOptions options), Is.True);
            Assert.That(options.DurationSeconds, Is.EqualTo(10));
            Assert.That(options.LogPath, Is.EqualTo(Path.GetFullPath(logPath)));

            string manualLogPath = Path.Combine(baseDirectory, "Logs", "native-audio-timing-attempt41-session.log");
            Assert.That(NativeAudioTimingDiagnostics.TryParseOptions(
                "1", "1800", manualLogPath, "attempt41-build", "run-2", baseDirectory, out NativeAudioTimingOptions manualOptions), Is.True);
            Assert.That(manualOptions.DurationSeconds, Is.EqualTo(1800));
        });
    }

    [Test]
    public void DisabledSession_AllocatesNoMetricsOrQueueStateAndIgnoresStop()
    {
        NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateDisabledForTesting();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.IsEnabled, Is.False);
            Assert.That(diagnostics.IsOptedIn, Is.False);
            Assert.That(diagnostics.GetMetricSnapshot(AudioTimingMetric.NativeAudioCallback), Is.EqualTo(default(AudioTimingMetricSnapshot)));
            Assert.That(diagnostics.GetQueueSnapshot(), Is.EqualTo(default(AudioTimingQueueSnapshot)));
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.Zero);
        });

        diagnostics.Stop("test");
        Assert.That(diagnostics.IsEnabled, Is.False);
    }

    [Test]
    public void RecordDuration_UsesAtomicAggregateCountersAcrossConcurrentCallbacks()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting();
        const int workers = 8;
        const int samplesPerWorker = 500;
        long sampleTicks = Stopwatch.Frequency / 50;

        Parallel.For(0, workers, _ =>
        {
            for (int sample = 0; sample < samplesPerWorker; sample++)
            {
                diagnostics.RecordDuration(AudioTimingMetric.NativeAudioCallback, sampleTicks);
            }
        });

        AudioTimingMetricSnapshot snapshot = diagnostics.GetMetricSnapshot(AudioTimingMetric.NativeAudioCallback);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Samples, Is.EqualTo(workers * samplesPerWorker));
            Assert.That(snapshot.TotalStopwatchTicks, Is.EqualTo((long)workers * samplesPerWorker * sampleTicks));
            Assert.That(snapshot.MaxStopwatchTicks, Is.EqualTo(sampleTicks));
            Assert.That(snapshot.OverBudgetSamples, Is.EqualTo(workers * samplesPerWorker));
        });
    }

    [Test]
    public void SummaryPublisher_ContainsAggregatesOnlyAndRespectsLineAndByteCaps()
    {
        List<string> lines = [];
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting(lines.Add);
        diagnostics.RecordDuration(AudioTimingMetric.NativeAudioCallback, Stopwatch.Frequency / 1000);
        diagnostics.RecordQueueState(depth: 3, droppedBlocks: 1);
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalFullReportParsed);
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalControlStateChanged);

        for (int i = 0; i < 100; i++)
        {
            diagnostics.PublishForTesting(DateTimeOffset.UnixEpoch.AddSeconds(i), i * Stopwatch.Frequency);
        }

        diagnostics.Stop("test");
        int serializedLength = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
        Assert.Multiple(() =>
        {
            Assert.That(lines.Count, Is.LessThanOrEqualTo(NativeAudioTimingDiagnostics.GetMaximumSummaryLines()));
            Assert.That(serializedLength, Is.LessThanOrEqualTo(NativeAudioTimingDiagnostics.MaximumLogBytes));
            Assert.That(lines.All(line => line.StartsWith(NativeAudioTimingDiagnostics.Prefix, StringComparison.Ordinal)), Is.True);
            Assert.That(lines.Any(line => line.Contains("native_audio_callback=1/1.000/1.000/0", StringComparison.Ordinal)), Is.True);
            Assert.That(lines.Any(line => line.Contains("queue_max_depth=3", StringComparison.Ordinal)
                                          && line.Contains("queue_dropped=1", StringComparison.Ordinal)), Is.True);
            Assert.That(lines.Any(line => line.Contains("physical_full_parsed=1", StringComparison.Ordinal)
                                          && line.Contains("physical_control_changes=1", StringComparison.Ordinal)), Is.True);
            Assert.That(string.Join('\n', lines), Does.Not.Contain("pcm="));
            Assert.That(string.Join('\n', lines), Does.Not.Contain("hid="));
            Assert.That(string.Join('\n', lines), Does.Not.Contain("button="));
        });
    }

    [Test]
    public void DiagnosticSession_ExpiresAtItsBoundedDeadline()
    {
        List<string> lines = [];
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting(lines.Add, durationSeconds: 1, startedTimestamp: 100);
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalReadSucceeded);

        Assert.That(diagnostics.IsEnabled, Is.True);
        Assert.That(diagnostics.ExpireForTesting(100 + Stopwatch.Frequency), Is.True);
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalReadSucceeded);
        Assert.That(diagnostics.IsEnabled, Is.False);
        Assert.That(diagnostics.GetMetricSnapshot(AudioTimingMetric.NativeAudioCallback).Samples, Is.Zero);
        Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(1));
        Assert.That(lines.Any(line => line.Contains("event=stop", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void ExplicitStop_FinalizesExactlyOnce()
    {
        List<string> lines = [];
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting(lines.Add);

        diagnostics.Stop("test");
        diagnostics.Stop("duplicate");

        Assert.Multiple(() =>
        {
            Assert.That(lines.Count(line => line.Contains("event=summary", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines.Count(line => line.Contains("event=stop", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines.Last().Contains("reason=test", StringComparison.Ordinal), Is.True);
        });
    }

    [Test]
    public void FinalWriteFailureAfterExplicitStop_IsNotMaskedByUserStopReason()
    {
        using NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting(
            _ => throw new IOException("synthetic writer failure"));

        diagnostics.Stop("user");

        Assert.That(diagnostics.StopReason, Is.EqualTo("writer-error"));
    }

    [Test]
    public void Dispose_FinalizesExactlyOnce()
    {
        List<string> lines = [];
        NativeAudioTimingDiagnostics diagnostics = NativeAudioTimingDiagnostics.CreateForTesting(lines.Add);
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalReadSucceeded);

        diagnostics.Dispose();
        diagnostics.RecordInputProgress(InputProgressMetric.PhysicalReadSucceeded);
        diagnostics.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(lines.Count(line => line.Contains("event=summary", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines.Count(line => line.Contains("event=stop", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines.Last().Contains("reason=disposed", StringComparison.Ordinal), Is.True);
            Assert.That(diagnostics.GetInputProgressSnapshot(InputProgressMetric.PhysicalReadSucceeded), Is.EqualTo(1));
        });
    }
}
