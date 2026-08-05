using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PalmierShell.Core;

namespace PalmierShell.Views;

public partial class ReportProblemDialog : Window {
    public ReportProblemDialog() => InitializeComponent();

    public static Task ShowAsync(Window owner) =>
        new ReportProblemDialog().ShowDialog(owner);

    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e) => BeginMoveDrag(e);

    async void OnSend(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        SendButton.IsEnabled = false;
        StatusText.Text = "Collecting logs…";
        string zipPath = Path.Combine(Path.GetTempPath(), $"palmierwin-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try {
            int count = await Task.Run(() => LogShare.CollectLogs(zipPath, NoteBox.Text?.Trim()));
            if (count == 0) {
                StatusText.Text = "No logs found yet — nothing to send.";
                SendButton.IsEnabled = true;
                return;
            }
            StatusText.Text = $"Uploading {count} log file(s)…";
            string url = await LogShare.UploadAsync(zipPath);
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(url);
            StatusText.Text = $"Uploaded. The link is on your clipboard — send it with your report:\n{url}";
            SessionLog.Event("report", $"logs shared ({count} files)");
            SendButton.IsVisible = false;
        } catch (Exception ex) {
            StatusText.Text = $"Upload failed ({ex.Message}). Your logs are still on this machine —";
            OpenButton.IsVisible = true;
            SendButton.IsEnabled = true;
        } finally {
            try { File.Delete(zipPath); } catch { }
        }
    }

    void OnOpenFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PalmierPro", "logs");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
