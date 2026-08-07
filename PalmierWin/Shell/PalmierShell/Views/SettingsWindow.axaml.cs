using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PalmierShell.Core;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Appearance and AI preferences. Provider/model/key are saved through the
/// agent view model so there is one path that also reconfigures the agent.
/// Accent changes preview live and revert if the window closes unsaved.
public partial class SettingsWindow : Window {
    /// Tells the user whether leaving the key box blank keeps a stored key.
    public static readonly Avalonia.Data.Converters.IValueConverter KeyWatermark =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(
            has => has ? "Key saved — type to replace" : "Paste your API key");

    readonly MainViewModel? main;
    readonly string accentOnOpen;
    string accentHex;
    bool saved;
    bool loadingAgentMode = true;

    public SettingsWindow() : this(null!) { }  // XAML designer only

    public SettingsWindow(MainViewModel main, int tabIndex = 0) {
        this.main = main;
        accentOnOpen = accentHex = HexOf(Accent.Current);
        InitializeComponent();
        if (main is null) return;
        Panes.SelectedIndex = tabIndex;

        // The AI tab binds straight to the agent view model — one owner for
        // provider, key, and model state.
        DataContext = main.Agent;
        SnapDefault.IsChecked = main.Timeline.SnapEnabled;
        var settings = SettingsStore.Load();
        UserNameBox.Text = settings.UserName;
        // The mode radios apply on click (no Save round-trip); the flag keeps
        // the initial check from writing the settings right back.
        AgentModeExternal.IsChecked = settings.AgentMode == AppSettings.AgentModeExternal;
        AgentModeInline.IsChecked = settings.AgentMode != AppSettings.AgentModeExternal;
        AgentModeStatus.Text = main.Mcp.StatusLine;
        AgentModeInline.Checked += OnAgentModeChecked;
        AgentModeExternal.Checked += OnAgentModeChecked;
        loadingAgentMode = false;
        main.Mcp.PropertyChanged += OnMcpPropertyChanged;
        AccentSwatches.SelectedHex = accentHex;
        AccentSwatches.SelectionChanged += hex => { accentHex = hex; Accent.Apply(hex); };
        BuildGenerationKeys();
        Closed += (_, _) => {
            main.Mcp.PropertyChanged -= OnMcpPropertyChanged;
            if (!saved) Accent.Apply(accentOnOpen);
        };
    }

    void OnMcpPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(Core.Mcp.McpHost.StatusLine) && main is not null)
            AgentModeStatus.Text = main.Mcp.StatusLine;
    }

    async void OnAgentModeChecked(object? sender, RoutedEventArgs e) {
        if (loadingAgentMode || main is null) return;
        string mode = AgentModeExternal.IsChecked == true
            ? AppSettings.AgentModeExternal : AppSettings.AgentModeInline;
        await main.Mcp.SetAgentModeAsync(mode);
    }

    readonly Dictionary<string, TextBox> generationKeyBoxes = new();

    /// One password row per generation provider, built from the provider list
    /// so adding a provider needs no XAML change.
    void BuildGenerationKeys() {
        var settings = SettingsStore.Load();
        var rows = new List<Control>();
        foreach (var provider in GenerationProviders.All) {
            var box = new TextBox {
                PasswordChar = '•',
                FontSize = 12,
                Watermark = settings.KeyFor(provider.Id).Length > 0
                    ? "Key saved — type to replace"
                    : $"API key from {provider.KeyHint}",
            };
            generationKeyBoxes[provider.Id] = box;
            rows.Add(new StackPanel {
                Spacing = 2,
                Margin = new Avalonia.Thickness(0, 0, 0, 10),
                Children = {
                    new TextBlock {
                        Text = provider.Name, FontSize = 11,
                        Foreground = (IBrush?)this.FindResource("ThemeTextTertiaryBrush"),
                    },
                    box,
                },
            });
        }
        GenerationKeys.ItemsSource = rows;
    }

    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    async void OnSave(object? sender, RoutedEventArgs e) {
        if (main is null) return;
        bool snap = SnapDefault.IsChecked == true;
        main.Timeline.SnapEnabled = snap;
        string userName = UserNameBox.Text?.Trim() ?? "";
        main.SetUserName(userName);
        // Blank generation boxes keep whatever key is already stored.
        var generationKeys = generationKeyBoxes
            .Select(pair => (Provider: pair.Key, Key: pair.Value.Text?.Trim() ?? ""))
            .Where(entry => entry.Key.Length > 0)
            .ToList();

        await Task.Run(() => SettingsStore.Update(s => {
            var updated = s with { Accent = accentHex, SnapEnabled = snap, UserName = userName };
            foreach (var (provider, key) in generationKeys) updated = updated.WithKey(provider, key);
            return updated;
        }));
        foreach (var box in generationKeyBoxes.Values) {
            box.Text = "";
            box.Watermark = "Key saved — type to replace";
        }
        await main.Agent.SaveSettingsCommand.ExecuteAsync(null);
        await main.Media.Generate.RefreshKeyAsync();
        saved = true;
        StatusText.Text = "Saved.";
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
