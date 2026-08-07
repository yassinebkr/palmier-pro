using PalmierShell.Core;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Enhance mode: a clip's tail becomes the source video, the picker narrows
/// to the models that can extend, and the finished take lands right after
/// the clip it continues.
public class EnhanceTests {
    const string ExtendVideoId = "blackforestlabs/flux-3/extend-video";
    const string ReplicateFluxId = "black-forest-labs/flux-3";

    static GeneratePanelViewModel Vm() => new((_, _) => Task.CompletedTask, () => []);

    static IGenerationProvider Fal => GenerationProviders.ById("fal")!;

    static string TempPng() {
        string path = Path.Combine(Path.GetTempPath(), $"palmier-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public void AnExtendOnlyModelIsNotOfferedForPlainGenerations() {
        var vm = Vm();
        vm.SelectedProvider = Fal;
        Assert.DoesNotContain(ExtendVideoId, vm.ModelChoices);
        Assert.Contains("blackforestlabs/flux-3/text-to-video", vm.ModelChoices);
    }

    [Fact]
    public void AModelThatAlsoExtendsStaysInTheNormalPicker() {
        var vm = Vm();   // Replicate is the default provider
        Assert.Contains(ReplicateFluxId, vm.ModelChoices);
    }

    [Fact]
    public void ArmingAnEnhanceNarrowsThePickerToExtendModels() {
        var vm = Vm();
        vm.SelectedProvider = Fal;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal([ExtendVideoId], vm.ModelChoices);
    }

    [Fact]
    public void ArmingAnEnhanceOnReplicateOffersTheSharedFluxEndpoint() {
        var vm = Vm();
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal([ReplicateFluxId], vm.ModelChoices);
    }

    [Fact]
    public async Task BeginEnhanceArmsThePendingEnhanceAndSaysSo() {
        var vm = Vm();
        await vm.Initialized;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal("clip-1", vm.PendingEnhance!.ClipId);
        Assert.True(vm.IsOpen);
        Assert.StartsWith("Extend 'Clip One' — describe what happens next, then Generate.",
                          vm.Message);
    }

    [Fact]
    public async Task BeginEnhanceAttachesTheTailAsVideo1() {
        var vm = Vm();
        await vm.Initialized;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        var chip = Assert.Single(vm.ReferenceVideos);
        Assert.Equal("tail.mp4", chip.Path);
        Assert.Equal("[Video1]", chip.Label);
    }

    [Fact]
    public async Task BeginEnhanceClearsTheFrameSlots() {
        var vm = Vm();
        await vm.Initialized;
        string png = TempPng();
        vm.SetFirstFrame(png, 100);
        vm.SetLastFrame(png, 200);
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.False(vm.HasFirstFrame);
        Assert.False(vm.HasLastFrame);
        File.Delete(png);
    }

    [Fact]
    public async Task BeginEnhanceSwitchesOffAModelThatCannotExtend() {
        var vm = Vm();
        await vm.Initialized;
        vm.ModelId = "bytedance/seedance-2.0";
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal(ReplicateFluxId, vm.ModelId);
        Assert.Contains("Switched to FLUX.3", vm.Message);
    }

    [Fact]
    public async Task BeginEnhanceKeepsAnAlreadyExtendCapableModel() {
        var vm = Vm();
        await vm.Initialized;
        vm.ModelId = ReplicateFluxId;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal(ReplicateFluxId, vm.ModelId);
        Assert.DoesNotContain("Switched", vm.Message);
    }

    [Fact]
    public async Task RearmingReplacesThePendingEnhanceAndTheSourceVideo() {
        var vm = Vm();
        await vm.Initialized;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "first-tail.mp4");
        vm.BeginEnhance(new EnhanceTarget("clip-2", "Clip Two"), "second-tail.mp4");
        Assert.Equal("clip-2", vm.PendingEnhance!.ClipId);
        Assert.Equal("second-tail.mp4", Assert.Single(vm.ReferenceVideos).Path);
        Assert.Contains("Clip Two", vm.Message);
    }

    [Fact]
    public async Task ClearingThePlacementRestoresTheFullPicker() {
        var vm = Vm();
        await vm.Initialized;
        vm.SelectedProvider = Fal;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal([ExtendVideoId], vm.ModelChoices);

        vm.ClearPlacement();
        Assert.Null(vm.PendingEnhance);
        Assert.DoesNotContain(ExtendVideoId, vm.ModelChoices);
        Assert.Contains("blackforestlabs/flux-3/text-to-video", vm.ModelChoices);
        Assert.NotEqual(ExtendVideoId, vm.ModelId);
    }

    /// The armed extend model is the narrowed list's first entry, which is
    /// exactly where the saved-model restore is allowed to write — so an
    /// unscoped restore would de-select it mid-arm.
    [Fact]
    public async Task ASavedPlainModelDoesNotReplaceTheArmedExtendModel() {
        var vm = Vm();
        await vm.Initialized;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal(ReplicateFluxId, vm.ModelId);

        vm.LoadSettings = () =>
            AppSettings.Default.WithModel("generate:replicate", "bytedance/seedance-2.0");
        await vm.RefreshKeyAsync();
        Assert.Equal(ReplicateFluxId, vm.ModelId);
    }

    [Fact]
    public async Task ASavedModelStillRestoresOverAnUntouchedDefault() {
        var vm = Vm();
        await vm.Initialized;
        vm.ModelId = vm.Models[0].Id;
        vm.LoadSettings = () =>
            AppSettings.Default.WithModel("generate:replicate", "google/veo-3");
        await vm.RefreshKeyAsync();
        Assert.Equal("google/veo-3", vm.ModelId);
    }

    /// The request is the same assembly path Generate uses — checked here
    /// without spending money.
    [Fact]
    public async Task TheSubmitCarriesTheTailAsTheReferenceVideo() {
        var vm = Vm();
        await vm.Initialized;
        vm.TunePrompt = false;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        vm.Prompt = "the crowd keeps dancing";

        var request = vm.BuildRequest();
        Assert.Equal("tail.mp4", Assert.Single(request.ReferenceVideos));
        Assert.Equal("the crowd keeps dancing", request.Prompt);
        Assert.Equal(ReplicateFluxId, request.Model);
    }

    /// The source video is the whole request; without it the endpoint would
    /// be paid to refuse. The chip's × must not become a broken run.
    [Fact]
    public async Task GenerateRefusesAnEnhanceWhoseSourceVideoWasRemoved() {
        var vm = Vm();
        await vm.Initialized;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        vm.RemoveReferenceCommand.Execute(vm.ReferenceVideos[0]);
        vm.HasApiKey = true;
        vm.Prompt = "the crowd keeps dancing";

        vm.GenerateCommand.Execute(null);
        Assert.Contains("re-arm", vm.Message);
        Assert.Empty(vm.Jobs);
    }

    [Fact]
    public async Task TheEnhanceSourceLocationArmsTheSameOptInToggle() {
        var vm = Vm();
        await vm.Initialized;
        vm.TunePrompt = false;
        vm.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4",
                        locationTag: "Paris, France");
        Assert.True(vm.HasLocationContext);
        Assert.False(vm.UseLocationContext);

        vm.Prompt = "the crowd keeps dancing";
        vm.UseLocationContext = true;
        await vm.LocationResolution;
        Assert.EndsWith("\nSetting: Paris, France", vm.FinalPrompt);
    }

    /// The landing seam: a live project and the undo stack, wired exactly as
    /// MainViewModel wires InsertEnhance.
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public TimelineViewModel Timeline { get; }
        public UndoStack Undo { get; }

        public Harness() {
            Timeline = new TimelineViewModel(Project);
            Undo = new UndoStack(Timeline.CaptureSnapshot, Timeline.RestoreSnapshot);
        }

        public TimelineState State => TimelineState.Parse(CoreApi.GetTimelineJson(Project));
        public List<ClipState> VideoClips => State.Tracks.First(t => t.Type == "video").Clips;
        public void Dispose() => CoreApi.palmier_project_destroy(Project);
    }

    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void TheTakeLandsRightAfterTheSourceClipUnderOneUndoEntry() {
        using var h = new Harness();
        string source = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;

        bool placed = h.Undo.Execute("Enhance Clip",
            () => MainViewModel.PlaceEnhance(h.Project, new EnhanceTarget(source, "testsrc"),
                                             TestMediaPath("click.mp4"), 45));
        Assert.True(placed);
        Assert.Equal(2, h.VideoClips.Count);
        Assert.Equal(60, h.VideoClips[1].StartFrame);
        Assert.Equal(45, h.VideoClips[1].DurationFrames);

        h.Undo.Undo();
        Assert.Equal(source, Assert.Single(h.VideoClips).Id);
    }

    [Fact]
    public void TheTakeFollowsTheSourceClipOntoItsTrack() {
        using var h = new Harness();
        string source = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        string v2 = CoreApi.AddTrack(h.Project, "video")!;
        Assert.Equal(1, CoreApi.palmier_timeline_move_clip_to_track(h.Project, source, v2, 0));

        Assert.True(MainViewModel.PlaceEnhance(h.Project, new EnhanceTarget(source, "testsrc"),
                                               TestMediaPath("click.mp4"), 45));
        var v2Clips = h.State.Tracks.Single(t => t.Id == v2).Clips;
        Assert.Equal(2, v2Clips.Count);
        Assert.Equal(60, v2Clips[1].StartFrame);
    }

    [Fact]
    public void ASourceClipEditedAwayFailsInsteadOfPlacing() {
        using var h = new Harness();
        string source = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        Assert.Equal(1, CoreApi.palmier_timeline_remove_clip(h.Project, source));

        Assert.False(MainViewModel.PlaceEnhance(h.Project, new EnhanceTarget(source, "testsrc"),
                                                TestMediaPath("click.mp4"), 45));
        Assert.Empty(h.VideoClips);
    }

    /// The span after the source clip may be taken: AddClipAt's own
    /// semantics apply, as with any drop onto occupied space — the sitting
    /// clip is overwritten, never a silent refusal.
    [Fact]
    public void AnOccupiedLandingSpotKeepsTheOverwriteSemantics() {
        using var h = new Harness();
        string source = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        CoreApi.AddClipAt(h.Project, TestMediaPath("testsrc.mp4"), 30, 60);

        Assert.True(MainViewModel.PlaceEnhance(h.Project, new EnhanceTarget(source, "testsrc"),
                                               TestMediaPath("click.mp4"), 45));
        Assert.Equal(2, h.VideoClips.Count);
        Assert.Equal(60, h.VideoClips[1].StartFrame);
        Assert.Equal(45, h.VideoClips[1].DurationFrames);
    }

    /// The extraction finishes after the user has armed something else: the
    /// stale completion must not wipe the newer arm.
    [Fact]
    public async Task AStaleExtractionDoesNotClobberANewerArm() {
        using var h = new Harness();
        var composer = Vm();
        await composer.Initialized;
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();
        var release = new ManualResetEventSlim();

        var arming = MainViewModel.BeginEnhanceAsync(h.Timeline, composer, clipId,
            (_, _, _) => { release.Wait(); return "tail.mp4"; }, _ => null);
        composer.BeginShot(new ShotTarget("T", 300, 60));
        release.Set();
        await arming;

        Assert.NotNull(composer.PendingShot);
        Assert.Null(composer.PendingEnhance);
        Assert.Empty(composer.ReferenceVideos);
    }

    /// A failed re-arm must not leave the previous enhance armed while the
    /// message names the clip that just failed.
    [Fact]
    public async Task AFailedRearmClearsThePreviousEnhance() {
        using var h = new Harness();
        var composer = Vm();
        await composer.Initialized;
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();

        await MainViewModel.BeginEnhanceAsync(h.Timeline, composer, clipId,
            (_, _, _) => "tail.mp4", _ => null);
        Assert.NotNull(composer.PendingEnhance);

        await MainViewModel.BeginEnhanceAsync(h.Timeline, composer, clipId,
            (_, _, _) => null, _ => null);
        Assert.False(composer.HasPendingPlacement);
        Assert.Contains("Could not extract", composer.Message);
    }
}
