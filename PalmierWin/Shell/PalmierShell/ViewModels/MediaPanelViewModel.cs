using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

public sealed partial class MediaItemViewModel : ObservableObject {
    public string Path { get; }
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    public int TotalFrames { get; }
    public string DurationText { get; }
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";

    [ObservableProperty] Bitmap? thumbnail;

    /// Library folder this item lives in (managed in the panel, not on disk).
    [ObservableProperty] string folder = MediaPanelViewModel.DefaultFolder;

    /// Scrub position under the pointer, 0…1, and whether the pointer is on
    /// the tile — the thumbnail draws a position bar from these.
    [ObservableProperty] double hoverFraction;
    [ObservableProperty] bool hovering;

    Bitmap[]? hoverStrip;
    bool hoverStripLoading;

    /// Swaps the thumbnail to the frame under the pointer (0…1 across the
    /// tile). The 8-tile strip loads lazily on first hover.
    public void HoverScrub(double fraction) {
        HoverFraction = fraction;
        Hovering = true;
        if (hoverStrip is null) {
            if (Width > 0 && !hoverStripLoading) {
                hoverStripLoading = true;
                _ = Task.Run(() => {
                    var result = CoreApi.GetThumbnails(Path, 8);
                    if (result is null) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        var tiles = new Bitmap[result.Value.Count];
                        for (int i = 0; i < tiles.Length; i++)
                            tiles[i] = ThumbnailBitmaps.FromTiles(result.Value.Tiles, i);
                        hoverStrip = tiles;
                    });
                });
            }
            return;
        }
        int index = Math.Clamp((int)(fraction * hoverStrip.Length), 0, hoverStrip.Length - 1);
        Thumbnail = hoverStrip[index];
    }

    public void EndHoverScrub() {
        Hovering = false;
        HoverFraction = 0;
        if (hoverStrip is { Length: > 0 }) Thumbnail = hoverStrip[0];
    }

    public MediaItemViewModel(string path, CoreApi.MediaProbe probe) {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Width = probe.Width;
        Height = probe.Height;
        Fps = probe.Fps;
        TotalFrames = probe.TotalFrames;
        double seconds = probe.Fps > 0 && probe.TotalFrames > 0 ? probe.TotalFrames / probe.Fps : 0;
        DurationText = TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    }
}

public sealed partial class MediaFolderGroup : ObservableObject {
    public string Name { get; }
    public List<MediaItemViewModel> Items { get; }
    [ObservableProperty] bool isExpanded = true;
    [ObservableProperty] bool isRenaming;
    [ObservableProperty] string editName;

    public MediaFolderGroup(string name, List<MediaItemViewModel> items, bool expanded) {
        Name = name;
        Items = items;
        isExpanded = expanded;
        editName = name;
    }

    public string CountText => $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}";
}

public sealed partial class MediaPanelViewModel : ObservableObject {
    public ObservableCollection<MediaItemViewModel> Items { get; } = new();

    [ObservableProperty] string searchText = "";
    [ObservableProperty] MediaItemViewModel? selectedItem;

    /// Grid of thumbnails or compact rows, like upstream's media panel views.
    [ObservableProperty] bool listView;

    [RelayCommand] void ShowGrid() => ListView = false;
    [RelayCommand] void ShowList() => ListView = true;
    [RelayCommand] void ToggleView() => ListView = !ListView;

    public enum MediaSort { Added, Name, Duration, Type }

    /// Library order. Added = import order, the panel's historical behavior.
    [ObservableProperty] MediaSort sortMode = MediaSort.Added;

    partial void OnSortModeChanged(MediaSort value) => NotifyGroups();

    [RelayCommand] void SortBy(string mode) =>
        SortMode = Enum.Parse<MediaSort>(mode);

    IEnumerable<MediaItemViewModel> Sorted(IEnumerable<MediaItemViewModel> items) => SortMode switch {
        MediaSort.Name => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        MediaSort.Duration => items.OrderByDescending(i => i.Fps > 0 ? i.TotalFrames / i.Fps : 0),
        MediaSort.Type => items.OrderBy(i => System.IO.Path.GetExtension(i.Path),
                                        StringComparer.OrdinalIgnoreCase)
                               .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        _ => items,
    };

    /// Grid tile width; the thumbnail scales with it at 16:9.
    [ObservableProperty] double tileWidth = 104;

    public double ThumbWidth => TileWidth - 8;
    public double ThumbHeight => Math.Round((TileWidth - 8) * 9 / 16);

    partial void OnTileWidthChanged(double value) {
        OnPropertyChanged(nameof(ThumbWidth));
        OnPropertyChanged(nameof(ThumbHeight));
    }

    [RelayCommand] void SetThumbSize(string size) =>
        TileWidth = size switch { "Small" => 84, "Large" => 140, _ => 104 };

    /// The Generate panel imports its finished clips straight back here.
    public GeneratePanelViewModel Generate { get; }

    public MediaPanelViewModel() {
        Generate = new GeneratePanelViewModel(ImportFileAsync, () => Items);
    }

    /// Where generated transitions are filed.
    public const string TransitionsFolder = "Transitions";

    public const string DefaultFolder = "Library";

    readonly Dictionary<string, bool> folderExpanded = new();
    readonly List<string> folders = [DefaultFolder];

    public IReadOnlyList<string> Folders => folders;

    public IEnumerable<MediaItemViewModel> FilteredItems =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Items
            : Items.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// Managed folder groups in creation order (empty folders included so a
    /// fresh folder is visible), preserving expand/collapse and rename state.
    public IEnumerable<MediaFolderGroup> FolderGroups {
        get {
            var visible = Sorted(FilteredItems).ToList();
            foreach (var name in folders) {
                var group = new MediaFolderGroup(name, visible.Where(i => i.Folder == name).ToList(),
                    folderExpanded.TryGetValue(name, out bool open) ? open : true);
                group.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(MediaFolderGroup.IsExpanded))
                        folderExpanded[name] = group.IsExpanded;
                };
                yield return group;
            }
        }
    }

    void NotifyGroups() {
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(FolderGroups));
        OnPropertyChanged(nameof(Folders));
    }

    void EnsureFolder(string name) {
        if (!folders.Contains(name)) folders.Add(name);
    }

    [RelayCommand]
    void CreateFolder() {
        string name = "New Folder";
        for (int n = 2; folders.Contains(name); n++) name = $"New Folder {n}";
        folders.Add(name);
        NotifyGroups();
    }

    /// Renames a folder; rejects empty and duplicate names. Returns success.
    public bool RenameFolder(string oldName, string newName) {
        string trimmed = newName.Trim();
        if (trimmed.Length == 0 || (trimmed != oldName && folders.Contains(trimmed))) return false;
        int index = folders.IndexOf(oldName);
        if (index < 0) return false;
        folders[index] = trimmed;
        if (folderExpanded.Remove(oldName, out bool open)) folderExpanded[trimmed] = open;
        foreach (var item in Items.Where(i => i.Folder == oldName)) item.Folder = trimmed;
        NotifyGroups();
        return true;
    }

    /// Deletes a folder; its items move to Library. Library itself stays.
    public void DeleteFolder(string name) {
        if (name == DefaultFolder) return;
        folders.Remove(name);
        folderExpanded.Remove(name);
        EnsureFolder(DefaultFolder);
        foreach (var item in Items.Where(i => i.Folder == name)) item.Folder = DefaultFolder;
        NotifyGroups();
    }

    public void MoveItem(MediaItemViewModel item, string folder) {
        EnsureFolder(folder);
        item.Folder = folder;
        NotifyGroups();
    }

    /// Raised when the user requests adding a media item to the timeline.
    public event Action<MediaItemViewModel>? AddToTimelineRequested;

    /// Raised for "Remove from Library" (context menu / Delete key). The
    /// owner confirms and removes any timeline clips first.
    public event Action<MediaItemViewModel>? RemoveItemRequested;
    public void RequestRemove(MediaItemViewModel item) => RemoveItemRequested?.Invoke(item);

    /// Drops the item from the library. Not undoable — neither is import.
    public void RemoveItem(MediaItemViewModel item) {
        if (!Items.Remove(item)) return;
        if (SelectedItem == item) SelectedItem = null;
        NotifyGroups();
    }

    /// Supplied by the view so the command can open the file picker.
    public Func<IStorageProvider>? StorageProviderSource { get; set; }

    partial void OnSearchTextChanged(string value) { OnPropertyChanged(nameof(FilteredItems)); OnPropertyChanged(nameof(FolderGroups)); }

    [RelayCommand]
    async Task ImportAsync() {
        var provider = StorageProviderSource?.Invoke();
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import media",
            AllowMultiple = true,
            FileTypeFilter = [
                new FilePickerFileType("Video") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.m4v"] },
                new FilePickerFileType("Audio") { Patterns = ["*.m4a", "*.mp3", "*.wav", "*.aac"] },
            ],
        });
        foreach (var file in files) {
            string? path = file.TryGetLocalPath();
            if (path is null) continue;
            await ImportFileAsync(path);
        }
    }

    /// Probes + adds one file, loading its thumbnail off the UI thread.
    /// The library as saved: paths and their folders.
    public List<SavedMedia> SaveLibrary() =>
        Items.Select(i => new SavedMedia(i.Path, i.Folder)).ToList();

    /// Rebuilds the library from a saved project. Files that have moved or
    /// been deleted are skipped and reported, never silently dropped.
    public async Task<List<string>> RestoreLibraryAsync(
        IReadOnlyList<SavedMedia> media, IReadOnlyList<string> savedFolders) {
        Items.Clear();
        folders.Clear();
        folders.Add(DefaultFolder);
        foreach (var name in savedFolders) EnsureFolder(name);

        var missing = new List<string>();
        foreach (var entry in media) {
            if (!File.Exists(entry.Path)) {
                missing.Add(entry.Path);
                continue;
            }
            await ImportFileAsync(entry.Path);
            if (Items.LastOrDefault() is { } added) {
                EnsureFolder(entry.Folder);
                added.Folder = entry.Folder;
            }
        }
        NotifyGroups();
        return missing;
    }

    /// Imports into a named library folder, creating it if needed. Generated
    /// transitions land in one of their own so they do not scatter through the
    /// footage they were made from.
    public async Task ImportFileAsync(string path, string? folder) {
        await ImportFileAsync(path);
        if (folder is null or "" || Items.LastOrDefault() is not { } added ||
            added.Path != path) return;
        EnsureFolder(folder);
        added.Folder = folder;
        NotifyGroups();
    }

    public async Task ImportFileAsync(string path) {
        if (Items.Any(i => i.Path == path)) return;
        var probe = await Task.Run(() => CoreApi.ProbeMedia(path));
        if (probe is null) return;
        var item = new MediaItemViewModel(path, probe.Value);
        Items.Add(item);
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(FolderGroups));
        if (probe.Value.Width > 0) {
            var thumb = await Task.Run(() => CoreApi.GetThumbnails(path, 1));
            if (thumb is not null)
                item.Thumbnail = ThumbnailBitmaps.FromTiles(thumb.Value.Tiles, 0);
        }
    }

    [RelayCommand]
    void AddToTimeline(MediaItemViewModel? item) {
        if (item is not null) AddToTimelineRequested?.Invoke(item);
    }

    /// Raised when the user wants a library file in the preview's source
    /// monitor rather than on the timeline.
    public event Action<MediaItemViewModel>? OpenInViewerRequested;

    [RelayCommand]
    void OpenInViewer(MediaItemViewModel? item) {
        if (item is null) return;
        SelectedItem = item;
        OpenInViewerRequested?.Invoke(item);
    }
}
