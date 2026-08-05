using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using PalmierShell.Core;

namespace PalmierShell.Views;

/// The accent palette as clickable swatches, shared by Settings and the
/// first-run welcome dialog so both offer the same choices in the same order.
public sealed class AccentSwatchPicker : ItemsControl {
    // Avalonia looks control themes up by exact type; inherit ItemsControl's.
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    readonly List<Button> swatches = new();
    string selectedHex = Accent.DefaultHex;

    public event Action<string>? SelectionChanged;

    public string SelectedHex {
        get => selectedHex;
        set {
            selectedHex = value;
            MarkSelected();
        }
    }

    public AccentSwatchPicker() {
        ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        foreach (var (name, hex) in Accent.Choices) {
            var button = new Button {
                Width = 56, Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse(hex)),
                Tag = hex,
            };
            ToolTip.SetTip(button, name);
            button.Click += (_, _) => {
                SelectedHex = hex;
                SelectionChanged?.Invoke(hex);
            };
            swatches.Add(button);
        }
        ItemsSource = swatches;
        MarkSelected();
    }

    void MarkSelected() {
        foreach (var button in swatches) {
            bool selected = string.Equals((string)button.Tag!, selectedHex, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = Brushes.White;
            button.BorderThickness = new Thickness(selected ? 2 : 0);
        }
    }
}
