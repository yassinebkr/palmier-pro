using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace PalmierShell.Views;

public partial class InspectorPanel : UserControl {
    public InspectorPanel() {
        InitializeComponent();
        // TextBox bindings commit on focus loss; make Enter commit too.
        AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
    }

    void OnFieldKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key is Key.Enter or Key.Return && e.Source is TextBox box) {
            var binding = Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty);
            binding?.UpdateSource();
            e.Handled = true;
        }
    }

    async void OnChooseLut(object? sender, RoutedEventArgs e) {
        if (DataContext is not ViewModels.InspectorViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions {
            Title = "Choose a LUT",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Cube LUT") {
                Patterns = ["*.cube"],
            }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.LutPath = path;
    }
}
