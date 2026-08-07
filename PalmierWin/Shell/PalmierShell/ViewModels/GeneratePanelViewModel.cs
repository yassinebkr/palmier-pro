using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;
using PalmierShell.Core.Generation;

namespace PalmierShell.ViewModels;

/// A generation in flight, shown as a placeholder tile in the library until
/// its file lands.
public sealed partial class GenerationJobViewModel : ObservableObject {
    public string Prompt { get; }
    public string ModelName { get; }
    readonly CancellationTokenSource cancellation = new();
    readonly DateTime startedAt = DateTime.UtcNow;
    readonly Avalonia.Threading.DispatcherTimer ticker;
    GenerationState state = GenerationState.Queued;

    [ObservableProperty] string statusText = "Queued…";
    [ObservableProperty] bool failed;

    public CancellationToken Token => cancellation.Token;

    public GenerationJobViewModel(string prompt, string modelName) {
        Prompt = prompt;
        ModelName = modelName;
        // A silent minutes-long wait reads as a hang; the clock is the
        // difference between "working" and "stuck".
        ticker = new Avalonia.Threading.DispatcherTimer {
            Interval = TimeSpan.FromSeconds(1),
        };
        ticker.Tick += (_, _) => Refresh();
        ticker.Start();
    }

    /// A running DispatcherTimer roots the job; every terminal path must
    /// land here or finished jobs never collect.
    public void Finish() => ticker.Stop();

    [RelayCommand]
    public void Cancel() {
        cancellation.Cancel();
        Finish();
        StatusText = "Cancelled";
        Failed = true;
    }

    int? progress;

    public void Report(GenerationStatus status) {
        state = status.State;
        if (status.Progress is { } percent) progress = percent;
        Refresh();
    }

    void Refresh() {
        if (Failed) { Finish(); return; }
        var elapsed = DateTime.UtcNow - startedAt;
        string clock = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        StatusText = state switch {
            GenerationState.Queued => $"Queued… {clock}",
            GenerationState.Running when progress is { } percent =>
                $"Generating {percent}% — {clock}",
            GenerationState.Running => $"Generating… {clock}",
            GenerationState.Succeeded => $"Downloading… {clock}",
            _ => "Failed",
        };
    }
}

/// One attached reference, shown as a chip: the label the prompt uses for it
/// and the file it points at.
public sealed partial class ReferenceItemViewModel : ObservableObject {
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    [ObservableProperty] string label;

    public ReferenceItemViewModel(string path, string label) {
        Path = path;
        this.label = label;
    }
}

/// One row of the recent-generations list: the take's prompt, model and age,
/// plus an Import action while the file is on disk and not yet in the library.
public sealed partial class RecentGenerationViewModel : ObservableObject {
    public string MediaPath { get; }
    public string PromptExcerpt { get; }
    public string Detail { get; }
    [ObservableProperty] bool canImport;

    public RecentGenerationViewModel(RecentGeneration entry, bool canImport, DateTimeOffset now) {
        MediaPath = entry.MediaPath;
        CanImport = canImport;
        Detail = $"{entry.Model} · {RelativeTime.Ago(entry.CreatedUtc, now)}";
        string oneLine = entry.Prompt.ReplaceLineEndings(" ").Trim();
        PromptExcerpt = oneLine.Length <= 60 ? oneLine : oneLine[..60].TrimEnd() + "…";
    }
}

/// The Generate panel: prompt, provider, model, duration. Finished clips are
/// imported into the media library like any other file.
public sealed partial class GeneratePanelViewModel : ObservableObject {
    readonly Func<string, string?, Task> importAsync;
    readonly Func<IEnumerable<MediaItemViewModel>> library;

    /// Library items the frame slots can be filled from. Stills only: a
    /// reference frame is a picture, and Capture Frame is how a clip becomes
    /// one. Reaching for the OS file dialog instead sent the user out of the
    /// app to hunt for files it had already imported.
    public IReadOnlyList<MediaItemViewModel> FrameChoices =>
        library().Where(item => IsStill(item.Path)).Reverse().ToList();

    static bool IsStill(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    public IReadOnlyList<IGenerationProvider> Providers { get; } = GenerationProviders.All;
    public ObservableCollection<GenerationModel> Models { get; } = new();
    public ObservableCollection<int> Durations { get; } = new();
    public ObservableCollection<string> Resolutions { get; } = new();
    public ObservableCollection<GenerationJobViewModel> Jobs { get; } = new();

    /// Reference stills the model travels between. Set by hand, or filled
    /// automatically when a transition is requested at a cut.
    [ObservableProperty] string? firstFramePath;
    [ObservableProperty] string? lastFramePath;
    [ObservableProperty] Avalonia.Media.Imaging.Bitmap? firstFrameThumb;
    [ObservableProperty] Avalonia.Media.Imaging.Bitmap? lastFrameThumb;

    /// Timeline frames the slots were captured from, when they came from the
    /// timeline — this is what makes them steerable. Manual picks have none.
    [ObservableProperty] int? firstFrameNumber;
    [ObservableProperty] int? lastFrameNumber;

    public bool CanNudgeFirst => FirstFrameNumber is not null && CaptureTimelineFrame is not null;
    public bool CanNudgeLast => LastFrameNumber is not null && CaptureTimelineFrame is not null;

    public string FirstFrameLabel =>
        FirstFrameNumber is { } f ? Timecode.Format(f, TimelineFps) : "";
    public string LastFrameLabel =>
        LastFrameNumber is { } l ? Timecode.Format(l, TimelineFps) : "";

    /// Captures one composited timeline frame to a PNG, off the UI thread.
    /// Wired by the shell; the composer only knows frame numbers.
    public Func<int, Task<string?>>? CaptureTimelineFrame { get; set; }

    /// Steps a slot along the timeline and recaptures — the fix for "the
    /// frame the cut landed on is not the frame I want to travel from".
    /// Failures land in `Message`: a capture that silently does nothing is
    /// indistinguishable from a dead button.
    [RelayCommand]
    async Task NudgeFirst(string delta) {
        if (FirstFrameNumber is not { } frame || CaptureTimelineFrame is null) return;
        int target = Math.Max(0, frame + int.Parse(delta));
        if (await CaptureAt(target) is { } path) SetFirstFrame(path, target);
    }

    [RelayCommand]
    async Task NudgeLast(string delta) {
        if (LastFrameNumber is not { } frame || CaptureTimelineFrame is null) return;
        int target = Math.Max(0, frame + int.Parse(delta));
        if (await CaptureAt(target) is { } path) SetLastFrame(path, target);
    }

    async Task<string?> CaptureAt(int frame) {
        Message = $"Capturing {Timecode.Format(frame, TimelineFps)}…";
        try {
            string? path = await CaptureTimelineFrame!(frame);
            Message = path is null
                ? $"Could not read frame {Timecode.Format(frame, TimelineFps)} — nothing " +
                  "decodable there."
                : null;
            return path;
        } catch (Exception ex) {
            Message = $"Could not capture {Timecode.Format(frame, TimelineFps)}: {ex.Message}";
            return null;
        }
    }

    /// Reference media riding with the prompt — the [Image1]/[Video1] the
    /// prompt addresses. Only offered on models whose schemas take them.
    public ObservableCollection<ReferenceItemViewModel> ReferenceImages { get; } = new();
    public ObservableCollection<ReferenceItemViewModel> ReferenceVideos { get; } = new();

    public bool HasReferences => ReferenceImages.Count > 0 || ReferenceVideos.Count > 0;

    /// Library videos short enough to be a reference (the endpoint takes 15 s
    /// combined, so anything longer can never fit).
    public IReadOnlyList<MediaItemViewModel> VideoRefChoices =>
        library().Where(item => !IsStill(item.Path) && SecondsOf(item) is > 0 and <= 15)
                 .Reverse().ToList();

    static double SecondsOf(MediaItemViewModel item) =>
        item.Fps > 0 ? item.TotalFrames / item.Fps : 0;

    public void AddReferenceImage(string path) {
        if (CurrentModel is not { } m || ReferenceImages.Count >= m.MaxReferenceImages) return;
        ReferenceImages.Add(new ReferenceItemViewModel(path, $"[Image{ReferenceImages.Count + 1}]"));
        ReferencesChanged();
    }

    public void AddReferenceVideo(string path) {
        if (CurrentModel is not { } m || ReferenceVideos.Count >= m.MaxReferenceVideos) return;
        ReferenceVideos.Add(new ReferenceItemViewModel(path, $"[Video{ReferenceVideos.Count + 1}]"));
        ReferencesChanged();
    }

    [RelayCommand]
    void RemoveReference(ReferenceItemViewModel? item) {
        if (item is null) return;
        ReferenceImages.Remove(item);
        ReferenceVideos.Remove(item);
        Relabel(ReferenceImages, "Image");
        Relabel(ReferenceVideos, "Video");
        ReferencesChanged();
    }

    static void Relabel(ObservableCollection<ReferenceItemViewModel> list, string noun) {
        for (int i = 0; i < list.Count; i++) list[i].Label = $"[{noun}{i + 1}]";
    }

    void ReferencesChanged() {
        OnPropertyChanged(nameof(HasReferences));
        OnPropertyChanged(nameof(ReferenceConflict));
        RefreshDerived();
    }

    /// The schema forbids frames + references in one request. Refusing here
    /// is the contract: paying the endpoint to refuse it is not a validation
    /// strategy.
    public string? ReferenceConflict {
        get {
            if (!HasReferences) return null;
            if (CurrentModel is not { AcceptsReferences: true })
                return "This model takes no reference media — on Replicate only Seedance 2.0 " +
                       "does. Switch model or remove the references.";
            if (CurrentModel.FramesAndReferencesExclusive && (HasFirstFrame || HasLastFrame))
                return "This model takes first/last frames OR reference media, never both. " +
                       "Clear one side before generating.";
            return null;
        }
    }

    /// The composer is docked, not a popup — arming a transition has to make
    /// it visible, or the whole flow looks like it did nothing.
    [ObservableProperty] bool isOpen;

    [RelayCommand]
    void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    void TogglePromptPreview() => ShowPromptPreview = !ShowPromptPreview;

    /// Set when the run should land on a timeline cut rather than the library.
    public TransitionTarget? PendingTransition { get; private set; }

    /// Set when the run should fill empty timeline space with a new shot.
    public ShotTarget? PendingShot { get; private set; }

    /// Set when the run should continue an existing clip and land after it.
    public EnhanceTarget? PendingEnhance { get; private set; }

    /// Bumped on every arm and disarm, so asynchronous arm prep (frame
    /// decodes, tail extractions) can tell a stale completion from the arm
    /// it started under.
    public int ArmGeneration { get; private set; }

    public bool HasFirstFrame => FirstFramePath is not null;
    public bool HasLastFrame => LastFramePath is not null;

    [ObservableProperty] IGenerationProvider selectedProvider;
    /// The model id sent to the provider. Free text so a model newer than this
    /// build's curated list can still be used by pasting its id.
    [ObservableProperty] string modelId = "";
    [ObservableProperty] int selectedDuration = 5;
    [ObservableProperty] string selectedResolution = "720p";
    [ObservableProperty] string prompt = "";
    [ObservableProperty] bool hasApiKey;
    [ObservableProperty] string? message;

    /// Where the next take lands, in one persistent header line. Message is
    /// transient — a capture warning replaces the arm's text — so the target
    /// statement lives here and the arm messages are composed from it.
    string targetSummary = DefaultTargetSummary;
    const string DefaultTargetSummary = "New shot into the media library";

    public string TargetSummary =>
        UseLocationContext && LocationStatus is { } status
            ? $"{targetSummary} · {status}"
            : targetSummary;

    void SetTargetSummary(string summary) {
        targetSummary = summary;
        OnPropertyChanged(nameof(TargetSummary));
    }

    /// The prompt builder's fold follows the user, across sessions. Persisted
    /// on every toggle; restored once at construction.
    [ObservableProperty] bool promptBuilderExpanded = true;
    bool restoringBuilderState;

    partial void OnPromptBuilderExpandedChanged(bool value) {
        if (restoringBuilderState) return;
        _ = Task.Run(() => SettingsStore.Update(s => s with { PromptBuilderExpanded = value }));
    }

    /// The guided builder's sections. A chip appends its phrase to the same
    /// editable prompt text — the builder is an assist, never a gate.
    public IReadOnlyList<PromptChipGroup> PromptChipGroups => PromptBuilder.Groups;

    [RelayCommand]
    void InsertPromptChip(string phrase) => Prompt = PromptBuilder.AppendPhrase(Prompt, phrase);

    /// Model-specific prompt tuning. On by default — it is the difference
    /// between a usable clip and a lottery ticket — but always visible and
    /// always optional.
    [ObservableProperty] bool tunePrompt = true;
    [ObservableProperty] bool showPromptPreview;

    /// Opt-in GPS grounding: a "Setting: <place>" line from the armed target's
    /// source media location tag. The toggle is offered only when the tag
    /// exists and resets to off on every arm — location is never sent unless
    /// the user asks, this time.
    [ObservableProperty] bool hasLocationContext;
    [ObservableProperty] bool useLocationContext;
    [ObservableProperty] string? locationStatus;

    string? locationTag;
    string? settingText;
    int locationGeneration;

    /// In-flight Setting-line resolution, awaitable so tests can order
    /// against the status writes.
    public Task LocationResolution { get; private set; } = Task.CompletedTask;

    /// Resolves a location tag to "City, Country" text. The shared geocoder
    /// by default; settable so tests never touch the network.
    public Func<string, Task<string?>> DescribeLocation { get; set; } =
        tag => GeocodeService.Shared.DescribeAsync(tag);

    /// Suggestion strings for the picker — "Display name — id".
    public ObservableCollection<string> ModelChoices { get; } = new();

    public bool CanGenerate => HasApiKey && Prompt.Trim().Length > 0 && ModelId.Trim().Length > 0;

    GenerationModel? CurrentModel => Models.FirstOrDefault(m => m.Id == ModelId.Trim());

    IPromptStyle Style => PromptStyles.For(ModelId.Trim());

    PromptContext Context => new(HasFirstFrame, HasLastFrame,
                                 PendingTransition is not null, SelectedDuration) {
        Frames = CurrentModel?.Frames ?? FrameInput.None,
        ImageReferences = ReferenceImages.Count,
        VideoReferences = ReferenceVideos.Count,
    };

    /// The prompt exactly as it will be sent. The Setting line lives here, in
    /// the one assembly path both the preview and the submit read, so the two
    /// can never diverge — and never in the editable box, which stays the
    /// user's own words.
    public string FinalPrompt {
        get {
            string built = TunePrompt ? Style.Build(Prompt, Context) : Prompt.Trim();
            if (UseLocationContext && settingText is { } setting)
                built += $"\nSetting: {setting}";
            return built;
        }
    }

    /// Name of the tuning in force, for the composer's label.
    public string StyleName => Style.Name;

    /// Disclosure label. It names the thing rather than the action, and says
    /// how long the result is, so it is worth opening before spending money.
    public string PromptPreviewLabel {
        get {
            int length = FinalPrompt.Length;
            if (length == 0) return "See the prompt that will be sent";
            return ShowPromptPreview
                ? $"Hide the prompt that will be sent ({length} characters)"
                : $"See the prompt that will be sent ({length} characters)";
        }
    }

    public string PromptPreviewChevron => ShowPromptPreview ? "▾" : "▸";

    /// What is wrong with the prompt as written — the part that actually
    /// improves a result. Shown in the composer, not hidden in a tooltip: a
    /// weak prompt is not rescued by anything the tuning appends.
    public IReadOnlyList<string> Advice => Style.Review(Prompt, Context);

    /// Guidance plus anything questionable about the prompt as written.
    public string PromptAdvice =>
        string.Join(Environment.NewLine,
            Style.Review(Prompt, Context).Concat(Style.Notes).Select(line => "• " + line));

    /// Trouble with the *endpoint* rather than the prompt: stills that will not
    /// be sent because the chosen model cannot take them. Kept separate from
    /// `Advice` so the two never say the same thing twice.
    public string? PromptWarning {
        get {
            if (!HasFirstFrame && !HasLastFrame) return null;
            if (CurrentModel is null)
                return "We have not read this model's schema, so the stills will not be sent. " +
                       "Pick one from the list to use them.";
            return CurrentModel.AcceptsFrames
                ? null
                : "This endpoint takes no reference frames — they will not be sent. " +
                  "Pick a first/last frame model to use them.";
        }
    }

    public bool HasPromptWarning => PromptWarning is not null;

    /// What the run is expected to cost, shown before the Generate button.
    public string PriceText {
        get {
            var estimate = GenerationPricing.For(SelectedProvider.Id, ModelId,
                                                 SelectedDuration, SelectedResolution);
            return estimate is null
                ? "No published rate for this model"
                : $"{estimate.Text}{(estimate.Approximate ? " (approx.)" : "")}";
        }
    }

    public string PriceBasis =>
        GenerationPricing.For(SelectedProvider.Id, ModelId, SelectedDuration, SelectedResolution)
            is { } estimate
            ? $"{estimate.Basis}. Rates change — check the provider before relying on it."
            : $"We have no rate for this model on {SelectedProvider.Name}. " +
              "Check its pricing page before running it.";

    partial void OnShowPromptPreviewChanged(bool value) {
        OnPropertyChanged(nameof(PromptPreviewLabel));
        OnPropertyChanged(nameof(PromptPreviewChevron));
    }

    void RefreshDerived() {
        OnPropertyChanged(nameof(FinalPrompt));
        OnPropertyChanged(nameof(StyleName));
        OnPropertyChanged(nameof(PromptPreviewLabel));
        OnPropertyChanged(nameof(Advice));
        OnPropertyChanged(nameof(PromptAdvice));
        OnPropertyChanged(nameof(PromptWarning));
        OnPropertyChanged(nameof(HasPromptWarning));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(PriceBasis));
        OnPropertyChanged(nameof(FrameModeText));
        OnPropertyChanged(nameof(ReferenceConflict));
        OnPropertyChanged(nameof(CanAttachReferences));
        OnPropertyChanged(nameof(TargetSummary));
    }

    /// Whether the reference section is offered at all for the current model.
    public bool CanAttachReferences => CurrentModel is { AcceptsReferences: true };

    /// Which of the endpoint's image modes the attached stills will go in.
    ///
    /// They are not interchangeable: first/last travels between two frames,
    /// while a reference array carries a character's likeness into a new shot
    /// — and an endpoint usually refuses to do both at once. Which one you get
    /// was invisible, so a transition spent months being sent down the
    /// likeness path and rejected, with nothing on screen to say why.
    public string? FrameModeText {
        get {
            if (!HasFirstFrame && !HasLastFrame) return null;
            if (CurrentModel is not { } model)
                return "Unknown model — the stills will not be sent.";
            return model.Frames switch {
                FrameInput.FirstLast => "Sent as first and last frame.",
                FrameInput.FirstOnly when HasFirstFrame && HasLastFrame =>
                    "Sent as the opening frame only — this model has no last-frame input, " +
                    "so the end frame will not be sent.",
                FrameInput.FirstOnly when HasFirstFrame => "Sent as the opening frame.",
                FrameInput.FirstOnly =>
                    "This model only takes an opening frame; the end frame will not be sent.",
                FrameInput.References =>
                    "Sent as reference images — this model has no first/last frame mode, so it " +
                    "is being asked to carry a likeness rather than travel between the frames.",
                _ => "This model takes no stills; they will not be sent.",
            };
        }
    }

    public GeneratePanelViewModel(Func<string, string?, Task> importAsync,
                                  Func<IEnumerable<MediaItemViewModel>> library) {
        this.importAsync = importAsync;
        this.library = library;
        selectedProvider = Providers[0];
        SyncModels();
        Initialized = InitializeAsync();
    }

    /// The constructor's key/model restore, awaitable so callers (and tests)
    /// can order against it instead of racing its Message writes.
    public Task Initialized { get; }

    /// One settings read at construction: the persisted panel state plus the
    /// key/model restore every settings read shares.
    async Task InitializeAsync() {
        var settings = await Task.Run(() => LoadSettings());
        restoringBuilderState = true;
        PromptBuilderExpanded = settings.PromptBuilderExpanded;
        restoringBuilderState = false;
        ApplySettings(settings);
    }

    void SyncModels() => SyncModels(keepSelection: false);

    /// Rebuilds the pickers from the provider's manifest. A provider switch
    /// starts at the top of the new list; a manifest sync keeps the current
    /// model when it survives, so the button never silently moves the user.
    void SyncModels(bool keepSelection) {
        string keep = keepSelection ? ModelId.Trim() : "";
        Models.Clear();
        ModelChoices.Clear();
        foreach (var model in PickerModels()) {
            Models.Add(model);
            ModelChoices.Add(model.Id);
        }
        ModelId = keep.Length > 0 ? keep : Models.FirstOrDefault()?.Id ?? "";
    }

    /// What the picker may offer in the current mode. An extend-only endpoint
    /// needs a source video to mean anything, so it stays out of plain shots
    /// and transitions — and while an enhance is armed, the models that can
    /// continue a clip are all the picker shows.
    IEnumerable<GenerationModel> PickerModels() => PendingEnhance is not null
        ? SelectedProvider.Models.Where(m => m.CanExtend)
        : SelectedProvider.Models.Where(m => !m.ExtendOnly);

    partial void OnSelectedProviderChanged(IGenerationProvider value) {
        SyncModels();
        _ = RefreshKeyAsync();
    }

    /// In flight while the manifest check runs: the button relabels and
    /// disables rather than stacking a second fetch.
    [ObservableProperty] bool updatingModels;

    public bool CanUpdateModels => !UpdatingModels;

    public string UpdateModelsText => UpdatingModels ? "Checking…" : "Update models";

    partial void OnUpdatingModelsChanged(bool value) {
        OnPropertyChanged(nameof(CanUpdateModels));
        OnPropertyChanged(nameof(UpdateModelsText));
    }

    /// Pulls the latest model manifest and swaps the picker over to it. The
    /// current list stays on any failure — offline must never blank models.
    [RelayCommand]
    async Task UpdateModels() {
        if (UpdatingModels) return;
        UpdatingModels = true;
        Message = "Checking for new models…";
        try {
            await RunModelSyncAsync();
        } finally {
            UpdatingModels = false;
        }
    }

    /// Startup sync (MainViewModel, once per launch): quiet unless models
    /// actually changed — no "Checking…" churn, failures stay silent.
    public async Task StartupSyncAsync() {
        var report = await ModelManifest.SyncAsync();
        if (report is null) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            SyncModels(keepSelection: true);
            ModelOptionsChanged(ModelId);
            if (report.Added.Count > 0 || report.Removed.Count > 0)
                Message = DescribeSync(report);
        });
    }

    async Task RunModelSyncAsync() {
        var report = await ModelManifest.SyncAsync();
        if (report is null) {
            Message = "Couldn't check — keeping the current list.";
            return;
        }
        SyncModels(keepSelection: true);
        ModelOptionsChanged(ModelId);   // unchanged id still re-reads durations/resolutions
        Message = DescribeSync(report);
    }

    static string DescribeSync(ModelManifest.ManifestSyncReport report) {
        var parts = new List<string>();
        if (report.Added.Count > 0)
            parts.Add($"{report.Added.Count} new model{(report.Added.Count > 1 ? "s" : "")}: " +
                      string.Join(", ", report.Added));
        if (report.Removed.Count > 0)
            parts.Add($"{report.Removed.Count} model{(report.Removed.Count > 1 ? "s" : "")} removed: " +
                      string.Join(", ", report.Removed));
        return parts.Count > 0 ? string.Join("; ", parts) + "." : "Already up to date.";
    }

    /// Durations come from the curated entry; a pasted id gets the common set.
    /// Added before the reselect and pruned after, because a ComboBox bound to
    /// an item that leaves its source writes null back into this int.
    partial void OnModelIdChanged(string value) => ModelOptionsChanged(value);

    void ModelOptionsChanged(string value) {
        var model = Models.FirstOrDefault(m => m.Id == value.Trim());
        Retarget(Durations, model?.Durations ?? [5, 10], SelectedDuration, next => SelectedDuration = next);
        Retarget(Resolutions, model?.Resolutions ?? ["720p"], SelectedResolution,
                 next => SelectedResolution = next);
        OnPropertyChanged(nameof(CanGenerate));
        RefreshDerived();
    }

    /// Swaps a picker's options without letting the bound selection go null:
    /// a ComboBox writes null back into its source the moment the selected
    /// item leaves the list, so options are added before the reselect and the
    /// stale ones pruned after.
    static void Retarget<T>(ObservableCollection<T> list, IReadOnlyList<T> options,
                            T current, Action<T> select) {
        foreach (var option in options) {
            if (!list.Contains(option)) list.Add(option);
        }
        select(options.Contains(current) ? current : options[0]);
        for (int i = list.Count - 1; i >= 0; i--) {
            if (!options.Contains(list[i])) list.RemoveAt(i);
        }
    }

    partial void OnPromptChanged(string value) {
        OnPropertyChanged(nameof(CanGenerate));
        RefreshDerived();
    }

    partial void OnUseLocationContextChanged(bool value) =>
        LocationResolution = ResolveLocationAsync(value);

    async Task ResolveLocationAsync(bool enabled) {
        int generation = ++locationGeneration;
        settingText = null;
        if (!enabled || locationTag is null) {
            LocationStatus = null;
            RefreshDerived();
            return;
        }
        LocationStatus = "locating…";
        string? place = await DescribeLocation(locationTag);
        if (generation != locationGeneration) return;   // toggled off or re-armed mid-flight
        settingText = place;
        LocationStatus = place is null ? "location unavailable" : $"Setting: {place}";
        RefreshDerived();
    }

    /// Arming or clearing a target starts the privacy surface over: toggle
    /// off, any stale resolution dropped, and the row hidden when the source
    /// media carries no location tag.
    void ResetLocation(string? tag) {
        locationGeneration++;   // stales any in-flight resolve
        locationTag = tag is { Length: > 0 } ? tag : null;
        settingText = null;
        LocationStatus = null;
        HasLocationContext = locationTag is not null;
        UseLocationContext = false;
        RefreshDerived();
    }

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));
    partial void OnSelectedDurationChanged(int value) => RefreshDerived();
    partial void OnSelectedResolutionChanged(string value) => RefreshDerived();
    partial void OnTunePromptChanged(bool value) => RefreshDerived();

    /// Test seam: replaces the settings read. Never set in production code.
    public Func<AppSettings> LoadSettings { get; set; } = SettingsStore.Load;

    /// Re-reads the provider's key; call after the settings pane saves.
    /// Also restores the provider's last-used model — but only over an
    /// untouched default, never over a selection made this session, and in
    /// enhance mode never a model the narrowed picker does not offer.
    public async Task RefreshKeyAsync() => ApplySettings(await Task.Run(() => LoadSettings()));

    void ApplySettings(AppSettings settings) {
        HasApiKey = settings.KeyFor(SelectedProvider.Id).Length > 0;
        Message = HasApiKey ? null : $"Add a {SelectedProvider.Name} key in Settings → Generation.";
        if (ModelId == Models.FirstOrDefault()?.Id
            && settings.Models.TryGetValue(ModelMemoryKey, out var saved)
            && saved.Length > 0
            && (PendingEnhance is null || Models.Any(m => m.Id == saved)))
            ModelId = saved;
    }

    /// Namespaced so generation models never collide with the Agent's
    /// per-provider model memory in the same settings map.
    string ModelMemoryKey => "generate:" + SelectedProvider.Id;

    /// Two runs of the same request, to pick the better take. Off by default:
    /// it doubles the bill.
    [ObservableProperty] bool twoTakes;

    [RelayCommand]
    void Generate() {
        if (!CanGenerate) return;
        if (ReferenceConflict is { } conflict) {
            Message = conflict;
            return;
        }
        if (PendingEnhance is not null && ReferenceVideos.Count == 0) {
            Message = "The source video was removed — re-arm Enhance from the clip's menu.";
            return;
        }
        string id = ModelId.Trim();
        string name = Models.FirstOrDefault(m => m.Id == id)?.Name ?? id;
        var request = BuildRequest();
        // The model that actually ran is the one worth restoring next launch.
        string memoryKey = ModelMemoryKey;
        _ = Task.Run(() => SettingsStore.Update(s => s.WithModel(memoryKey, id)));
        var transition = PendingTransition;
        var shot = PendingShot;
        var enhance = PendingEnhance;
        int takes = TwoTakes ? 2 : 1;
        // One claim across the takes: the first success lands on the
        // timeline, the other stays in the library as the alternative.
        var claim = new InsertClaim();
        for (int take = 1; take <= takes; take++) {
            var job = new GenerationJobViewModel(Prompt.Trim(),
                takes > 1 ? $"{name} · take {take}" : name);
            Jobs.Add(job);
            _ = RunAsync(job, SelectedProvider, request, transition, shot, enhance, claim);
        }
        Prompt = "";
        ClearPlacement();
    }

    /// The request exactly as Generate sends it — the prompt actually sent,
    /// not the one typed, plus the attached stills and references. Split out
    /// so what would go on the wire is checkable without spending money.
    public GenerationRequest BuildRequest() => new(FinalPrompt, ModelId.Trim(), SelectedDuration) {
        FirstFrame = FirstFramePath,
        LastFrame = LastFramePath,
        Resolution = SelectedResolution,
        NegativePrompt = TunePrompt ? Style.Negative(Context) : null,
        ReferenceImages = ReferenceImages.Select(r => r.Path).ToList(),
        ReferenceVideos = ReferenceVideos.Select(r => r.Path).ToList(),
    };

    sealed class InsertClaim {
        int claimed;
        public bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
    }

    /// Raised when a finished transition should be dropped onto its cut.
    public event Action<TransitionTarget, string>? TransitionReady;

    /// Raised when a finished shot should be dropped into the space it was
    /// generated for.
    public event Action<ShotTarget, string>? ShotReady;

    /// Raised when a finished enhance should land after its source clip.
    public event Action<EnhanceTarget, string>? EnhanceReady;

    async Task RunAsync(GenerationJobViewModel job, IGenerationProvider provider,
                        GenerationRequest request, TransitionTarget? transition,
                        ShotTarget? shot, EnhanceTarget? enhance, InsertClaim claim) {
        try {
            var settings = await Task.Run(SettingsStore.Load);
            string key = settings.KeyFor(provider.Id);
            if (key.Length == 0) throw new GenerationException($"No {provider.Name} API key.");

            string path = await GenerationService.RunAsync(provider, request, key, job.Report, job.Token);
            GenerationRecord.Write(path, new GenerationRecord(
                provider.Name, request.Model, request.Prompt, request.Seconds,
                DateTime.UtcNow.ToString("O")) {
                FirstFrame = request.FirstFrame,
                LastFrame = request.LastFrame,
            });
            Jobs.Remove(job);
            job.Finish();
            // Transitions and enhances get folders of their own; a generated
            // shot is just another piece of footage and belongs with the rest.
            await importAsync(path,
                transition is not null ? MediaPanelViewModel.TransitionsFolder
                : enhance is not null ? MediaPanelViewModel.EnhancedFolder
                : null);
            // With two takes, the first success takes the timeline slot and
            // the other stays in the library as the alternative to swap in.
            if (transition is not null) {
                if (claim.TryClaim()) TransitionReady?.Invoke(transition, path);
                else Message = "Second take imported to Transitions — swap it in if you prefer it.";
            }
            if (shot is not null) {
                if (claim.TryClaim()) ShotReady?.Invoke(shot, path);
                else Message = "Second take imported to the library — swap it in if you prefer it.";
            }
            if (enhance is not null) {
                if (claim.TryClaim()) EnhanceReady?.Invoke(enhance, path);
                else Message = "Second take imported to Enhanced — swap it in if you prefer it.";
            }
            await RefreshRecentAsync();
        } catch (OperationCanceledException) {
            Jobs.Remove(job);
            job.Finish();
        } catch (Exception ex) {
            job.Failed = true;
            job.Finish();
            job.StatusText = ex is GenerationException ? ex.Message : $"Failed: {ex.Message}";
        }
    }

    /// App shutdown: cancel every in-flight job so no download or remote
    /// polling continues after the window is gone.
    public void CancelAll() {
        foreach (var job in Jobs.ToArray()) job.Cancel();
    }

    [RelayCommand]
    void DismissJob(GenerationJobViewModel? job) {
        if (job is not null) Jobs.Remove(job);
    }

    /// Finished takes still on disk, newest first. Rebuilt on every composer
    /// open and after each completed job; the section stays collapsed, and
    /// hidden while empty.
    public ObservableCollection<RecentGenerationViewModel> RecentGenerations { get; } = new();

    public bool HasRecentGenerations => RecentGenerations.Count > 0;

    /// Test seam: where the history is read from. Production reads the real
    /// generation output directory. Never set in production code.
    public Func<string> HistoryDirectory { get; set; } = () => GenerationService.OutputDirectory;

    /// Serializes concurrent rebuilds: a window open racing a job completion
    /// must not interleave two writers to the same list.
    int recentGeneration;

    public async Task RefreshRecentAsync() {
        int generation = ++recentGeneration;
        HashSet<string> known;
        IReadOnlyList<RecentGeneration> entries;
        try {
            string directory = HistoryDirectory();
            known = library().Select(item => item.Path)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries = await Task.Run(() => GenerationHistory.Load(directory));
        } catch (Exception ex) {
            // Bookkeeping, fail-soft: the last good list stands, and a refresh
            // failure must never fail the run that just finished.
            SessionLog.Event("generate", $"recent-generations refresh failed: {ex.Message}");
            return;
        }
        if (generation != recentGeneration) return;   // a newer load owns the list
        RecentGenerations.Clear();
        var now = DateTimeOffset.Now;
        foreach (var entry in entries)
            RecentGenerations.Add(new RecentGenerationViewModel(
                entry, canImport: !known.Contains(entry.MediaPath), now));
        OnPropertyChanged(nameof(HasRecentGenerations));
    }

    /// Re-imports a take that is on disk but not in the library. The media
    /// panel dedupes by path, and the button hides either way — a take that
    /// will not probe has no working Import to offer.
    [RelayCommand]
    async Task ImportRecent(RecentGenerationViewModel? row) {
        if (row is null || !row.CanImport) return;
        row.CanImport = false;
        await importAsync(row.MediaPath, null);
    }

    [RelayCommand]
    void ClearFirstFrame() {
        FirstFramePath = null;
        FirstFrameThumb = null;
        FirstFrameNumber = null;
        OnPropertyChanged(nameof(HasFirstFrame));
        FrameSlotsChanged();
    }

    [RelayCommand]
    void ClearLastFrame() {
        LastFramePath = null;
        LastFrameThumb = null;
        LastFrameNumber = null;
        OnPropertyChanged(nameof(HasLastFrame));
        FrameSlotsChanged();
    }

    /// `frame` is the timeline frame the still was captured at, when it came
    /// from the timeline — that is what the nudge buttons steer. A manual
    /// pick has no frame and cannot be nudged.
    public void SetFirstFrame(string path, int? frame = null) {
        FirstFramePath = path;
        FirstFrameThumb = LoadThumb(path);
        FirstFrameNumber = frame;
        OnPropertyChanged(nameof(HasFirstFrame));
        FrameSlotsChanged();
    }

    public void SetLastFrame(string path, int? frame = null) {
        LastFramePath = path;
        LastFrameThumb = LoadThumb(path);
        LastFrameNumber = frame;
        OnPropertyChanged(nameof(HasLastFrame));
        FrameSlotsChanged();
    }

    void FrameSlotsChanged() {
        OnPropertyChanged(nameof(CanNudgeFirst));
        OnPropertyChanged(nameof(CanNudgeLast));
        OnPropertyChanged(nameof(FirstFrameLabel));
        OnPropertyChanged(nameof(LastFrameLabel));
        OnPropertyChanged(nameof(ReferenceConflict));
        RefreshDerived();
    }

    static Avalonia.Media.Imaging.Bitmap? LoadThumb(string path) {
        try {
            return new Avalonia.Media.Imaging.Bitmap(path);
        } catch {
            return null;  // the still is still usable even if the preview isn't
        }
    }

    /// Arms a transition: the stills bracket the cut and the finished clip is
    /// inserted there instead of only landing in the library. The frame
    /// numbers make the slots steerable with the nudge buttons. `locationTag`
    /// is the outgoing clip's container location tag, when it has one.
    public void BeginTransition(TransitionTarget target, string firstFrame, string lastFrame,
                                int? firstFrameNumber = null, int? lastFrameNumber = null,
                                string? locationTag = null) {
        ArmGeneration++;
        PendingTransition = target;
        PendingShot = null;
        ClearEnhance();
        IsOpen = true;
        SetFirstFrame(firstFrame, firstFrameNumber);
        SetLastFrame(lastFrame, lastFrameNumber);
        string moved = MoveToFrameCapableModel();
        string where = target.FillsGap ? "the gap at" : "the cut at";
        SetTargetSummary($"Transition across {where} {Timecode.Format(target.BoundaryFrame, 30)}");
        Message = targetSummary + " — describe the motion, then Generate." + moved;
        ResetSpans();
        ResetLocation(locationTag);
        if (target.FillsGap && target.DurationFrames > 0) PreferDuration(target.DurationFrames);
    }

    /// Opens the composer while the frames on either side are still being
    /// decoded, so the click has a visible effect straight away.
    public void BeginTransitionPending(int boundaryFrame, bool fillsGap) {
        IsOpen = true;
        Message = $"Reading the frames on either side of the {(fillsGap ? "gap" : "cut")} " +
                  $"at {Timecode.Format(boundaryFrame, 30)}…";
    }

    /// Arms a generated shot for empty timeline space. `locationTag` is the
    /// nearest preceding clip's container location tag, when it has one.
    public void BeginShot(ShotTarget target, string? locationTag = null) {
        ArmGeneration++;
        PendingShot = target;
        PendingTransition = null;
        ClearEnhance();
        IsOpen = true;
        ClearFirstFrame();
        ClearLastFrame();
        ResetSpans();
        ResetLocation(locationTag);
        SetTargetSummary(target.AvailableFrames > 0
            ? $"New shot for the {target.AvailableFrames / 30.0:0.#} s gap at " +
              $"{Timecode.Format(target.StartFrame, 30)}"
            : $"New shot at {Timecode.Format(target.StartFrame, 30)}");
        Message = targetSummary + " — describe it, then Generate.";
        if (target.AvailableFrames > 0) PreferDuration(target.AvailableFrames);
    }

    /// Says which neighbouring stills were filled in, once they have decoded.
    /// Silent when neither side had a frame to offer — the plain "describe it"
    /// message from `BeginShot` still stands.
    public void DescribeShotFrames(bool hasFirst, bool hasLast) {
        if (PendingShot is null || (!hasFirst && !hasLast)) return;
        string frames = hasFirst && hasLast ? "the clips on both sides"
            : hasFirst ? "the clip before it" : "the clip after it";
        SetTargetSummary($"New shot to sit between {frames}");
        Message = targetSummary + " — describe it, then Generate." + MoveToFrameCapableModel();
    }

    /// Arms an enhance: the run continues the tail of the target's clip,
    /// attached as [Video1] — the stills step aside (the endpoints take one
    /// or the other) and the picker narrows to the models that can extend.
    /// The prompt is left for the user: it describes how the shot continues.
    /// `locationTag` is the clip's container location tag, when it has one.
    public void BeginEnhance(EnhanceTarget target, string tailVideoPath, string? locationTag = null) {
        ArmGeneration++;
        PendingEnhance = target;
        PendingTransition = null;
        PendingShot = null;
        IsOpen = true;
        ClearFirstFrame();
        ClearLastFrame();
        ResetSpans();
        ResetLocation(locationTag);
        SyncModels(keepSelection: true);
        string moved = MoveToExtendCapableModel();
        ReferenceImages.Clear();
        ReferenceVideos.Clear();
        AddReferenceVideo(tailVideoPath);
        SetTargetSummary($"Extend '{target.ClipName}'");
        if (ReferenceVideos.Count > 0) {
            Message = targetSummary + " — describe what happens next, then Generate." + moved;
        } else {
            ReferencesChanged();   // the clears above still changed the state
            Message = $"{SelectedProvider.Name} has no model that can extend a clip.";
        }
    }

    /// Matches the requested length to the space: the smallest offered
    /// duration that covers it, so the insert can trim to the exact gap.
    /// Only when nothing covers it does the longest available win.
    void PreferDuration(int availableFrames) {
        int needed = (availableFrames + TimelineFps - 1) / TimelineFps;
        var covering = Durations.Where(d => d >= needed).ToList();
        SelectedDuration = covering.Count > 0 ? covering.Min() : Durations.Max();
    }

    /// How much neighbouring footage the generated clip replaces. Zero — the
    /// default — keeps the stills exactly at the cut or gap edges; replacing
    /// is strictly opt-in, because a recapture away from the boundary shows
    /// a different frame than the one the user right-clicked between.
    public IReadOnlyList<double> SpanChoices { get; } = [0, 0.5, 1, 1.5, 2, 2.5, 3, 4, 5];
    [ObservableProperty] double spanBefore;
    [ObservableProperty] double spanAfter;
    bool settingSpans;

    public bool ShowSpanPickers =>
        PendingTransition is { FillsGap: false }
        || PendingShot is { AvailableFrames: > 0, BeforeClipId: not null, AfterClipId: not null };

    partial void OnSpanBeforeChanged(double value) { if (!settingSpans) _ = ApplySpansAsync(); }
    partial void OnSpanAfterChanged(double value) { if (!settingSpans) _ = ApplySpansAsync(); }

    /// Arming resets to zero without recapturing — the boundary stills the
    /// caller just set are already correct for zero spans.
    void ResetSpans() {
        settingSpans = true;
        SpanBefore = 0;
        SpanAfter = 0;
        settingSpans = false;
        OnPropertyChanged(nameof(ShowSpanPickers));
        OnPropertyChanged(nameof(HasPendingPlacement));
    }

    /// A span change moves the endpoints, so the stills, the insert target
    /// and the requested duration all follow. Spans are clamped so at least
    /// one frame of each neighbour survives — a still captured past a short
    /// clip's end shows some other clip entirely, which is worse than a
    /// shorter span. The stills follow the kept-frame convention: first is
    /// the last frame that survives before the replaced region, last is the
    /// first frame that survives after it.
    async Task ApplySpansAsync() {
        int before = (int)Math.Round(SpanBefore * TimelineFps);
        int after = (int)Math.Round(SpanAfter * TimelineFps);
        int anchorStart, anchorEnd;
        if (PendingTransition is { FillsGap: false } t) {
            before = Math.Min(before, Math.Max(0, t.BoundaryFrame - t.LeftClipStartFrame - 1));
            after = Math.Min(after, Math.Max(0, t.RightClipEndFrame - t.BoundaryFrame - 1));
            PendingTransition = t with {
                ReplaceBeforeFrames = before,
                ReplaceAfterFrames = after,
                DurationFrames = before + after > 0 ? before + after : t.DurationFrames,
            };
            anchorStart = anchorEnd = t.BoundaryFrame;
            if (before + after > 0) PreferDuration(before + after);
        } else if (PendingShot is { } s && ShowSpanPickers) {
            int gapEnd = s.StartFrame + s.AvailableFrames;
            before = Math.Min(before, Math.Max(0, s.StartFrame - s.BeforeClipStartFrame - 1));
            after = Math.Min(after, Math.Max(0, s.AfterClipEndFrame - gapEnd - 1));
            PendingShot = s with { ReplaceBeforeFrames = before, ReplaceAfterFrames = after };
            anchorStart = s.StartFrame;
            anchorEnd = gapEnd;
            PreferDuration(s.AvailableFrames + before + after);
        } else {
            return;
        }
        if (CaptureTimelineFrame is null) return;
        int firstFrame = Math.Max(0, anchorStart - before - 1);
        int lastFrame = anchorEnd + after;
        if (await CaptureAt(firstFrame) is { } first) SetFirstFrame(first, firstFrame);
        if (PendingTransition is null && PendingShot is null) return;   // re-armed meanwhile
        if (await CaptureAt(lastFrame) is { } last) SetLastFrame(last, lastFrame);
    }

    const int TimelineFps = 30;

    /// A transition is defined by its two stills, so a text-only endpoint
    /// cannot serve it. Switch to the sibling that takes frames and say so —
    /// silently sending a text-only run would burn money on the wrong thing.
    string MoveToFrameCapableModel() {
        if (CurrentModel is not { AcceptsFrames: false }) return "";
        var capable = Models.FirstOrDefault(m => m.AcceptsFrames);
        if (capable is null) return "";
        ModelId = capable.Id;
        return $" Switched to {capable.Name}, which is the one that takes both frames.";
    }

    /// An enhance cannot run on a model that cannot continue a clip. Switch
    /// to the first that can and say so, as MoveToFrameCapableModel does.
    string MoveToExtendCapableModel() {
        if (CurrentModel is { CanExtend: true }) return "";
        var capable = Models.FirstOrDefault(m => m.CanExtend);
        if (capable is null) return "";
        ModelId = capable.Id;
        return $" Switched to {capable.Name}, which is the one that continues a clip.";
    }

    /// Leaving enhance restores the full picker. A selection the unfiltered
    /// list no longer holds — the extend-only endpoint — falls back to the
    /// top of it rather than staying armed for a request it cannot serve.
    void ClearEnhance() {
        if (PendingEnhance is null) return;
        PendingEnhance = null;
        SyncModels(keepSelection: true);
        if (CurrentModel is null) ModelId = Models.FirstOrDefault()?.Id ?? "";
    }

    public void ClearPlacement() {
        ArmGeneration++;
        PendingTransition = null;
        PendingShot = null;
        ClearEnhance();
        ResetLocation(null);
        SetTargetSummary(DefaultTargetSummary);
        OnPropertyChanged(nameof(ShowSpanPickers));
        OnPropertyChanged(nameof(HasPendingPlacement));
    }

    public bool HasPendingPlacement => PendingTransition is not null || PendingShot is not null;

    /// Asks the shell to extract the clips around the pending target and
    /// attach them as video references — the "give the model the actual
    /// moving footage" path. Lives in the shell because only it can reach
    /// the timeline state and the media files.
    public event Action? VideoContextRequested;

    [RelayCommand]
    void AttachVideoContext() => VideoContextRequested?.Invoke();

    /// References and frames are mutually exclusive on the endpoint; when
    /// video context arrives, the stills step aside.
    public void ClearFramesForReferences() {
        ClearFirstFrame();
        ClearLastFrame();
    }
}

/// Where a generated transition should land: the cut or gap between two clips,
/// and how long the inserted clip should be. `FillsGap` distinguishes the two:
/// a cut is straddled, a gap is filled from its start.
public sealed record TransitionTarget(
    string LeftClipId, string RightClipId, int BoundaryFrame, int DurationFrames) {
    public bool FillsGap { get; init; }

    /// How much footage either side of the cut the transition replaces, in
    /// frames. Zero means straddle-without-replacing (the legacy overlay).
    public int ReplaceBeforeFrames { get; init; }
    public int ReplaceAfterFrames { get; init; }

    /// The neighbours' extents, so span captures and trims can be clamped
    /// inside them — a still past a short clip's end shows another clip.
    public int LeftClipStartFrame { get; init; }
    public int RightClipEndFrame { get; init; } = int.MaxValue;
}

/// Where a generated shot should land: empty space on a track.
/// `AvailableFrames` is 0 when the space is open-ended.
public sealed record ShotTarget(string TrackId, int StartFrame, int AvailableFrames) {
    /// The flanking clips, when they exist — what span replacement extends
    /// into and what video context is extracted from.
    public string? BeforeClipId { get; init; }
    public string? AfterClipId { get; init; }
    public int BeforeClipStartFrame { get; init; }
    public int AfterClipEndFrame { get; init; } = int.MaxValue;
    public int ReplaceBeforeFrames { get; init; }
    public int ReplaceAfterFrames { get; init; }
}

/// Where a finished enhance lands: right after the clip it continues, on
/// that clip's track. Only the id is durable — the landing re-reads the
/// clip's position, so edits made while the run generates are respected.
public sealed record EnhanceTarget(string ClipId, string ClipName);
