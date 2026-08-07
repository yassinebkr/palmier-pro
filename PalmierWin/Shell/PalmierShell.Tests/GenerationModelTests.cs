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

    [Fact]
    public void AnEndpointThatOnlyExtendsIsMarkedExtendOnly() {
        var extend = ModelManifest.For("fal").Single(m => m.Id == "blackforestlabs/flux-3/extend-video");
        Assert.True(extend.CanExtend);
        Assert.True(extend.ExtendOnly);
    }

    [Fact]
    public void AModelThatExtendsAmongOtherInputsIsNotExtendOnly() {
        var flux = ModelManifest.For("replicate").Single(m => m.Id == "black-forest-labs/flux-3");
        Assert.True(flux.CanExtend);
        Assert.False(flux.ExtendOnly);   // it also takes frames and plain text
    }

    [Fact]
    public void APlainTextToVideoModelNeitherExtendsNorIsExtendOnly() {
        var plain = ModelManifest.For("fal").Single(m => m.Id == "blackforestlabs/flux-3/text-to-video");
        Assert.False(plain.CanExtend);
        Assert.False(plain.ExtendOnly);
    }

    [Fact]
    public void ATextToVideoModelThatAlsoExtendsIsNotExtendOnly() {
        var model = new GenerationModel("x/extend-and-text", "X", [5]) {
            Capabilities = ["textToVideo", "extend"],
        };
        Assert.True(model.CanExtend);
        Assert.False(model.ExtendOnly);   // it still serves plain generations
    }
}
