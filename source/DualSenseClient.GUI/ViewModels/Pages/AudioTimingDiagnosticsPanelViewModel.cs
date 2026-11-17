using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualSenseClient.Controllers.DualSense.Audio;

namespace DualSenseClient.GUI.ViewModels.Pages;

public partial class AudioTimingDiagnosticsPanelViewModel : ObservableObject
{
    private readonly INativeAudioTimingDiagnosticsControl _control;

    public ObservableCollection<int> DurationOptions { get; } = [3, 10, 30];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDurationSelectionEnabled))]
    private int selectedDurationMinutes = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(IsDurationSelectionEnabled))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(IsDurationSelectionEnabled))]
    private NativeAudioTimingDiagnosticsState state = NativeAudioTimingDiagnosticsState.Off;

    [ObservableProperty]
    private string stateText = "Off";

    [ObservableProperty]
    private string logPathText = "No local timing log has been created yet.";

    [ObservableProperty]
    private string? errorMessage;

    public bool CanStart => !IsBusy
        && State is not (NativeAudioTimingDiagnosticsState.Running
            or NativeAudioTimingDiagnosticsState.Starting
            or NativeAudioTimingDiagnosticsState.Stopping);

    public bool CanStop => !IsBusy && State == NativeAudioTimingDiagnosticsState.Running;

    public bool IsDurationSelectionEnabled => !IsBusy
        && State is not (NativeAudioTimingDiagnosticsState.Running
            or NativeAudioTimingDiagnosticsState.Starting
            or NativeAudioTimingDiagnosticsState.Stopping);

    public AudioTimingDiagnosticsPanelViewModel(INativeAudioTimingDiagnosticsControl control)
    {
        _control = control;
        RefreshStatus();
    }

    public void RefreshStatus() => ApplyStatus(_control.GetStatus());

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartCaptureAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        RefreshCommandStates();
        try
        {
            ApplyStatus(await _control.StartAsync(SelectedDurationMinutes));
        }
        catch
        {
            ApplyStatus(new(
                NativeAudioTimingDiagnosticsState.Error,
                null,
                TimeSpan.Zero,
                "Could not start diagnostics. Check that the stage Logs folder is writable and has free disk space."));
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopCaptureAsync()
    {
        IsBusy = true;
        RefreshCommandStates();
        try
        {
            ApplyStatus(await _control.StopAsync());
        }
        catch
        {
            ApplyStatus(new(
                NativeAudioTimingDiagnosticsState.Error,
                LogPathText,
                TimeSpan.Zero,
                "Could not stop the diagnostics capture cleanly. The app will finish closing the local log in the background."));
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void ApplyStatus(NativeAudioTimingDiagnosticsStatus status)
    {
        State = status.State;
        StateText = status.State switch
        {
            NativeAudioTimingDiagnosticsState.Off => "Off",
            NativeAudioTimingDiagnosticsState.Starting => "Starting…",
            NativeAudioTimingDiagnosticsState.Running => $"Running — {FormatRemaining(status.Remaining)} remaining",
            NativeAudioTimingDiagnosticsState.Stopping => "Stopping and flushing the final summary…",
            NativeAudioTimingDiagnosticsState.Expired => "Expired",
            NativeAudioTimingDiagnosticsState.Error => "Error",
            _ => "Off"
        };
        LogPathText = status.LogPath ?? "No local timing log has been created yet.";
        ErrorMessage = status.ErrorMessage;
        RefreshCommandStates();
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private void RefreshCommandStates()
    {
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }
}
