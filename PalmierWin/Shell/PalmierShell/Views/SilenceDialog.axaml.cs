using Avalonia.Controls;
using Avalonia.Interactivity;
using PalmierShell.Core;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Detects the silent spans of one clip's audio and offers to cut them out.
/// Detection decodes the whole file, so it runs on a background task; Apply
/// commits through the view model as one undoable ripple delete.
public partial class SilenceDialog : Window {
    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    readonly MainViewModel main;
    readonly string clipId;
    readonly string mediaPath;
    List<SilentRange>? detected;

    public SilenceDialog() : this(null!, null!, null!) { }  // XAML designer only

    public SilenceDialog(MainViewModel main, string clipId, string mediaPath) {
        this.main = main;
        this.clipId = clipId;
        this.mediaPath = mediaPath;
        InitializeComponent();
        if (main is null) return;
        StatusText.Text = Path.GetFileName(mediaPath);
    }

    async void OnDetect(object? sender, RoutedEventArgs e) {
        if (!double.TryParse(ThresholdBox.Text, out double thresholdDb) ||
            thresholdDb is < -96 or > 0 ||
            !int.TryParse(MinSilenceBox.Text, out int minSilenceMs) || minSilenceMs < 0 ||
            !int.TryParse(PaddingBox.Text, out int paddingMs) || paddingMs < 0) {
            StatusText.Text = "Check the parameters — threshold −96…0 dB, durations 0 ms or more.";
            return;
        }
        SetBusy(true);
        StatusText.Text = "Analyzing audio…";
        detected = await Task.Run(() =>
            CoreApi.DetectSilence(mediaPath, thresholdDb, minSilenceMs, paddingMs));
        SetBusy(false);
        if (detected is null) {
            StatusText.Text = "Could not decode this clip's audio.";
        } else if (detected.Count == 0) {
            StatusText.Text = "No silence found with these settings.";
        } else {
            double seconds = detected.Sum(r => r.DurationMs) / 1000.0;
            StatusText.Text = $"Found {detected.Count} silent range(s), {seconds:0.0}s total.";
            ApplyButton.IsEnabled = true;
        }
    }

    void OnApply(object? sender, RoutedEventArgs e) {
        if (detected is { Count: > 0 }) main.ApplySilenceRemoval(clipId, detected);
        Close();
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close();

    void SetBusy(bool busy) {
        DetectButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = false;
        CancelButton.IsEnabled = !busy;
        ThresholdBox.IsEnabled = !busy;
        MinSilenceBox.IsEnabled = !busy;
        PaddingBox.IsEnabled = !busy;
    }
}
