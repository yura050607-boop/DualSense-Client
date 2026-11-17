using System.Diagnostics;
using DualSenseClient.Controllers;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.Emulation;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

[NonParallelizable]
public sealed class NativePostSubmitTelemetrySessionTests
{
    [Test]
    public async Task ManualSession_SamplesOnlyWhileCurrentAndLogsFixedPrivacySafeCounters()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            int calls = 0;
            using IDisposable registration = router.RegisterPostSubmitTelemetrySource(() =>
            {
                calls++;
                return new PostSubmitTelemetrySnapshot(
                    PostSubmitTelemetryStatus.Ok,
                    Generation: 17,
                    TargetCount: 1,
                    Received: 101,
                    Selected: 99,
                    TransitionOverflows: 2,
                    PresentedSamples: 88);
            })!;

            Assert.That(router.CaptureSession().IsEnabled, Is.False);
            Assert.That(calls, Is.Zero, "default-off construction must not invoke the telemetry provider");

            NativeAudioTimingDiagnosticsControl control = new(manager);
            Assert.That((await control.StartAsync(3)).State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
            NativeAudioTimingDiagnostics firstSession = manager.GetSessionForTesting()!;
            firstSession.PublishForTestingAt(DateTimeOffset.UnixEpoch, Stopwatch.GetTimestamp());
            Assert.That(calls, Is.EqualTo(1));

            await control.StopAsync();
            Assert.That(calls, Is.EqualTo(1), "the final stop summary must reuse the last bounded sample");
            string firstLog = File.ReadAllText(firstSession.LogPath!);
            Assert.Multiple(() =>
            {
                Assert.That(firstLog, Does.Contain("post_submit_status=ok post_submit_generation=17 post_submit_targets=1"));
                Assert.That(firstLog, Does.Contain("native_input_received=101 native_input_selected=99"));
                Assert.That(firstLog, Does.Contain("native_input_transition_overflows=2 native_input_presented_samples=88"));
                Assert.That(firstLog, Does.Not.Contain("PRIVATE"));
                Assert.That(firstLog, Does.Not.Contain("serialNumber"));
                Assert.That(firstLog, Does.Not.Contain("device_handle="));
            });

            Assert.That(manager.CapturePostSubmitTelemetryForTesting(firstSession).Status,
                Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(calls, Is.EqualTo(1), "a stopped session cannot trigger another native read");

            Assert.That((await control.StartAsync(3)).State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
            NativeAudioTimingDiagnostics secondSession = manager.GetSessionForTesting()!;
            Assert.That(manager.CapturePostSubmitTelemetryForTesting(firstSession).Status,
                Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(calls, Is.EqualTo(1), "a stale previous session cannot sample into the next capture");
            Assert.That(manager.CapturePostSubmitTelemetryForTesting(secondSession).Status,
                Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(calls, Is.EqualTo(2));
            await control.StopAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task EmulationService_RegistersNoTargetStatusAndUnregistersOnDispose()
    {
        string root = CreateTemporaryDirectory();
        ControllerTracker tracker = new();
        NativeAudioTimingDiagnosticsControl? control = null;
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            using EmulationService service = new(
                tracker,
                enumerator: null!,
                controllerInfo: null!,
                factory: null!,
                audioEngine: null!,
                specialActions: null!,
                timingDiagnostics: router);
            control = new NativeAudioTimingDiagnosticsControl(manager);

            Assert.That((await control.StartAsync(3)).State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;
            session.PublishForTestingAt(DateTimeOffset.UnixEpoch, Stopwatch.GetTimestamp());
            Assert.Multiple(() =>
            {
                Assert.That(session.LastPostSubmitTelemetry.Status, Is.EqualTo(PostSubmitTelemetryStatus.NoTarget));
                Assert.That(session.LastPostSubmitTelemetry.Generation, Is.Not.Null);
                Assert.That(session.LastPostSubmitTelemetry.TargetCount, Is.Zero);
            });

            service.Dispose();
            session.PublishForTestingAt(DateTimeOffset.UnixEpoch.AddSeconds(5), Stopwatch.GetTimestamp());
            Assert.That(session.LastPostSubmitTelemetry.Status, Is.EqualTo(PostSubmitTelemetryStatus.NoSource));
            await control.StopAsync();
        }
        finally
        {
            if (control is not null)
            {
                await control.StopAsync();
            }

            tracker.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task LateSampleFromReplacedServiceRegistrationCannotPublishItsOldGeneration()
    {
        string root = CreateTemporaryDirectory();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        NativeAudioTimingDiagnosticsControl? control = null;
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            int calls = 0;
            using IDisposable oldRegistration = router.RegisterPostSubmitTelemetrySource(() =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("synthetic sample release timed out");
                    }
                }

                return new PostSubmitTelemetrySnapshot(
                    PostSubmitTelemetryStatus.Ok,
                    Generation: call,
                    TargetCount: 1,
                    Received: 100 + (ulong)call,
                    Selected: 90 + (ulong)call,
                    TransitionOverflows: 0,
                    PresentedSamples: 80 + (ulong)call);
            })!;

            NativeAudioTimingDiagnosticsControl activeControl = new(manager);
            control = activeControl;
            await activeControl.StartAsync(3);
            NativeAudioTimingDiagnostics firstSession = manager.GetSessionForTesting()!;
            Task<PostSubmitTelemetrySnapshot> pending = Task.Run(
                () => manager.CapturePostSubmitTelemetryForTesting(firstSession));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);

            oldRegistration.Dispose();
            using IDisposable newRegistration = router.RegisterPostSubmitTelemetrySource(() =>
                new PostSubmitTelemetrySnapshot(
                    PostSubmitTelemetryStatus.Ok,
                    Generation: 42,
                    TargetCount: 1,
                    Received: 900,
                    Selected: 800,
                    TransitionOverflows: 3,
                    PresentedSamples: 700))!;
            release.Set();

            PostSubmitTelemetrySnapshot rejected = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            PostSubmitTelemetrySnapshot current = manager.CapturePostSubmitTelemetryForTesting(firstSession);

            Assert.Multiple(() =>
            {
                Assert.That(rejected.Status, Is.EqualTo(PostSubmitTelemetryStatus.Stale));
                Assert.That(rejected.Received, Is.Null);
                Assert.That(current.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
                Assert.That(current.Generation, Is.EqualTo(42));
                Assert.That(current.Received, Is.EqualTo(900));
                Assert.That(firstSession.LastPostSubmitTelemetry, Is.EqualTo(current));
            });

            await control.StopAsync();
        }
        finally
        {
            release.Set();
            if (control is not null)
            {
                await control.StopAsync();
            }

            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task StopWaitsForAnAlreadyActiveSampleThenPreventsFurtherSourceCalls()
    {
        string root = CreateTemporaryDirectory();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        NativeAudioTimingDiagnosticsControl? control = null;
        try
        {
            NativeAudioTimingDiagnosticsManager manager = CreateManager(root);
            NativeAudioTimingDiagnostics router = NativeAudioTimingDiagnostics.CreateRouterForTesting(manager);
            int calls = 0;
            using IDisposable registration = router.RegisterPostSubmitTelemetrySource(() =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("synthetic sample release timed out");
                }

                return new PostSubmitTelemetrySnapshot(
                    PostSubmitTelemetryStatus.Ok,
                    Generation: 8,
                    TargetCount: 1,
                    Received: 80,
                    Selected: 70,
                    TransitionOverflows: 0,
                    PresentedSamples: 60);
            })!;
            NativeAudioTimingDiagnosticsControl activeControl = new(manager);
            control = activeControl;
            await activeControl.StartAsync(3);
            NativeAudioTimingDiagnostics session = manager.GetSessionForTesting()!;

            Task<PostSubmitTelemetrySnapshot> sample = Task.Run(
                () => manager.CapturePostSubmitTelemetryForTesting(session));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Task<NativeAudioTimingDiagnosticsStatus> stop = Task.Run(async () => await activeControl.StopAsync());
            await Task.Delay(25);
            Assert.That(stop.IsCompleted, Is.False, "stop must serialize with the borrowed-handle call already in flight");

            release.Set();
            PostSubmitTelemetrySnapshot completed = await sample.WaitAsync(TimeSpan.FromSeconds(2));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            PostSubmitTelemetrySnapshot afterStop = manager.CapturePostSubmitTelemetryForTesting(session);

            Assert.Multiple(() =>
            {
                Assert.That(completed.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
                Assert.That(afterStop, Is.EqualTo(completed));
                Assert.That(calls, Is.EqualTo(1));
            });
        }
        finally
        {
            release.Set();
            if (control is not null)
            {
                await control.StopAsync();
            }

            DeleteTemporaryDirectory(root);
        }
    }

    private static NativeAudioTimingDiagnosticsManager CreateManager(string root) =>
        NativeAudioTimingDiagnosticsManager.CreateForTesting(
            root,
            Stopwatch.GetTimestamp,
            () => "attempt41-test",
            () => Guid.NewGuid().ToString("N"),
            testClock: true,
            startPublisher: false);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "attempt60-post-submit-" + Guid.NewGuid().ToString("N"));
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

}
