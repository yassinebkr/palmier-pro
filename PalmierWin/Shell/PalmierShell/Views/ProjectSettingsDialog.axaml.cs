using Avalonia.Controls;
using Avalonia.Interactivity;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Project-level render size: a preset aspect or a custom width × height.
/// Applying retargets the preview canvas, frame capture, and exports.
public partial class ProjectSettingsDialog : Window {
    static readonly (int Width, int Height)[] Presets =
        [(1920, 1080), (1080, 1920), (1080, 1080), (1080, 1350), (1440, 1080)];
    const int CustomIndex = 5;

    readonly MainViewModel main;

    public ProjectSettingsDialog() : this(null!) { }  // XAML designer only

    public ProjectSettingsDialog(MainViewModel main) {
        this.main = main;
        InitializeComponent();
        if (main is null) return;
        int match = Array.FindIndex(Presets,
            p => p == (main.ProjectRenderWidth, main.ProjectRenderHeight));
        PresetBox.SelectedIndex = match >= 0 ? match : CustomIndex;
        WidthBox.Text = main.ProjectRenderWidth.ToString();
        HeightBox.Text = main.ProjectRenderHeight.ToString();
        UpdateCustomState();
    }

    bool IsCustom => PresetBox.SelectedIndex < 0 || PresetBox.SelectedIndex == CustomIndex;

    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnPresetChanged(object? sender, SelectionChangedEventArgs e) {
        // SelectionChanged fires while InitializeComponent is still parsing;
        // named controls further down the XAML are not assigned yet.
        if (WidthBox is null || HeightBox is null || main is null) return;
        if (!IsCustom) {
            var (width, height) = Presets[PresetBox.SelectedIndex];
            WidthBox.Text = width.ToString();
            HeightBox.Text = height.ToString();
        }
        UpdateCustomState();
    }

    void UpdateCustomState() {
        WidthBox.IsEnabled = IsCustom;
        HeightBox.IsEnabled = IsCustom;
        StatusText.Text = "";
    }

    void OnApply(object? sender, RoutedEventArgs e) {
        if (!int.TryParse(WidthBox.Text?.Trim(), out int width) ||
            !int.TryParse(HeightBox.Text?.Trim(), out int height)) {
            StatusText.Text = "Width and height must be whole numbers.";
            return;
        }
        if (width % 2 != 0 || height % 2 != 0 || width < 16 || height < 16 ||
            width > 7680 || height > 7680) {
            StatusText.Text = "Width and height must be even numbers between 16 and 7680.";
            return;
        }
        if (main.SetProjectRenderSize(width, height)) Close();
        else StatusText.Text = "The core rejected that size.";
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
