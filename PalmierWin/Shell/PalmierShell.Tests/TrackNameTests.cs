using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class TrackNameTests {
    static TimelineState State(IntPtr project) => TimelineState.Parse(CoreApi.GetTimelineJson(project));

    static TrackState FirstVideo(IntPtr project) => State(project).Tracks.First(t => t.Type == "video");

    [Fact]
    public void ATrackStartsWithNoNameOfItsOwn() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Null(FirstVideo(project).Name);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RenamingATrackShowsInTheSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = FirstVideo(project).Id;
            Assert.Equal(1, CoreApi.palmier_track_rename(project, id, "B-roll"));
            Assert.Equal("B-roll", FirstVideo(project).Name);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AnEmptyNameGoesBackToTheDerivedLabel() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = FirstVideo(project).Id;
            CoreApi.palmier_track_rename(project, id, "B-roll");
            Assert.Equal(1, CoreApi.palmier_track_rename(project, id, "   "));
            Assert.Null(FirstVideo(project).Name);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void NamesAreTrimmed() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.palmier_track_rename(project, FirstVideo(project).Id, "  Dialogue  ");
            Assert.Equal("Dialogue", FirstVideo(project).Name);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AnUnknownTrackIsRefused() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(0, CoreApi.palmier_track_rename(project, "no-such-track", "X"));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ANameSurvivesASaveAndLoadRoundTrip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = FirstVideo(project).Id;
            CoreApi.palmier_track_rename(project, id, "Titles");
            string json = CoreApi.GetTimelineJson(project);

            IntPtr reopened = CoreApi.palmier_project_create();
            try {
                Assert.Equal(1, CoreApi.palmier_timeline_load_json(reopened, json));
                Assert.Equal("Titles", FirstVideo(reopened).Name);
            } finally {
                CoreApi.palmier_project_destroy(reopened);
            }
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
