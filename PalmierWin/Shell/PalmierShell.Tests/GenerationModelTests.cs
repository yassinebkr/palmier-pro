using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public class GenerationModelTests {
    [Fact]
    public void SeedanceIsOfferedByBothProviders() {
        Assert.Contains(GenerationProviders.ById("replicate")!.Models,
            m => m.Id == "bytedance/seedance-2.0");
        Assert.Contains(GenerationProviders.ById("fal")!.Models,
            m => m.Id == "bytedance/seedance-2.0/text-to-video");
    }

    [Fact]
    public void ModelIdsAreUniqueWithinAProvider() {
        foreach (var provider in GenerationProviders.All) {
            var ids = provider.Models.Select(m => m.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}
