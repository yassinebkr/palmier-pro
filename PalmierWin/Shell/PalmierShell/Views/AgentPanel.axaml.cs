using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

public partial class AgentPanel : UserControl {
    /// Green dot for a successful tool call, red for a rejected one.
    public static readonly IValueConverter ToolDotBrush = new FuncValueConverter<bool, IBrush>(
        ok => new SolidColorBrush(Color.Parse(ok ? "#4FB85F" : "#E54F4F")));

    public AgentPanel() {
        InitializeComponent();
        if (this.FindControl<ScrollViewer>("Scroll") is { } scroll)
            scroll.PropertyChanged += (_, e) => {
                if (e.Property == ScrollViewer.ExtentProperty) scroll.ScrollToEnd();
            };
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e) {
        if (DataContext is not AgentViewModel vm) return;
        if (e.Key is Key.Enter or Key.Return) {
            // Enter completes the highlighted mention when the popup is open.
            if (vm.MentionOpen && vm.MentionCandidates.Count > 0) {
                vm.ApplyMention(vm.MentionCandidates[0]);
            } else {
                vm.SendCommand.Execute(null);
            }
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            if (vm.MentionOpen) vm.MentionOpen = false;
            else if (vm.PermissionText is not null) vm.DenyPermissionCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnMentionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is AgentViewModel vm && (sender as Button)?.Content is string name)
            vm.ApplyMention(name);
    }

    /// The gear is a shortcut to the AI tab — Settings owns provider and key.
    void OnOpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is MainWindow window) window.ShowSettings(tabIndex: 1);
    }

    void OnToggleToolDetail(object? sender, TappedEventArgs e) {
        if ((sender as Control)?.DataContext is AgentEntryViewModel entry && entry.HasDetail)
            entry.ToggleExpandedCommand.Execute(null);
    }

    async void OnCopyEntry(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if ((sender as Control)?.DataContext is AgentEntryViewModel entry) await CopyAsync(entry.Text);
    }

    async void OnCopyConversation(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is AgentViewModel vm) await CopyAsync(vm.TranscriptText());
    }

    async Task CopyAsync(string text) {
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
    }
}
