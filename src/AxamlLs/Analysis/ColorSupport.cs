using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AxamlLs.Workspace;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Avalonia color literals: #RGB, #ARGB, #RRGGBB, #AARRGGBB and the named colors (Red, Transparent…).</summary>
public static class ColorSupport
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, uint>> KnownByAssembly = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, uint> Empty = new Dictionary<string, uint>();

    /// <summary>
    /// The named colors with their ARGB values, read from Avalonia's internal KnownColor enum in Avalonia.Base.dll
    /// (exactly what Color.Parse accepts), so there is no hand-copied table to drift.
    /// </summary>
    public static IReadOnlyDictionary<string, uint> KnownColors(ProjectSnapshot? project)
    {
        var path = project?.Compilation.References.OfType<PortableExecutableReference>()
            .Select(r => r.FilePath).FirstOrDefault(f => f != null && Path.GetFileName(f) == "Avalonia.Base.dll");
        if (path == null) return Empty;
        return KnownByAssembly.GetOrAdd(path, Read);
    }

    private static IReadOnlyDictionary<string, uint> Read(string path)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(th);
                if (md.GetString(type.Name) != "KnownColor" || md.GetString(type.Namespace) != "Avalonia.Media") continue;
                foreach (var fh in type.GetFields())
                {
                    var field = md.GetFieldDefinition(fh);
                    var constant = field.GetDefaultValue();
                    if (constant.IsNil) continue;
                    var c = md.GetConstant(constant);
                    var blob = md.GetBlobReader(c.Value);
                    uint value = c.TypeCode switch
                    {
                        ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                        ConstantTypeCode.Int32 => unchecked((uint)blob.ReadInt32()),
                        _ => 0,
                    };
                    var name = md.GetString(field.Name);
                    if (name != "None") result[name] = value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read KnownColor from {path}: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Color syntaxes Avalonia's Color.Parse accepts (checked against Avalonia 12.1.3), as zeroed snippet templates.
    /// Not accepted by Avalonia: CSS4 space syntax "rgb(0 0 0)", "0deg", hwb(), oklch(), "0x…".
    /// </summary>
    public static readonly (string Label, string Snippet, string Detail)[] Templates =
    [
        ("#RRGGBB", "#${1:000000}", "hex"),
        ("#AARRGGBB", "#${1:FF}${2:000000}", "hex with alpha first (FF = opaque)"),
        ("#RGB", "#${1:000}", "short hex"),
        ("#ARGB", "#${1:F}${2:000}", "short hex with alpha first"),
        ("rgb(r,g,b)", "rgb(${1:0},${2:0},${3:0})", "0–255 or %"),
        ("rgba(r,g,b,a)", "rgba(${1:0},${2:0},${3:0},${4:1})", "alpha 0–1, % or 0–255"),
        ("hsl(h,s%,l%)", "hsl(${1:0},${2:0}%,${3:0}%)", "hue 0–360"),
        ("hsla(h,s%,l%,a)", "hsla(${1:0},${2:0}%,${3:0}%,${4:1})", "hue 0–360, alpha 0–1"),
        ("hsv(h,s%,v%)", "hsv(${1:0},${2:0}%,${3:0}%)", "hue 0–360"),
        ("hsva(h,s%,v%,a)", "hsva(${1:0},${2:0}%,${3:0}%,${4:1})", "hue 0–360, alpha 0–1"),
    ];

    public static bool TryParse(string text, IReadOnlyDictionary<string, uint> known, out uint argb)
    {
        argb = 0;
        var s = text.Trim();
        if (TryParseFunction(s, out argb)) return true;
        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            switch (hex.Length)
            {
                case 3: // RGB
                    argb = 0xFF000000 | Expand(v >> 8) << 16 | Expand(v >> 4) << 8 | Expand(v);
                    return true;
                case 4: // ARGB
                    argb = Expand(v >> 12) << 24 | Expand(v >> 8) << 16 | Expand(v >> 4) << 8 | Expand(v);
                    return true;
                case 6:
                    argb = 0xFF000000 | v;
                    return true;
                case 8:
                    argb = v;
                    return true;
                default:
                    return false;
            }
        }
        return known.TryGetValue(s, out argb);
    }

    /// <summary>rgb()/rgba()/hsl()/hsla()/hsv()/hsva() with comma-separated arguments, as Avalonia reads them.</summary>
    private static bool TryParseFunction(string s, out uint argb)
    {
        argb = 0;
        var open = s.IndexOf('(');
        if (open <= 0 || !s.EndsWith(')')) return false;
        var name = s[..open].Trim().ToLowerInvariant();
        var parts = s[(open + 1)..^1].Split(',').Select(p => p.Trim()).ToArray();
        if (name is not ("rgb" or "rgba" or "hsl" or "hsla" or "hsv" or "hsva") || parts.Length is < 3 or > 4) return false;

        double alpha = 1;
        if (parts.Length == 4 && !TryAlpha(parts[3], out alpha)) return false;
        double r, g, b;
        if (name.StartsWith("rgb", StringComparison.Ordinal))
        {
            if (!TryChannel(parts[0], out r) || !TryChannel(parts[1], out g) || !TryChannel(parts[2], out b)) return false;
        }
        else
        {
            if (!TryNumber(parts[0], out var h) || !TryUnit(parts[1], out var sat) || !TryUnit(parts[2], out var third)) return false;
            (r, g, b) = name.StartsWith("hsl", StringComparison.Ordinal) ? FromHsl(h, sat, third) : FromHsv(h, sat, third);
        }
        static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
        argb = Byte(alpha) << 24 | Byte(r) << 16 | Byte(g) << 8 | Byte(b);
        return true;
    }

    private static bool TryNumber(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>A percentage, or a fraction 0–1.</summary>
    private static bool TryUnit(string s, out double v)
    {
        if (s.EndsWith('%') && TryNumber(s[..^1], out v))
        {
            v /= 100;
            return true;
        }
        return TryNumber(s, out v);
    }

    /// <summary>An RGB channel: 0–255 or a percentage.</summary>
    private static bool TryChannel(string s, out double v)
    {
        if (s.EndsWith('%')) return TryUnit(s, out v);
        if (!TryNumber(s, out v)) return false;
        v /= 255;
        return true;
    }

    /// <summary>Alpha: a percentage, a fraction 0–1, or a byte 0–255 (Avalonia reads 128 as 50%).</summary>
    private static bool TryAlpha(string s, out double v)
    {
        if (!TryUnit(s, out v)) return false;
        if (!s.EndsWith('%') && v > 1) v /= 255;
        return true;
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        return FromChroma(h, c, l - c / 2);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        return FromChroma(h, c, v - c);
    }

    private static (double R, double G, double B) FromChroma(double h, double c, double m)
    {
        h = (h % 360 + 360) % 360 / 60;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return (r + m, g + m, b + m);
    }

    /// <summary>The same color written in each Avalonia syntax (for the color picker's write-back).</summary>
    public static IEnumerable<string> Presentations(uint argb)
    {
        var a = (argb >> 24) / 255.0;
        var (r, g, b) = (argb >> 16 & 0xFF, argb >> 8 & 0xFF, argb & 0xFF);
        string A(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
        yield return Hex(argb);
        yield return a >= 1 ? $"rgb({r},{g},{b})" : $"rgba({r},{g},{b},{A(a)})";
        var (h, s, l) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
        yield return a >= 1 ? $"hsl({Math.Round(h)},{Math.Round(s * 100)}%,{Math.Round(l * 100)}%)"
            : $"hsla({Math.Round(h)},{Math.Round(s * 100)}%,{Math.Round(l * 100)}%,{A(a)})";
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, s, l);
    }

    private static uint Expand(uint nibble)
    {
        nibble &= 0xF;
        return nibble << 4 | nibble;
    }

    /// <summary>#RRGGBB when opaque, else #AARRGGBB.</summary>
    /// <summary>A 14×14 rounded square in the color as a markdown image (Zed renders data: URIs in hover and docs).</summary>
    public static string Swatch(uint argb)
    {
        var opacity = (argb >> 24) / 255.0;
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"14\" height=\"14\"><rect x=\"0.5\" y=\"0.5\" width=\"13\" height=\"13\" rx=\"2\" fill=\"#{argb & 0xFFFFFF:X6}\" " +
                  $"fill-opacity=\"{opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" stroke=\"#808080\" stroke-opacity=\"0.6\"/></svg>";
        return $"![]({"data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg))})";
    }

    public static string Hex(uint argb) => argb >> 24 == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";
}
