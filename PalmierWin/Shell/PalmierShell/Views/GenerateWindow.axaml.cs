using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// The Generate composer, in a window of its own.
///
/// It used to be docked in the media library, where the panel's height cut off
/// the price and the Generate button — the two things you most need to see
/// before spending money. Results still land in the library: in-flight tiles
/// there, finished clips imported, transitions filed under their own folder.
public partial class GenerateWindow : Window {
    public GenerateWindow() => InitializeComponent();

    GeneratePanelViewModel? Vm => DataContext as GeneratePanelViewModel;

    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnPickFirstFrame(object? sender, PointerPressedEventArgs e) => ShowFramePicker(sender, first: true);
    void OnPickLastFrame(object? sender, PointerPressedEventArgs e) => ShowFramePicker(sender, first: false);

    /// Offers the library's stills for one of the reference slots.
    ///
    /// These slots are normally filled from the timeline — the clips either
    /// side of a cut or gap — and filled by hand from stills the user captured
    /// with Capture Frame, which are already in the library. Opening the OS
    /// file dialog sent them out of the app to hunt for files it had imported
    /// itself. Browsing is still there for anything genuinely external.
    void ShowFramePicker(object? sender, bool first) {
        if (Vm is not { } vm || sender is not Control anchor) return;
        var menu = new MenuFlyout();

        var stills = vm.FrameChoices;
        foreach (var item in stills) {
            var entry = new MenuItem { Header = item.Name };
            if (item.Thumbnail is { } thumb) {
                entry.Icon = new Image {
                    Source = thumb, Width = 48, Height = 27,
                    Stretch = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            string path = item.Path;
            entry.Click += (_, _) => {
                if (first) vm.SetFirstFrame(path);
                else vm.SetLastFrame(path);
            };
            menu.Items.Add(entry);
        }

        if (stills.Count == 0) {
            menu.Items.Add(new MenuItem {
                Header = "No stills in the library — use Capture Frame",
                IsEnabled = false,
            });
        }
        menu.Items.Add(new Separator());

        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += (_, _) => _ = BrowseAsync(first);
        menu.Items.Add(browse);

        menu.ShowAt(anchor);
    }

    /// Reference pickers: same library-first pattern as the frame slots.
    /// Images offer the library's stills; videos offer clips short enough to
    /// fit the endpoint's 15-second combined budget.
    void OnAddRefImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowReferencePicker(sender, video: false);
    void OnAddRefVideo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowReferencePicker(sender, video: true);

    void ShowReferencePicker(object? sender, bool video) {
        if (Vm is not { } vm || sender is not Control anchor) return;
        var menu = new MenuFlyout();
        var choices = video ? vm.VideoRefChoices : vm.FrameChoices;
        foreach (var item in choices) {
            var entry = new MenuItem { Header = item.Name };
            if (item.Thumbnail is { } thumb) {
                entry.Icon = new Image {
                    Source = thumb, Width = 48, Height = 27,
                    Stretch = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            string path = item.Path;
            entry.Click += (_, _) => {
                if (video) vm.AddReferenceVideo(path);
                else vm.AddReferenceImage(path);
            };
            menu.Items.Add(entry);
        }
        if (choices.Count == 0) {
            menu.Items.Add(new MenuItem {
                Header = video ? "No clips of 15 s or less in the library"
                               : "No stills in the library — use Capture Frame",
                IsEnabled = false,
            });
        }
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += (_, _) => _ = BrowseReferenceAsync(video);
        menu.Items.Add(browse);
        menu.ShowAt(anchor);
    }

    async Task BrowseReferenceAsync(bool video) {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = video ? "Video reference" : "Image reference",
            AllowMultiple = false,
            FileTypeFilter = [video
                ? new FilePickerFileType("Videos") { Patterns = ["*.mp4", "*.mov"] }
                : new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (video) vm.AddReferenceVideo(path);
        else vm.AddReferenceImage(path);
    }

    /// Escape hatch for a still that was never imported.
    async Task BrowseAsync(bool first) {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = first ? "First frame" : "Last frame",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
            }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (first) vm.SetFirstFrame(path);
        else vm.SetLastFrame(path);
    }
}
