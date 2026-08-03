using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The composer behaviors the user steers money with: frame nudging, span
/// replacement, and live progress. All exercised without a window.
public class GenerateComposerTests {
    static GeneratePanelViewModel Vm() => new((_, _) => Task.CompletedTask, () => []);

    static string TempPng() {
        string path = Path.Combine(Path.GetTempPath(), $"palmier-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public async Task NudgeRecapturesAtTheSteppedFrameAndMovesTheSlot() {
        var vm = Vm();
        int? captured = null;
        string png = TempPng();
        vm.CaptureTimelineFrame = frame => {
            captured = frame;
            return Task.FromResult<string?>(png);
        };
        vm.SetFirstFrame(png, 100);
        Assert.True(vm.CanNudgeFirst);

        await ((IAsyncRelayCommand)vm.NudgeFirstCommand).ExecuteAsync("-30");
        Assert.Equal(70, captured);
        Assert.Equal(70, vm.FirstFrameNumber);

        await ((IAsyncRelayCommand)vm.NudgeFirstCommand).ExecuteAsync("1");
        Assert.Equal(71, vm.FirstFrameNumber);
        File.Delete(png);
    }

    /// A capture that fails must say so — a silent no-op is indistinguishable
    /// from a dead button, which is exactly how it was reported.
    [Fact]
    public async Task AFailedNudgeExplainsItselfAndChangesNothing() {
        var vm = Vm();
        await vm.Initialized;
        vm.CaptureTimelineFrame = _ => Task.FromResult<string?>(null);
        vm.SetFirstFrame(TempPng(), 100);

        await ((IAsyncRelayCommand)vm.NudgeFirstCommand).ExecuteAsync("30");
        Assert.Equal(100, vm.FirstFrameNumber);
        Assert.Contains("Could not read", vm.Message);
    }

    /// A manual gallery pick has no timeline frame, so there is nothing to
    /// steer and the nudges must not offer themselves.
    [Fact]
    public void AManualPickCannotBeNudged() {
        var vm = Vm();
        vm.CaptureTimelineFrame = _ => Task.FromResult<string?>(null);
        vm.SetFirstFrame(TempPng());
        Assert.False(vm.CanNudgeFirst);
    }

    /// Arming keeps the boundary stills untouched — the regression was a
    /// default span recapturing away from the cut, which put frames the user
    /// never clicked between into both slots.
    [Fact]
    public void ArmingATransitionDoesNotRecaptureTheBoundaryStills() {
        var vm = Vm();
        var captures = new List<int>();
        string png = TempPng();
        vm.CaptureTimelineFrame = frame => {
            captures.Add(frame);
            return Task.FromResult<string?>(png);
        };
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150), png, png, 299, 300);
        Assert.Empty(captures);
        Assert.Equal(0, vm.SpanBefore);
        Assert.Equal(299, vm.FirstFrameNumber);
        Assert.Equal(300, vm.LastFrameNumber);
        File.Delete(png);
    }

    /// Changing a span re-anchors both stills (kept-frame convention: last
    /// surviving frame before, first surviving after) and widens the insert
    /// extent — the pending target must carry the new spans or the insert
    /// would cut a different amount than the stills show.
    [Fact]
    public void SpanChangesFlowIntoThePendingTarget() {
        var vm = Vm();
        var captures = new List<int>();
        string png = TempPng();
        vm.CaptureTimelineFrame = frame => {
            captures.Add(frame);
            return Task.FromResult<string?>(png);
        };
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150) {
            LeftClipStartFrame = 0, RightClipEndFrame = 600,
        }, png, png, 299, 300);

        vm.SpanBefore = 3;
        Assert.Equal(90, vm.PendingTransition!.ReplaceBeforeFrames);
        Assert.Equal(0, vm.PendingTransition.ReplaceAfterFrames);
        Assert.Equal(90, vm.PendingTransition.DurationFrames);
        Assert.Contains(300 - 90 - 1, captures);
        File.Delete(png);
    }

    /// A span past a short neighbour's extent would capture a different clip
    /// entirely — the user's "both frames are wrong" report. Spans clamp so
    /// at least one frame of each neighbour survives.
    [Fact]
    public void SpansClampInsideShortNeighbours() {
        var vm = Vm();
        string png = TempPng();
        vm.CaptureTimelineFrame = _ => Task.FromResult<string?>(png);
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150) {
            LeftClipStartFrame = 280, RightClipEndFrame = 315,
        }, png, png, 299, 300);

        vm.SpanBefore = 3;
        vm.SpanAfter = 3;
        Assert.Equal(300 - 280 - 1, vm.PendingTransition!.ReplaceBeforeFrames);
        Assert.Equal(315 - 300 - 1, vm.PendingTransition.ReplaceAfterFrames);
        File.Delete(png);
    }

    /// Shots take the same spans, extending into both neighbours around the
    /// clicked gap.
    [Fact]
    public void ShotSpansExtendIntoTheNeighbours() {
        var vm = Vm();
        var captures = new List<int>();
        string png = TempPng();
        vm.CaptureTimelineFrame = frame => {
            captures.Add(frame);
            return Task.FromResult<string?>(png);
        };
        vm.BeginShot(new ShotTarget("T", 300, 60) {
            BeforeClipId = "a", AfterClipId = "b",
            BeforeClipStartFrame = 200, AfterClipEndFrame = 460,
        });
        Assert.True(vm.ShowSpanPickers);

        vm.SpanBefore = 1;
        Assert.Equal(30, vm.PendingShot!.ReplaceBeforeFrames);
        Assert.Equal(0, vm.PendingShot.ReplaceAfterFrames);
        Assert.Contains(300 - 30 - 1, captures);
        Assert.Contains(360, captures);
        File.Delete(png);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("loading pipeline...\ndone", null)]
    [InlineData("12%|██        | rendering", 12)]
    [InlineData("37%|███▋      |\n89%|████████▉ |", 89)]
    [InlineData("progress: 100 %", 100)]
    public void ProgressComesFromTheLastPercentInTheLogs(string? logs, int? expected) {
        Assert.Equal(expected, ReplicateProvider.ProgressFromLogs(logs));
    }

    [Fact]
    public void ProcessingStatusCarriesTheLogsProgress() {
        var status = ReplicateProvider.ParseStatus(
            """{"status":"processing","logs":"5%|\n42%| rendering"}""");
        Assert.Equal(GenerationState.Running, status.State);
        Assert.Equal(42, status.Progress);
    }

    [Fact]
    public void KlingModesArePricedApproximately() {
        var pro = GenerationPricing.For("replicate", "kwaivgi/kling-v3-video", 5, "1080p");
        Assert.NotNull(pro);
        Assert.True(pro!.Approximate);
        var standard = GenerationPricing.For("replicate", "kwaivgi/kling-v3-video", 5, "720p");
        Assert.True(standard!.Amount < pro.Amount);
    }
}
