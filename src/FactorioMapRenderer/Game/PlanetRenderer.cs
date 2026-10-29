using System.Runtime.InteropServices;
using SkiaSharp;

namespace FactorioMapRenderer.Game;

/// <summary>
/// The planets as globes, from the textures the game wraps around its 3D planets (graphics/space: 2048 × 1024, one
/// picture of the whole surface from pole to pole), far sharper than the 512 px pictures of the star map. Drawn as
/// the game shows them: lit from the sun's side, with their clouds, their glowing lava where they have it, and a
/// thin atmosphere at the edge.
/// </summary>
internal static class PlanetRenderer
{
    /// <summary>Diameter of the globe in pixels; the picture is a little larger, for the atmosphere.</summary>
    public const int Diameter = 1024;
    private const int Margin = 40;
    public const int Size = Diameter + 2 * Margin;

    /// <summary>Draws the planet whose textures are <paramref name="surface"/> (and its -cloud and -emission files); false if there are none.</summary>
    public static bool Render(string surface, string target)
    {
        if (!File.Exists(surface))
            return false;
        using var albedo = Decode(surface)!;
        using var clouds = Decode(surface.Replace(".png", "-cloud.png"));
        using var emission = Decode(surface.Replace(".png", "-emission.png"));

        // The textures are the bare colour of the ground; the game brightens them by its lighting. Each planet gets
        // the exposure that brings its lit side to a natural brightness, but dark worlds (Vulcanus) stay dark.
        double mean = MeanLuminance(albedo);
        double exposure = Math.Clamp(0.42 / Math.Max(mean, 0.01), 1.0, 2.6);
        var atmosphere = Average(clouds ?? albedo);

        var pixels = new int[Size * Size];
        double radius = Diameter / 2.0, centre = Size / 2.0;
        // the sun from the upper left, a little in front (the star map turns the picture towards its sun)
        var (lx, ly, lz) = Normalize(-0.55, -0.42, 0.72);
        // the axis tilted a little towards the viewer, as globes are usually shown
        const double tilt = 0.32;
        double cosTilt = Math.Cos(tilt), sinTilt = Math.Sin(tilt);

        for (int py = 0; py < Size; py++)
        for (int px = 0; px < Size; px++)
        {
            double u = (px + 0.5 - centre) / radius, v = (py + 0.5 - centre) / radius;
            double d2 = u * u + v * v;
            if (d2 > 1)
            {
                // atmosphere: a soft glow just outside the edge, brightest on the sunlit side
                double out_ = (Math.Sqrt(d2) - 1) * radius / Margin;
                if (out_ >= 1)
                    continue;
                double side = Math.Clamp(0.35 + 0.65 * (-(u * lx + v * ly) / Math.Sqrt(d2)), 0, 1);
                double glow = Math.Pow(1 - out_, 3) * 0.55 * side;
                pixels[py * Size + px] = Pack(atmosphere.R, atmosphere.G, atmosphere.B, glow);
                continue;
            }
            double z = Math.Sqrt(1 - d2);
            // point on the globe → latitude and longitude, with the tilt
            double y = -v * cosTilt + z * sinTilt, zz = v * sinTilt + z * cosTilt;
            double lat = Math.Asin(Math.Clamp(y, -1, 1)), lon = Math.Atan2(u, zz);
            double tx = (lon / (2 * Math.PI) + 0.5) * albedo.Width, ty = (0.5 - lat / Math.PI) * albedo.Height;

            var (r, g, b, _) = Sample(albedo, tx, ty);
            double light = Math.Max(0, u * lx + v * ly + z * lz);
            double shade = 0.08 + 1.05 * light;
            r *= exposure * shade;
            g *= exposure * shade;
            b *= exposure * shade;

            if (clouds is not null)
            {
                var (cr, cg, cb, ca) = Sample(clouds, tx * clouds.Width / albedo.Width, ty * clouds.Height / albedo.Height);
                double cover = ca * 0.4 * Math.Max(Math.Max(cr, cg), cb);
                double cloudLight = 0.06 + 1.1 * light;
                r += (cr * cloudLight - r) * cover;
                g += (cg * cloudLight - g) * cover;
                b += (cb * cloudLight - b) * cover;
            }
            if (emission is not null)
            {
                // lava glows by itself, most visibly on the night side
                var (er, eg, eb, ea) = Sample(emission, tx * emission.Width / albedo.Width, ty * emission.Height / albedo.Height);
                double glow = ea * (0.55 + 0.6 * (1 - light));
                r += er * glow;
                g += eg * glow;
                b += eb * glow;
            }
            // the atmosphere thickens towards the edge
            double haze = Math.Pow(1 - z, 2.5) * 0.5 * (0.25 + light);
            r += (atmosphere.R - r) * haze;
            g += (atmosphere.G - g) * haze;
            b += (atmosphere.B - b) * haze;
            // a soft edge instead of a jagged one
            double edge = Math.Clamp((1 - Math.Sqrt(d2)) * radius, 0, 1);
            pixels[py * Size + px] = Pack(r, g, b, edge);
        }

        using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var stream = File.Create(target);
        return bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    }

    private static SKBitmap? Decode(string file)
    {
        if (!File.Exists(file))
            return null;
        using var decoded = SKBitmap.Decode(file);
        if (decoded is null)
            return null;
        // one layout for every texture: grey ones (the clouds) become grey RGBA
        var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(decoded);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, 0, 0, paint);
        return bitmap;
    }

    /// <summary>Bilinear sample, wrapping around in longitude; components 0–1.</summary>
    private static (double R, double G, double B, double A) Sample(SKBitmap bitmap, double x, double y)
    {
        var span = bitmap.GetPixelSpan();
        int w = bitmap.Width, h = bitmap.Height;
        x -= 0.5;
        y = Math.Clamp(y - 0.5, 0, h - 1);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j < 2; j++)
        for (int i = 0; i < 2; i++)
        {
            int sx = ((x0 + i) % w + w) % w, sy = Math.Min(y0 + j, h - 1);
            double weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            int o = (sy * w + sx) * 4;
            r += span[o] * weight;
            g += span[o + 1] * weight;
            b += span[o + 2] * weight;
            a += span[o + 3] * weight;
        }
        return (r / 255, g / 255, b / 255, a / 255);
    }

    private static double MeanLuminance(SKBitmap bitmap)
    {
        var span = bitmap.GetPixelSpan();
        double sum = 0;
        int count = 0;
        for (int o = 0; o < span.Length; o += 4 * 16)
        {
            sum += (0.2126 * span[o] + 0.7152 * span[o + 1] + 0.0722 * span[o + 2]) / 255;
            count++;
        }
        return sum / Math.Max(count, 1);
    }

    /// <summary>The planet's typical colour, lightened: the colour of its atmosphere.</summary>
    private static (double R, double G, double B) Average(SKBitmap bitmap)
    {
        var span = bitmap.GetPixelSpan();
        double r = 0, g = 0, b = 0;
        int count = 0;
        for (int o = 0; o < span.Length; o += 4 * 16)
        {
            if (span[o + 3] < 16)
                continue;
            r += span[o];
            g += span[o + 1];
            b += span[o + 2];
            count++;
        }
        count = Math.Max(count, 1);
        double max = Math.Max(Math.Max(r, g), Math.Max(b, 1));
        // keep the hue, raise it to a light tone
        return (0.45 + 0.55 * r / max, 0.45 + 0.55 * g / max, 0.45 + 0.55 * b / max);
    }

    private static (double, double, double) Normalize(double x, double y, double z)
    {
        double l = Math.Sqrt(x * x + y * y + z * z);
        return (x / l, y / l, z / l);
    }

    private static int Pack(double r, double g, double b, double a)
    {
        static uint C(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
        return (int)(C(a) << 24 | C(b) << 16 | C(g) << 8 | C(r));
    }
}
