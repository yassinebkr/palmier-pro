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
            if (vm is not null) {
                vm.WheelsRefreshed -= SyncWheels;
                vm.CurvesRefreshed -= SyncCurves;
            }
            vm = DataContext as ViewModels.InspectorViewModel;
            if (vm is not null) {
                vm.WheelsRefreshed += SyncWheels;
                vm.CurvesRefreshed += SyncCurves;
                SyncWheels();
                SyncCurves();
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

    /// Re-loads each curve editor's points from the committed models after
    /// any refresh. The editors own their in-flight drag, so this never
    /// writes back.
    void SyncCurves() {
        if (vm is null) return;
        GradeEditor.Points = vm.CurveGrade.Points((Core.GradeChannel)GradeEditor.Channel);
        HueEditor.Points = vm.HueCurveSet.Points((Core.HueChannel)HueEditor.Channel);
    }

    void OnGradeChannelTab(object? sender, RoutedEventArgs e) {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int channel)) return;
        GradeEditor.Channel = channel;
        if (vm is not null)
            GradeEditor.Points = vm.CurveGrade.Points((Core.GradeChannel)channel);
    }

    void OnHueChannelTab(object? sender, RoutedEventArgs e) {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int channel)) return;
        HueEditor.Channel = channel;
        if (vm is not null)
            HueEditor.Points = vm.HueCurveSet.Points((Core.HueChannel)channel);
    }

    void OnGradeCurveChanged(object? sender, CurvePointsEventArgs e) {
        if (sender is CurveEditor editor)
            vm?.PreviewCurve((Core.GradeChannel)editor.Channel, e.Points);
    }

    void OnGradeCurveCommitted(object? sender, CurvePointsEventArgs e) {
        if (sender is CurveEditor editor)
            vm?.CommitCurve((Core.GradeChannel)editor.Channel, e.Points);
    }

    void OnHueCurveChanged(object? sender, CurvePointsEventArgs e) {
        if (sender is CurveEditor editor)
            vm?.PreviewHueCurve((Core.HueChannel)editor.Channel, e.Points);
    }

    void OnHueCurveCommitted(object? sender, CurvePointsEventArgs e) {
        if (sender is CurveEditor editor)
            vm?.CommitHueCurve((Core.HueChannel)editor.Channel, e.Points);
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
