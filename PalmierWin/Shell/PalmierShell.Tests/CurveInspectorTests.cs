using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The inspector's curve glue: commits write the effect through the undo
/// stack (one entry per gesture), previews never do, identity clears the
/// effect, and refresh populates the editors from core state — including
/// macOS-authored projects — without writing back.
public class CurveInspectorTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    /// Mirrors how MainViewModel wires the inspector: same project, timeline,
    /// and undo stack, no shell windows.
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public TimelineViewModel Timeline { get; }
        public UndoStack Undo { get; }
        public InspectorViewModel Inspector { get; }

        public Harness() {
            Timeline = new TimelineViewModel(Project);
            Undo = new UndoStack(Timeline.CaptureSnapshot, Timeline.RestoreSnapshot);
            Inspector = new InspectorViewModel(Project, Timeline, new MediaPanelViewModel(), Undo);
        }

        public string AddSelectedClip() {
            string id = CoreApi.AddClip(Project, TestMediaPath("testsrc.mp4"), 60)!;
            Timeline.Reload();
            Timeline.SelectOnly(id);
            return id;
        }

        public ClipState Clip(string id) => State.FindClip(id)!;
        public TimelineState State => TimelineState.Parse(CoreApi.GetTimelineJson(Project));
        public void Dispose() => CoreApi.palmier_project_destroy(Project);
    }

    [Fact]
    public void CommitCurve_WritesTheCurveParam_AsOneUndoEntry() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        var points = new List<CurvePoint> { new(0, 0), new(0.5, 0.7), new(1, 1) };

        h.Inspector.CommitCurve(GradeChannel.Master, points);

        string json = h.Clip(id).EffectOf("color.curves")?.Text("curve") ?? "";
        Assert.Equal(points, GradeCurve.Parse(json).Master);
        Assert.Equal(points, h.Inspector.CurveGrade.Master);   // refresh read it back
        Assert.Equal(1, h.Undo.Count);

        h.Undo.Undo();
        Assert.Null(h.Clip(id).EffectOf("color.curves"));
    }

    [Fact]
    public void PreviewCurve_TracksTheGesture_WithoutTouchingCoreOrUndo() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        var points = new List<CurvePoint> { new(0, 0.1), new(1, 0.9) };

        h.Inspector.PreviewCurve(GradeChannel.Red, points);

        Assert.Equal(points, h.Inspector.CurveGrade.Red);
        Assert.Equal(0, h.Undo.Count);
        Assert.Null(h.Clip(id).EffectOf("color.curves"));
    }

    [Fact]
    public void CommitCurve_AllChannelsIdentity_RemovesTheEffect() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        h.Inspector.CommitCurve(GradeChannel.Master, [new CurvePoint(0, 0), new CurvePoint(0.5, 0.6), new CurvePoint(1, 1)]);
        Assert.NotNull(h.Clip(id).EffectOf("color.curves"));

        // The editor emits an empty channel for the identity shape.
        h.Inspector.CommitCurve(GradeChannel.Master, []);
        Assert.Null(h.Clip(id).EffectOf("color.curves"));
    }

    [Fact]
    public void CommitCurve_IdentityOnACurvelessClip_IsAQuietNoOp() {
        using var h = new Harness();
        string id = h.AddSelectedClip();

        h.Inspector.CommitCurve(GradeChannel.Master, []);

        Assert.Null(h.Clip(id).EffectOf("color.curves"));
        Assert.Equal(0, h.Undo.Count);
    }

    [Fact]
    public void Refresh_PopulatesFromAnExistingEffect_WithoutWriteBack() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        // As a macOS-authored project arrives: written straight through the ABI.
        var curve = new GradeCurve { Red = [new(0, 0.1), new(0.5, 0.5), new(1, 0.9)] };
        string paramJson = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["curve"] = curve.ToJson() });
        Assert.Equal(1, CoreApi.palmier_clip_set_effect(h.Project, id, "color.curves", paramJson));

        h.Timeline.Reload();

        Assert.Equal(3, h.Inspector.CurveGrade.Red.Count);
        Assert.Equal(0.5, h.Inspector.CurveGrade.Red[1].Y);
        Assert.Equal(0, h.Undo.Count);
        Assert.Equal(curve.ToJson(), h.Clip(id).EffectOf("color.curves")!.Text("curve"));

        h.Timeline.SelectOnly(null);
        Assert.True(h.Inspector.CurveGrade.IsIdentity);
    }

    [Fact]
    public void CommitHueCurve_WritesTheCurvesParam_AsOneUndoEntry() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        var points = new List<CurvePoint> { new(0, 0.5), new(0.5, 0.9), new(1, 0.5) };

        h.Inspector.CommitHueCurve(HueChannel.Sat, points);

        string json = h.Clip(id).EffectOf("color.hueCurves")?.Text("curves") ?? "";
        Assert.Equal(points, HueCurves.Parse(json).HueVsSat);
        Assert.Equal(1, h.Undo.Count);

        h.Undo.Undo();
        Assert.Null(h.Clip(id).EffectOf("color.hueCurves"));
    }

    [Fact]
    public void CommitHueCurve_AllChannelsNeutral_RemovesTheEffect() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        h.Inspector.CommitHueCurve(HueChannel.Sat, [new CurvePoint(0, 0.5), new CurvePoint(0.5, 0.9), new CurvePoint(1, 0.5)]);
        Assert.NotNull(h.Clip(id).EffectOf("color.hueCurves"));

        h.Inspector.CommitHueCurve(HueChannel.Sat, []);
        Assert.Null(h.Clip(id).EffectOf("color.hueCurves"));
    }

    /// A drag on Red previews points into the model; losing the capture
    /// cancels the gesture and the panel answers with a Refresh. A later
    /// commit on Master must not serialize the canceled Red preview.
    [Fact]
    public void CanceledCurveDrag_DoesNotLeakIntoALaterCommitOnAnotherChannel() {
        using var h = new Harness();
        string id = h.AddSelectedClip();
        var redPreview = new List<CurvePoint> { new(0, 0.1), new(1, 0.9) };

        h.Inspector.PreviewCurve(GradeChannel.Red, redPreview);
        Assert.Equal(redPreview, h.Inspector.CurveGrade.Red);
        h.Inspector.Refresh();   // the Canceled handler's recovery
        Assert.Empty(h.Inspector.CurveGrade.Red);

        var master = new List<CurvePoint> { new(0, 0), new(0.5, 0.7), new(1, 1) };
        h.Inspector.CommitCurve(GradeChannel.Master, master);

        string json = h.Clip(id).EffectOf("color.curves")?.Text("curve") ?? "";
        var written = GradeCurve.Parse(json);
        Assert.Equal(master, written.Master);
        Assert.Empty(written.Red);
    }
}
