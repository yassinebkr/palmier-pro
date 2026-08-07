using Avalonia.Data;
using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class DoubleTextConverterTests {
    [Fact]
    public void EmptyAndInvalidTextPushNothing() {
        Assert.Same(BindingOperations.DoNothing, DoubleTextConverter.Instance.ConvertBack("", typeof(double), null, null));
        Assert.Same(BindingOperations.DoNothing, DoubleTextConverter.Instance.ConvertBack("   ", typeof(double), null, null));
        Assert.Same(BindingOperations.DoNothing, DoubleTextConverter.Instance.ConvertBack("1,2,3", typeof(double), null, null));
    }

    [Fact]
    public void ValidTextParsesWithTheGivenCulture() {
        var fr = new System.Globalization.CultureInfo("fr-FR");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(0.5d, DoubleTextConverter.Instance.ConvertBack("0,5", typeof(double), null, fr));
        Assert.Equal(0.5d, DoubleTextConverter.Instance.ConvertBack("0.5", typeof(double), null, inv));
        Assert.Same(BindingOperations.DoNothing,
            DoubleTextConverter.Instance.ConvertBack("0,5", typeof(double), null, inv));
    }

    [Fact]
    public void DoubleFormatsShort() {
        Assert.Equal("0,5", DoubleTextConverter.Instance.Convert(0.5d, typeof(string), null, null)
            .ToString()!.Replace(".", ","));
    }
}
