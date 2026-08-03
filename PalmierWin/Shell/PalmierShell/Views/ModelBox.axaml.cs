using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace PalmierShell.Views;

/// Model id field: click to browse the list, type to search it, or paste an id
/// this build has never heard of. One control covers all three because model
/// catalogues are long, change often, and outrun any shipped list.
public partial class ModelBox : UserControl {
    /// Long catalogues (OpenRouter ships ~340) are cut to the newest few until
    /// the user searches, so opening the list is not a wall of text.
    const int BrowseLimit = 40;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ModelBox, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ModelBox, string?>(
            nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<ModelBox, string?>(nameof(Watermark));

    public IEnumerable? ItemsSource {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    bool choosing;
    /// A flyout declared inside Button.Flyout is outside the control's name
    /// scope, so x:Name generates no field — hold it here instead.
    readonly FlyoutBase? flyout;

    public ModelBox() {
        InitializeComponent();
        PropertyChanged += (_, e) => {
            if (e.Property == TextProperty) ShowCurrent();
            else if (e.Property == ItemsSourceProperty) Subscribe(e.OldValue as IEnumerable);
            else if (e.Property == WatermarkProperty) Search.Watermark = Watermark;
        };
        flyout = Trigger.Flyout;
        if (flyout is not null) flyout.Opened += (_, _) => OnOpened();
        ShowCurrent();
    }

    /// A live list (arriving from the provider) must refresh an open popup.
    void Subscribe(IEnumerable? old) {
        if (old is INotifyCollectionChanged previous) previous.CollectionChanged -= OnItemsChanged;
        if (ItemsSource is INotifyCollectionChanged current) current.CollectionChanged += OnItemsChanged;
        Populate(Search.Text ?? "");
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Populate(Search.Text ?? "");

    void ShowCurrent() {
        Current.Text = string.IsNullOrEmpty(Text) ? Watermark ?? "Choose a model" : Text;
    }

    void OnOpened() {
        Search.Text = "";
        Populate("");
        // Focus has to wait for the popup to finish opening.
        Dispatcher.UIThread.Post(() => Search.Focus(), DispatcherPriority.Input);
    }

    string[] AllModels() =>
        ItemsSource?.Cast<object?>().OfType<string>().ToArray() ?? [];

    void Populate(string query) {
        var all = AllModels();
        var matches = query.Length == 0
            ? all
            : all.Where(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        bool trimmed = query.Length == 0 && matches.Length > BrowseLimit;
        choosing = true;
        List.ItemsSource = trimmed ? matches.Take(BrowseLimit).ToArray() : matches;
        List.SelectedItem = Text;
        choosing = false;

        Hint.Text = trimmed
            ? $"Newest {BrowseLimit} of {all.Length} — type to search them all"
            : matches.Length == 0 && query.Length > 0
                ? $"No match. Press Enter to use “{query}” as the model id."
                : "";
        Hint.IsVisible = Hint.Text.Length > 0;
    }

    void OnSearchChanged(object? sender, TextChangedEventArgs e) => Populate(Search.Text ?? "");

    void OnSearchKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            flyout?.Hide();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Enter or Key.Return)) return;
        // Enter takes the top match, or the raw text when nothing matched —
        // that is how a brand-new model id gets in.
        string chosen = List.ItemsSource?.Cast<string>().FirstOrDefault()
                        ?? (Search.Text ?? "").Trim();
        if (chosen.Length > 0) Commit(chosen);
        e.Handled = true;
    }

    void OnItemChosen(object? sender, SelectionChangedEventArgs e) {
        if (choosing || List.SelectedItem is not string id) return;
        Commit(id);
    }

    void Commit(string id) {
        Text = id;
        ShowCurrent();
        flyout?.Hide();
    }
}
