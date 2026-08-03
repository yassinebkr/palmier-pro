using Avalonia;
using Avalonia.Controls;

namespace PalmierShell.Views;

/// Arranges its single child centered at the project's aspect ratio. Used to
/// aspect-fit the preview HWND inside the black canvas without touching the
/// Vulkan blit.
public sealed class AspectFitPanel : Panel {
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<AspectFitPanel, double>(nameof(AspectRatio), 16.0 / 9.0);

    public double AspectRatio {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        double ratio = AspectRatio;
        double w = finalSize.Width, h = finalSize.Height;
        if (w / Math.Max(1, h) > ratio) w = h * ratio;
        else h = w / ratio;
        var rect = new Rect((finalSize.Width - w) / 2, (finalSize.Height - h) / 2, w, h);
        foreach (var child in Children) child.Arrange(rect);
        return finalSize;
    }
}
