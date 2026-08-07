using System.Net;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

public class LocationContextTests {
    const string ParisTag = "+48.8566+002.3522/";

    const string ParisJson = """
        {"place_id":1,"lat":"48.857","lon":"2.352",
         "display_name":"Eiffel Tower, Avenue Anatole France, 75007 Paris, France",
         "address":{"city":"Paris","country":"France"}}
        """;

    static string TempDir() => Path.Combine(Path.GetTempPath(), $"palmier-geo-{Guid.NewGuid():N}");

    static void Cleanup(string dir) {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    sealed class StubHandler : HttpMessageHandler {
        readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;
        public int Calls;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
            this.respond = respond;
        public StubHandler(string json)
            : this(_ => Task.FromResult(Json(json))) { }
        public static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            Interlocked.Increment(ref Calls);
            return respond(request);
        }
    }

    [Fact]
    public void ParsesTheIPhoneDecimalForm() {
        var coords = LocationContext.ParseIso6709(ParisTag);
        Assert.NotNull(coords);
        Assert.Equal(48.8566, coords.Value.Lat);
        Assert.Equal(2.3522, coords.Value.Lon);
    }

    [Fact]
    public void ParsesNegativeCoordinates() {
        var coords = LocationContext.ParseIso6709("-33.8688-151.2093/");
        Assert.NotNull(coords);
        Assert.Equal(-33.8688, coords.Value.Lat);
        Assert.Equal(-151.2093, coords.Value.Lon);
    }

    [Fact]
    public void ParsesWithAnAltitudeGroup() {
        var coords = LocationContext.ParseIso6709("+48.8566+002.3522+035.0/");
        Assert.NotNull(coords);
        Assert.Equal(48.8566, coords.Value.Lat);
        Assert.Equal(2.3522, coords.Value.Lon);
    }

    [Fact]
    public void ToleratesAMissingTrailingSlash() {
        Assert.NotNull(LocationContext.ParseIso6709("+48.8566+002.3522"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Paris")]
    [InlineData("+48.8566/")]
    [InlineData("48.8566+002.3522")]
    [InlineData("+4852.36+00221.12/")]   // degrees/minutes form: out of scope, not misparsed
    public void RejectsNonDecimalForms(string tag) {
        Assert.Null(LocationContext.ParseIso6709(tag));
    }

    [Theory]
    [InlineData("+91.0+002.0/")]
    [InlineData("-90.1+000.0/")]
    [InlineData("+48.0+181.0/")]
    [InlineData("+48.0-180.5/")]
    public void RejectsOutOfRangeCoordinates(string tag) {
        Assert.Null(LocationContext.ParseIso6709(tag));
    }

    [Fact]
    public async Task FreeTextTagsPassThroughWithoutTheNetwork() {
        var handler = new StubHandler(ParisJson);
        var geo = new GeocodeService(handler, TempDir());
        Assert.Equal("Paris, France", await geo.DescribeAsync("Paris, France"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyTagsResolveToNull(string tag) {
        var handler = new StubHandler(ParisJson);
        var geo = new GeocodeService(handler, TempDir());
        Assert.Null(await geo.DescribeAsync(tag));
        Assert.Equal(0, handler.Calls);
    }

    /// Privacy: a coordinate-shaped tag that does not parse is still
    /// coordinates. It resolves to nothing rather than leaking raw numbers
    /// into a prompt through the free-text path.
    [Fact]
    public async Task UnparseableCoordinateShapedTagsResolveToNull() {
        var handler = new StubHandler(ParisJson);
        var geo = new GeocodeService(handler, TempDir());
        Assert.Null(await geo.DescribeAsync("+4852.36+00221.12/"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GeocodeComposesCityAndCountry() {
        var dir = TempDir();
        var geo = new GeocodeService(new StubHandler(ParisJson), dir);
        Assert.Equal("Paris, France", await geo.DescribeAsync(ParisTag));
        Cleanup(dir);
    }

    [Fact]
    public async Task GeocodeFallsBackThroughTheAddressFields() {
        const string json = """{"address":{"village":"Giverny","country":"France"}}""";
        var dir = TempDir();
        var geo = new GeocodeService(new StubHandler(json), dir);
        Assert.Equal("Giverny, France", await geo.DescribeAsync(ParisTag));
        Cleanup(dir);
    }

    [Fact]
    public async Task GeocodeFallsBackToATruncatedDisplayName() {
        const string json =
            """{"display_name":"Eiffel Tower, Avenue Anatole France, 75007 Paris, France"}""";
        var dir = TempDir();
        var geo = new GeocodeService(new StubHandler(json), dir);
        Assert.Equal("Eiffel Tower, Avenue Anatole France", await geo.DescribeAsync(ParisTag));
        Cleanup(dir);
    }

    [Fact]
    public async Task ACachedResultSkipsTheNetwork() {
        var dir = TempDir();
        var handler = new StubHandler(ParisJson);
        var geo = new GeocodeService(handler, dir);
        Assert.Equal("Paris, France", await geo.DescribeAsync(ParisTag));
        Assert.Equal("Paris, France", await geo.DescribeAsync(ParisTag));
        Assert.Equal(1, handler.Calls);
        Cleanup(dir);
    }

    [Fact]
    public async Task TheDiskCacheServesNewServiceInstances() {
        var dir = TempDir();
        var first = new GeocodeService(new StubHandler(ParisJson), dir);
        Assert.Equal("Paris, France", await first.DescribeAsync(ParisTag));

        var handler = new StubHandler(ParisJson);
        var second = new GeocodeService(handler, dir);
        Assert.Equal("Paris, France", await second.DescribeAsync(ParisTag));
        Assert.Equal(0, handler.Calls);
        Cleanup(dir);
    }

    [Fact]
    public async Task ConcurrentLookupsShareOneFlight() {
        var dir = TempDir();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async _ => {
            entered.TrySetResult();
            await release.Task;
            return StubHandler.Json(ParisJson);
        });
        var geo = new GeocodeService(handler, dir);

        var first = geo.DescribeAsync(ParisTag);
        var second = geo.DescribeAsync(ParisTag);
        await entered.Task;   // the shared flight reached the network
        Assert.Equal(1, handler.Calls);
        release.SetResult();
        Assert.Equal("Paris, France", await first);
        Assert.Equal("Paris, France", await second);
        Cleanup(dir);
    }

    [Fact]
    public async Task AFailedLookupResolvesToNullWithoutThrowing() {
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var geo = new GeocodeService(handler, TempDir());
        Assert.Null(await geo.DescribeAsync(ParisTag));
    }

    [Fact]
    public async Task AFailedLookupIsNotRetriedWithinTheSession() {
        var dir = TempDir();
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var geo = new GeocodeService(handler, dir);
        Assert.Null(await geo.DescribeAsync(ParisTag));
        Assert.Null(await geo.DescribeAsync(ParisTag));
        Assert.Equal(1, handler.Calls);
        Cleanup(dir);
    }

    [Fact]
    public async Task AFailedLookupIsNeverWrittenToTheDiskCache() {
        var dir = TempDir();
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var geo = new GeocodeService(handler, dir);
        Assert.Null(await geo.DescribeAsync(ParisTag));
        Assert.False(File.Exists(Path.Combine(dir, "geocode-cache.json")));
        Cleanup(dir);
    }

    static GeneratePanelViewModel Vm() => new((_, _) => Task.CompletedTask, () => []);

    [Fact]
    public async Task TheSettingLineFollowsTheToggle() {
        var vm = Vm();
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150), "from.png", "to.png",
                           locationTag: "Paris, France");
        vm.Prompt = "a motorbike weaves through traffic";
        Assert.True(vm.HasLocationContext);
        Assert.DoesNotContain("Setting:", vm.FinalPrompt);

        vm.UseLocationContext = true;
        await vm.LocationResolution;
        Assert.EndsWith("\nSetting: Paris, France", vm.FinalPrompt);
        Assert.Equal("Setting: Paris, France", vm.LocationStatus);

        vm.UseLocationContext = false;
        await vm.LocationResolution;
        Assert.DoesNotContain("Setting:", vm.FinalPrompt);
    }

    [Fact]
    public async Task AnIsoTagResolvesThroughTheDescriberNeverIntoThePrompt() {
        var vm = Vm();
        string? queried = null;
        vm.DescribeLocation = tag => {
            queried = tag;
            return Task.FromResult<string?>("Paris, France");
        };
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150), "from.png", "to.png",
                           locationTag: ParisTag);
        vm.Prompt = "a motorbike weaves through traffic";
        vm.UseLocationContext = true;
        await vm.LocationResolution;

        Assert.Equal(ParisTag, queried);                     // raw tag went only to the resolver
        Assert.EndsWith("\nSetting: Paris, France", vm.FinalPrompt);
        Assert.DoesNotContain("48.8566", vm.FinalPrompt);    // never the coordinates
    }

    [Fact]
    public async Task AFailedLookupSendsNoSettingLine() {
        var vm = Vm();
        vm.DescribeLocation = _ => Task.FromResult<string?>(null);
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150), "from.png", "to.png",
                           locationTag: ParisTag);
        vm.Prompt = "a motorbike weaves through traffic";
        vm.UseLocationContext = true;
        await vm.LocationResolution;

        Assert.Equal("location unavailable", vm.LocationStatus);
        Assert.DoesNotContain("Setting:", vm.FinalPrompt);
    }

    [Fact]
    public async Task TheToggleDefaultsOffOnEveryArm() {
        var vm = Vm();
        vm.BeginShot(new ShotTarget("T", 300, 60), locationTag: "Lisbon, Portugal");
        Assert.False(vm.UseLocationContext);
        vm.UseLocationContext = true;
        await vm.LocationResolution;

        vm.BeginShot(new ShotTarget("T", 600, 60), locationTag: "Lisbon, Portugal");
        Assert.False(vm.UseLocationContext);
        Assert.Null(vm.LocationStatus);
        Assert.DoesNotContain("Setting:", vm.FinalPrompt);
    }

    [Fact]
    public void NoLocationTagOffersNoToggle() {
        var vm = Vm();
        vm.BeginTransition(new TransitionTarget("L", "R", 300, 150), "from.png", "to.png");
        Assert.False(vm.HasLocationContext);
    }

    [Fact]
    public async Task ClearingThePlacementDropsTheLocationContext() {
        var vm = Vm();
        vm.BeginShot(new ShotTarget("T", 300, 60), locationTag: "Lisbon, Portugal");
        Assert.True(vm.HasLocationContext);
        vm.ClearPlacement();
        Assert.False(vm.HasLocationContext);
        Assert.False(vm.UseLocationContext);
        await vm.LocationResolution;   // no surprise status write afterwards
        Assert.Null(vm.LocationStatus);
    }
}
