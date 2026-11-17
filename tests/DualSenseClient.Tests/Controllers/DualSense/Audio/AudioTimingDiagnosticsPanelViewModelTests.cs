using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.GUI.ViewModels.Pages;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public class AudioTimingDiagnosticsPanelViewModelTests
{
    [Test]
    public async Task StartAndStopCommands_UseSelectedDurationAndRefreshBoundStatus()
    {
        FakeControl control = new();
        AudioTimingDiagnosticsPanelViewModel viewModel = new(control);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.DurationOptions, Is.EqualTo(new[] { 3, 10, 30 }));
            Assert.That(viewModel.SelectedDurationMinutes, Is.EqualTo(10));
            Assert.That(viewModel.StartCaptureCommand.CanExecute(null), Is.True);
        });

        viewModel.SelectedDurationMinutes = 30;
        viewModel.StartCaptureCommand.Execute(null);
        await viewModel.StartCaptureCommand.ExecutionTask!;

        Assert.Multiple(() =>
        {
            Assert.That(control.StartedDurationMinutes, Is.EqualTo(30));
            Assert.That(viewModel.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Running));
            Assert.That(viewModel.StateText, Is.EqualTo("Running — 30:00 remaining"));
            Assert.That(viewModel.LogPathText, Is.EqualTo(control.LogPath));
            Assert.That(viewModel.IsDurationSelectionEnabled, Is.False);
            Assert.That(viewModel.StartCaptureCommand.CanExecute(null), Is.False);
            Assert.That(viewModel.StopCaptureCommand.CanExecute(null), Is.True);
        });

        viewModel.StopCaptureCommand.Execute(null);
        await viewModel.StopCaptureCommand.ExecutionTask!;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Off));
            Assert.That(viewModel.LogPathText, Is.EqualTo(control.LogPath));
            Assert.That(viewModel.StartCaptureCommand.CanExecute(null), Is.True);
        });
    }

    [Test]
    public void RefreshStatus_ShowsExpiredAndActionableStartErrorStates()
    {
        FakeControl control = new();
        AudioTimingDiagnosticsPanelViewModel viewModel = new(control);
        control.Status = new(
            NativeAudioTimingDiagnosticsState.Expired,
            control.LogPath,
            TimeSpan.Zero,
            null);

        viewModel.RefreshStatus();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.State, Is.EqualTo(NativeAudioTimingDiagnosticsState.Expired));
            Assert.That(viewModel.StateText, Is.EqualTo("Expired"));
            Assert.That(viewModel.LogPathText, Is.EqualTo(control.LogPath));
            Assert.That(viewModel.StartCaptureCommand.CanExecute(null), Is.True);
        });

        const string message = "Check that the stage Logs folder is writable.";
        control.Status = new(
            NativeAudioTimingDiagnosticsState.Error,
            control.LogPath,
            TimeSpan.Zero,
            message);
        viewModel.RefreshStatus();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.StateText, Is.EqualTo("Error"));
            Assert.That(viewModel.ErrorMessage, Is.EqualTo(message));
            Assert.That(viewModel.LogPathText, Is.EqualTo(control.LogPath));
            Assert.That(viewModel.StartCaptureCommand.CanExecute(null), Is.True);
        });
    }

    private sealed class FakeControl : INativeAudioTimingDiagnosticsControl
    {
        public string LogPath { get; } = Path.Combine(Path.GetTempPath(), "attempt41", "Logs", "timing.log");
        public int? StartedDurationMinutes { get; private set; }
        public NativeAudioTimingDiagnosticsStatus Status { get; set; } = new(
            NativeAudioTimingDiagnosticsState.Off,
            null,
            TimeSpan.Zero,
            null);

        public NativeAudioTimingDiagnosticsStatus GetStatus() => Status;

        public Task<NativeAudioTimingDiagnosticsStatus> StartAsync(int durationMinutes)
        {
            StartedDurationMinutes = durationMinutes;
            Status = new(
                NativeAudioTimingDiagnosticsState.Running,
                LogPath,
                TimeSpan.FromMinutes(durationMinutes),
                null);
            return Task.FromResult(Status);
        }

        public Task<NativeAudioTimingDiagnosticsStatus> StopAsync()
        {
            Status = new(NativeAudioTimingDiagnosticsState.Off, LogPath, TimeSpan.Zero, null);
            return Task.FromResult(Status);
        }
    }
}
