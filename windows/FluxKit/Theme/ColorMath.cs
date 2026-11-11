namespace FluxKit;

// A color in OKLCH: the lightness L from 0 to 1, the chroma C, and the hue H
// in radians.
public readonly record struct Oklch(double L, double C, double H);

// A minimum contrast Ratio of a color against the color Against.
public readonly record struct ContrastNeed
{
    public int Against { get; }
    public double Ratio { get; }

    // The luminance of Against, so that the guard computes it once.
    internal double Lum { get; }

    public ContrastNeed(int against, double ratio)
    {
        Against = against;
        Ratio = ratio;
        Lum = ColorMath.Luminance(against);
    }
}

// A color as its 3 sRGB channels, for the WPF brushes.
public readonly record struct Rgb(byte R, byte G, byte B)
{
    // The color of 0xRRGGBB. The alpha byte does not count.
    public static Rgb Of(int color) => new((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    // The color as 0xRRGGBB.
    public int Value => (R << 16) | (G << 8) | B;

    // The color as "#rrggbb".
    public string Hex => ColorMath.Hex(Value);
}

// The color math of the theme engine. A color is an int 0xRRGGBB. Each
// function ignores the alpha byte. ColorMath.swift of the Mac app has the
// same math, and the tests check the same values. Swift rounds a half away
// from zero, so each rounding here does the same.
public static class ColorMath
{
    // The color without its alpha byte.
    public static int Opaque(int color) => color & 0xFFFFFF;

    static int Channel(int color, int shift) => (color >> shift) & 0xFF;

    static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    // Decodes 1 sRGB channel, from 0 to 255, to linear light.
    static double Linear(int c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    // Encodes linear light to 1 sRGB channel, from 0 to 255.
    static int Encode(double v)
    {
        var c = Math.Min(Math.Max(v, 0), 1);
        var s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        var scaled = Round(s * 255);
        if (!double.IsFinite(scaled))
        {
            return 0;
        }
        return Math.Clamp((int)scaled, 0, 255);
    }

    // The relative luminance of WCAG 2.2, from 0 for black to 1 for white.
    public static double Luminance(int color)
    {
        var r = 0.2126 * Linear(Channel(color, 16));
        var g = 0.7152 * Linear(Channel(color, 8));
        var b = 0.0722 * Linear(Channel(color, 0));
        return r + g + b;
    }

    // The contrast ratio of WCAG 2.2, from 1 to 21. The order of the colors
    // does not matter.
    public static double Contrast(int a, int b) => Ratio(Luminance(a), Luminance(b));

    static double Ratio(double la, double lb) => la > lb ? (la + 0.05) / (lb + 0.05) : (lb + 0.05) / (la + 0.05);

    // The color at t on the sRGB line from a to b. t = 0 gives a, and t = 1
    // gives b.
    public static int Mix(int a, int b, double t)
    {
        int M(int shift)
        {
            double x = Channel(a, shift);
            double y = Channel(b, shift);
            var v = Round(x + (y - x) * t);
            if (!double.IsFinite(v))
            {
                return 0;
            }
            return Math.Clamp((int)v, 0, 255) << shift;
        }
        return M(16) | M(8) | M(0);
    }

    // Converts a color to OKLCH.
    public static Oklch ToOklch(int color)
    {
        var r = Linear(Channel(color, 16));
        var g = Linear(Channel(color, 8));
        var b = Linear(Channel(color, 0));
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var lab = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        return new Oklch(lab, double.Hypot(a, bb), Math.Atan2(bb, a));
    }

    // The distance of 2 colors in OKLAB, from 0 for the same color. 0.02 is
    // about the smallest difference that people see. 0.1 is a clear
    // difference.
    public static double Distance(int a, int b)
    {
        var x = ToOklch(a);
        var y = ToOklch(b);
        var da = x.C * Math.Cos(x.H) - y.C * Math.Cos(y.H);
        var db = x.C * Math.Sin(x.H) - y.C * Math.Sin(y.H);
        var dl = x.L - y.L;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    // Converts OKLAB to linear sRGB. The values can be outside 0 to 1.
    static (double R, double G, double B) LinearRgb(double l, double a, double b)
    {
        var l3 = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m3 = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s3 = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        var r = 4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3;
        var g = -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3;
        var bl = -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3;
        return (r, g, bl);
    }

    const double GamutEpsilon = 1e-4;

    static bool InGamut((double R, double G, double B) v) =>
        InGamut(v.R) && InGamut(v.G) && InGamut(v.B);

    static bool InGamut(double v) => v >= -GamutEpsilon && v <= 1 + GamutEpsilon;

    // Converts OKLCH to a color. A color outside sRGB loses chroma until it
    // fits. The lightness and the hue stay.
    public static int FromOklch(Oklch c)
    {
        (double R, double G, double B) At(double chroma) => LinearRgb(c.L, chroma * Math.Cos(c.H), chroma * Math.Sin(c.H));
        var v = At(c.C);
        if (!InGamut(v))
        {
            var lo = 0.0;
            var hi = c.C;
            for (var i = 0; i < 24; i++)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(At(mid)))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            v = At(lo);
        }
        return (Encode(v.R) << 16) | (Encode(v.G) << 8) | Encode(v.B);
    }

    // True when color meets each of the needs.
    public static bool Meets(int color, IReadOnlyList<ContrastNeed> needs) =>
        needs.All(n => Contrast(color, n.Against) >= n.Ratio);

    // The step of the lightness search, in OKLCH lightness.
    const double LightnessStep = 0.002;

    // The contrast guard. It moves only the OKLCH lightness of color until
    // the color meets each need, and it keeps the hue. It takes the smallest
    // move, and it tries the lighter side first when lighter is true. A color
    // that meets the needs stays as it is. When no lightness meets each need,
    // the guard returns the color that comes closest.
    public static int Guarded(int color, IReadOnlyList<ContrastNeed> needs, bool lighter)
    {
        var start = Opaque(color);
        if (needs.Count == 0)
        {
            return start;
        }
        double Score(int x)
        {
            var lx = Luminance(x);
            var least = double.MaxValue;
            foreach (var n in needs)
            {
                least = Math.Min(least, Ratio(lx, n.Lum) / n.Ratio);
            }
            return least;
        }
        var best = start;
        var bestScore = Score(start);
        if (bestScore >= 1)
        {
            return start;
        }
        var o = ToOklch(start);
        double[] sides = lighter ? [1, -1] : [-1, 1];
        var steps = (int)(1 / LightnessStep) + 1;
        for (var k = 1; k <= steps; k++)
        {
            var any = false;
            foreach (var side in sides)
            {
                var l = o.L + side * k * LightnessStep;
                if (l < 0 || l > 1)
                {
                    continue;
                }
                any = true;
                var x = FromOklch(new Oklch(l, o.C, o.H));
                var s = Score(x);
                if (s >= 1)
                {
                    return x;
                }
                if (s > bestScore)
                {
                    best = x;
                    bestScore = s;
                }
            }
            if (!any)
            {
                break;
            }
        }
        return best;
    }

    // The color with the hue of the mix of baseColor and toward at t, and
    // with the luminance of baseColor. Each color keeps its contrast against
    // it, as against baseColor.
    public static int Tint(int baseColor, int toward, double t)
    {
        var o = ToOklch(Mix(baseColor, toward, t));
        var target = Luminance(baseColor);
        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Luminance(FromOklch(new Oklch(mid, o.C, o.H))) < target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        var a = FromOklch(new Oklch(lo, o.C, o.H));
        var b = FromOklch(new Oklch(hi, o.C, o.H));
        return Math.Abs(Luminance(a) - target) < Math.Abs(Luminance(b) - target) ? a : b;
    }

    // Reduces the chroma of a color to limit at most. The lightness and the
    // hue stay.
    public static int LimitChroma(int color, double limit)
    {
        var o = ToOklch(color);
        return o.C <= limit ? Opaque(color) : FromOklch(new Oklch(o.L, limit, o.H));
    }

    // The color on the sRGB line from "from" to "to" whose contrast against
    // "from" is target. When "to" is not far enough from "from", it returns
    // "to".
    public static int StepTo(int from, int to, double target)
    {
        if (Contrast(from, to) <= target)
        {
            return Opaque(to);
        }
        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Contrast(from, Mix(from, to, mid)) < target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return Mix(from, to, hi);
    }

    // Writes 0xRRGGBB as "#rrggbb".
    public static string Hex(int color) => "#" + Opaque(color).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

    // Reads "#rrggbb" or "#rrggbbaa", with or without "#", as 0xRRGGBB. It
    // drops the alpha.
    public static int? ParseColor(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }
        if (s.Length is not (6 or 8) || !s.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        return int.Parse(s.AsSpan(0, 6), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
    }
}
