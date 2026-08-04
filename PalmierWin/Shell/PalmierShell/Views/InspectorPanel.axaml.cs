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
        DataContextChanged += (_, _) => {
            if (vm is not null) vm.WheelsRefreshed -= SyncWheels;
            vm = DataContext as ViewModels.InspectorViewModel;
            if (vm is not null) {
                vm.WheelsRefreshed += SyncWheels;
                SyncWheels();
            }
        };
    }

    ViewModels.InspectorViewModel? vm;

    /// Re-places the wheel handles from committed params after any refresh.
    void SyncWheels() {
        if (vm is null) return;
        var lift = vm.WheelVector(Core.ColorWheelMath.WheelKind.Lift);
        LiftWheel.WheelX = lift.X; LiftWheel.WheelY = lift.Y; LiftWheel.Offset = lift.Offset;
        var gain = vm.WheelVector(Core.ColorWheelMath.WheelKind.Gain);
        GainWheel.WheelX = gain.X; GainWheel.WheelY = gain.Y; GainWheel.Offset = gain.Offset;
        var gamma = vm.WheelVector(Core.ColorWheelMath.WheelKind.Gamma);
        GammaWheel.WheelX = gamma.X; GammaWheel.WheelY = gamma.Y; GammaWheel.Offset = gamma.Offset;
    }

    Core.ColorWheelMath.WheelKind KindOf(object? sender) =>
        ReferenceEquals(sender, LiftWheel) ? Core.ColorWheelMath.WheelKind.Lift
            : ReferenceEquals(sender, GainWheel) ? Core.ColorWheelMath.WheelKind.Gain
            : Core.ColorWheelMath.WheelKind.Gamma;

    void OnWheelChanged(object? sender, EventArgs e) {
        if (sender is ColorWheel wheel)
            vm?.PreviewWheel(KindOf(sender), wheel.WheelX, wheel.WheelY, wheel.Offset);
    }

    void OnWheelCommitted(object? sender, EventArgs e) {
        if (sender is ColorWheel wheel)
            vm?.CommitWheel(KindOf(sender), wheel.WheelX, wheel.WheelY, wheel.Offset);
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
