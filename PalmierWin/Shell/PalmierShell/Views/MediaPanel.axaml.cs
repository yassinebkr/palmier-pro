using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

public partial class MediaPanel : UserControl {
    /// Drag payload format: the media item's absolute file path.
    public const string MediaPathFormat = "palmier/media-path";

    /// Hover-scrub position bar width across the 96 px thumbnail.
    /// fraction × thumb width — the bar tracks resizable thumbnails.
    public static readonly Avalonia.Data.Converters.IMultiValueConverter HoverBarWidth =
        new Avalonia.Data.Converters.FuncMultiValueConverter<double, double>(values => {
            var list = values.ToList();
            return list.Count == 2 ? Math.Clamp(list[0], 0, 1) * list[1] : 0;
        });

    Point pressPoint;
    MediaItemViewModel? pressedItem;

    public MediaPanel() => InitializeComponent();

    void OnItemDoubleTapped(object? sender, TappedEventArgs e) {
        if (DataContext is MediaPanelViewModel vm &&
            (sender as Control)?.DataContext is MediaItemViewModel item)
            vm.AddToTimelineCommand.Execute(item);
    }

    void OnItemPointerPressed(object? sender, PointerPressedEventArgs e) {
        pressedItem = (sender as Control)?.DataContext as MediaItemViewModel;
        pressPoint = e.GetPosition(this);
        if (Vm is { } vm && pressedItem is { } item) vm.SelectedItem = item;
    }

    async void OnItemPointerMoved(object? sender, PointerEventArgs e) {
        if (pressedItem is not { } item) {
            // No button down: hover scrub across the thumbnail.
            if (sender is Control { DataContext: MediaItemViewModel hovered } tile) {
                // Normalised against the tile's real width: thumbnails resize.
                double x = e.GetPosition(tile).X;
                hovered.HoverScrub(Math.Clamp(x / Math.Max(1, tile.Bounds.Width), 0, 1));
            }
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { pressedItem = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - pressPoint.X) < 6 && Math.Abs(p.Y - pressPoint.Y) < 6) return;
        pressedItem = null;
        var data = new DataObject();
        data.Set(MediaPathFormat, item.Path);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e) => pressedItem = null;

    void OnFolderHeaderTapped(object? sender, TappedEventArgs e) {
        if ((sender as Control)?.DataContext is MediaFolderGroup { IsRenaming: false } group)
            group.IsExpanded = !group.IsExpanded;
    }

    MediaPanelViewModel? Vm => DataContext as MediaPanelViewModel;

    void OnRenameFolder(object? sender, RoutedEventArgs e) {
        if ((sender as Control)?.DataContext is MediaFolderGroup group)
            group.IsRenaming = true;
    }

    void OnDeleteFolder(object? sender, RoutedEventArgs e) {
        if ((sender as Control)?.DataContext is MediaFolderGroup group)
            Vm?.DeleteFolder(group.Name);
    }

    void OnRenameKeyDown(object? sender, KeyEventArgs e) {
        if ((sender as Control)?.DataContext is not MediaFolderGroup group) return;
        if (e.Key is Key.Enter or Key.Return) {
            CommitRename(group, (sender as TextBox)?.Text ?? group.EditName);
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            group.IsRenaming = false;
            e.Handled = true;
        }
    }

    void OnRenameLostFocus(object? sender, RoutedEventArgs e) {
        if ((sender as Control)?.DataContext is MediaFolderGroup { IsRenaming: true } group)
            CommitRename(group, (sender as TextBox)?.Text ?? group.EditName);
    }

    void CommitRename(MediaFolderGroup group, string newName) {
        group.IsRenaming = false;
        Vm?.RenameFolder(group.Name, newName);
    }

    // Right-click on an item: timeline + move-to-folder actions, built here
    // because the folder list is dynamic.
    void OnItemContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not MediaItemViewModel item) return;
        var flyout = new MenuFlyout();
        var add = new MenuItem { Header = "Add to Timeline" };
        add.Click += (_, _) => vm.AddToTimelineCommand.Execute(item);
        flyout.Items.Add(add);
        var open = new MenuItem { Header = "Open in Viewer" };
        open.Click += (_, _) => vm.OpenInViewerCommand.Execute(item);
        flyout.Items.Add(open);
        var move = new MenuItem { Header = "Move to Folder" };
        foreach (var folder in vm.Folders.Where(f => f != item.Folder)) {
            var target = new MenuItem { Header = folder };
            target.Click += (_, _) => vm.MoveItem(item, folder);
            move.Items.Add(target);
        }
        move.IsEnabled = move.Items.Count > 0;
        flyout.Items.Add(move);
        flyout.ShowAt((Control)sender!, true);
        e.Handled = true;
    }

    void OnItemPointerExited(object? sender, PointerEventArgs e) {
        if ((sender as Control)?.DataContext is MediaItemViewModel item)
            item.EndHoverScrub();
    }

    void OnDismissJob(object? sender, RoutedEventArgs e) {
        if ((sender as Control)?.DataContext is GenerationJobViewModel job)
            Vm?.Generate.DismissJobCommand.Execute(job);
    }

}
