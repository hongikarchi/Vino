namespace GPTino.AgentHost.Hosting;

/// <summary>
/// One OKLCH color (Ottosson's Oklab in polar form) plus the deterministic conversion the
/// layer-curation palette relies on. The palette's canonical values stay OKLCH — perceptually
/// uniform lightness steps, hue that does not drift when L or C change — and sRGB is emitted
/// only at apply time, as an opaque 8-bit ARGB int (the layer-color contract elsewhere).
/// Out-of-gamut colors are brought in by reducing chroma ONLY: lightness and hue are design
/// intent, chroma is the negotiable axis.
/// </summary>
public readonly record struct OklchColor(double L, double C, double HDegrees)
{
    /// <summary>
    /// Opaque ARGB (alpha forced 0xFF) after an sRGB-gamut chroma clamp. Deterministic: the
    /// same OKLCH input always yields the same int, so audits can re-derive and compare.
    /// </summary>
    public int ToArgb()
    {
        var (r, g, b) = ClampChromaToSrgb().ToLinearSrgb();
        return unchecked((int)0xFF000000
            | (ToByte(r) << 16)
            | (ToByte(g) << 8)
            | ToByte(b));
    }

    /// <summary>True when the color converts to linear sRGB with every channel inside [0, 1].</summary>
    public bool IsInSrgbGamut()
    {
        var (r, g, b) = ToLinearSrgb();
        const double epsilon = 1e-6;
        return r >= -epsilon && r <= 1 + epsilon
            && g >= -epsilon && g <= 1 + epsilon
            && b >= -epsilon && b <= 1 + epsilon;
    }

    /// <summary>
    /// The nearest in-gamut color with the SAME lightness and hue — chroma is reduced by
    /// bisection until the color fits sRGB. In-gamut inputs return unchanged. Lightness outside
    /// [0, 1] cannot be repaired by chroma reduction: the result stays out of gamut and
    /// <see cref="ToArgb"/> saturates per channel (white/black). The palette parser rejects such
    /// values, so only hand-constructed colors can reach that degenerate path.
    /// </summary>
    public OklchColor ClampChromaToSrgb()
    {
        if (IsInSrgbGamut())
        {
            return this;
        }
        double low = 0, high = C;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (low + high) / 2;
            if ((this with { C = middle }).IsInSrgbGamut())
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return this with { C = low };
    }

    // Ottosson's reference Oklab -> linear sRGB (https://bottosson.github.io/posts/oklab/),
    // preceded by the polar -> cartesian step. Constants are the published ones verbatim.
    private (double R, double G, double B) ToLinearSrgb()
    {
        var hRadians = HDegrees * Math.PI / 180.0;
        var a = C * Math.Cos(hRadians);
        var b = C * Math.Sin(hRadians);

        var l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;

        return (
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static int ToByte(double linear)
    {
        var srgb = linear <= 0.0031308
            ? 12.92 * linear
            : 1.055 * Math.Pow(Math.Max(linear, 0), 1.0 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(srgb, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
