using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class TimelineTabTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void NewProject_HasOneActiveTimeline() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_timeline_count(project));
            Assert.Equal(0, CoreApi.palmier_project_active_timeline(project));
            Assert.Equal("Timeline 1", CoreApi.TimelineName(project, 0));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddTimeline_BecomesActiveAndStartsEmpty() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60));
            int index = CoreApi.palmier_project_add_timeline(project, "");
            Assert.Equal(1, index);
            Assert.Equal(1, CoreApi.palmier_project_active_timeline(project));
            Assert.Equal("Timeline 2", CoreApi.TimelineName(project, 1));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Empty(state.Tracks.SelectMany(t => t.Clips));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void EditsTargetOnlyTheActiveTimeline() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            CoreApi.palmier_project_add_timeline(project, "B");
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 90);

            Assert.Equal(90, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);
            Assert.Equal(1, CoreApi.palmier_project_set_active_timeline(project, 0));
            Assert.Equal(60, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SetActiveTimeline_RejectsAnUnknownIndex() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(0, CoreApi.palmier_project_set_active_timeline(project, 4));
            Assert.Equal(0, CoreApi.palmier_project_set_active_timeline(project, -1));
            Assert.Equal(0, CoreApi.palmier_project_active_timeline(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RemoveTimeline_RefusesTheLastOne() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(0, CoreApi.palmier_project_remove_timeline(project, 0));
            Assert.Equal(1, CoreApi.palmier_project_timeline_count(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RemoveActiveTimeline_FallsBackToASurvivingOne() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            CoreApi.palmier_project_add_timeline(project, "B");
            Assert.Equal(1, CoreApi.palmier_project_remove_timeline(project, 1));
            Assert.Equal(1, CoreApi.palmier_project_timeline_count(project));
            Assert.Equal(0, CoreApi.palmier_project_active_timeline(project));
            Assert.Equal(60, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RenameTimeline_RejectsEmptyNamesAndUnknownIndexes() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_rename_timeline(project, 0, "Rough Cut"));
            Assert.Equal("Rough Cut", CoreApi.TimelineName(project, 0));
            Assert.Equal(0, CoreApi.palmier_project_rename_timeline(project, 0, ""));
            Assert.Equal(0, CoreApi.palmier_project_rename_timeline(project, 3, "Nope"));
            Assert.Equal("Rough Cut", CoreApi.TimelineName(project, 0));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void UndoOfAnEditMadeOnAnotherTab_SwitchesBackBeforeRestoring() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            int active = 0;
            var undo = new UndoStack(
                () => CoreApi.GetTimelineJson(project),
                json => CoreApi.palmier_timeline_load_json(project, json) == 1,
                () => active,
                index => {
                    if (CoreApi.palmier_project_set_active_timeline(project, index) != 1) return false;
                    active = index;
                    return true;
                });

            undo.Execute("Add Clip", () => CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60) is not null);
            active = CoreApi.palmier_project_add_timeline(project, "B");
            undo.Execute("Add Clip", () => CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 90) is not null);

            undo.Undo();  // second tab's clip
            Assert.Equal(1, active);
            Assert.Equal(0, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);

            undo.Undo();  // first tab's clip — must retarget tab 0
            Assert.Equal(0, active);
            Assert.Equal(0, CoreApi.palmier_project_active_timeline(project));
            Assert.Equal(0, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);

            undo.Redo();
            Assert.Equal(60, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    int scope;  // instance state: xUnit builds a fresh test class per test

    [Fact]
    public void ForgetScope_DropsAClosedTimelinesHistoryAndRebasesTheRest() {
        var undo = new UndoStack(() => "a", _ => true, () => scope, index => { scope = index; return true; });
        undo.Push("on 0", "a", "b");
        scope = 1;
        undo.Push("on 1", "b", "c");
        scope = 2;
        undo.Push("on 2", "c", "d");

        undo.ForgetScope(1);
        undo.Undo();
        Assert.Equal(1, scope);  // the old index 2 rebased down to 1
        undo.Undo();
        Assert.Equal(0, scope);
        Assert.False(undo.CanUndo);
    }
}
