using Avalonia;
using Avalonia.Controls;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

public partial class McpPanel : UserControl {
    public McpPanel() {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        (DataContext as McpPanelViewModel)?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        (DataContext as McpPanelViewModel)?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    async void OnCopySnippet(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is McpPanelViewModel vm &&
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.ConfigSnippet);
    }
}
