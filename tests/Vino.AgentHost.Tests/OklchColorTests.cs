using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

public sealed class OklchColorTests
{
    private static (int A, int R, int G, int B) Channels(int argb) =>
        ((argb >> 24) & 0xFF, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    // The sRGB primaries in OKLCH, from Ottosson's published Oklab values for
    // (1,0,0)/(0,1,0)/(0,0,1) converted to polar form. These anchor the whole conversion:
    // a palette that reproduces the primaries within ±1/255 per channel is running the
    // reference math, not an approximation.
    [Theory]
    [InlineData(0.627955, 0.257683, 29.2338, 255, 0, 0)]
    [InlineData(0.866440, 0.294828, 142.4953, 0, 255, 0)]
    [InlineData(0.452014, 0.313214, 264.0520, 0, 0, 255)]
    public void ReproducesSrgbPrimariesWithinOneStep(
        double l, double c, double hDegrees, int red, int green, int blue)
    {
        var (a, r, g, b) = Channels(new OklchColor(l, c, hDegrees).ToArgb());
        Assert.Equal(0xFF, a);
        Assert.InRange(r, red - 1, red + 1);
        Assert.InRange(g, green - 1, green + 1);
        Assert.InRange(b, blue - 1, blue + 1);
    }

    [Fact]
    public void WhiteAndBlackAreExact()
    {
        Assert.Equal(unchecked((int)0xFFFFFFFF), new OklchColor(1, 0, 0).ToArgb());
        Assert.Equal(unchecked((int)0xFF000000), new OklchColor(0, 0, 0).ToArgb());
    }

    [Fact]
    public void OutOfGamutClampPreservesLightnessAndHue()
    {
        var wild = new OklchColor(0.65, 0.4, 145);
        Assert.False(wild.IsInSrgbGamut());

        var clamped = wild.ClampChromaToSrgb();
        Assert.Equal(wild.L, clamped.L);
        Assert.Equal(wild.HDegrees, clamped.HDegrees);
        Assert.True(clamped.C < wild.C);
        Assert.True(clamped.IsInSrgbGamut());
    }

    [Fact]
    public void InGamutColorClampsToItself()
    {
        var color = new OklchColor(0.65, 0.025, 75);
        Assert.True(color.IsInSrgbGamut());
        Assert.Equal(color, color.ClampChromaToSrgb());
    }

    [Fact]
    public void AlphaIsAlwaysOpaqueEvenOutOfGamut()
    {
        foreach (var color in new[]
        {
            new OklchColor(0.92, 0.02, 85),
            new OklchColor(0.45, 0.09, 62),
            new OklchColor(0.65, 0.4, 145),
            new OklchColor(0.1, 0.35, 264),
        })
        {
            var (a, _, _, _) = Channels(color.ToArgb());
            Assert.Equal(0xFF, a);
        }
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        var color = new OklchColor(0.54, 0.10, 38);
        Assert.Equal(color.ToArgb(), color.ToArgb());
    }
}
