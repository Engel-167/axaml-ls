using System.Buffers.Binary;
using AxamlLs.Analysis;

namespace AxamlLs.Tests;

/// <summary>Image sizes from headers and small previews (performance plan P6).</summary>
public class AssetPreviewTests
{
    private static ImageSize? Read(byte[] bytes) => ImageInfo.Read(new MemoryStream(bytes));

    private static byte[] Png(int width, int height, int padTo = 33)
    {
        var bytes = new byte[Math.Max(padTo, 33)];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] Riff(string chunk, Action<byte[]> fill)
    {
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        fill(bytes);
        return bytes;
    }

    [Fact]
    public void Png_gif_and_bmp_sizes_come_from_their_headers()
    {
        Assert.Equal(new ImageSize(640, 480), Read(Png(640, 480)));

        var gif = new byte[32];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 120);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 90);
        Assert.Equal(new ImageSize(120, 90), Read(gif));

        var bmp = new byte[32];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 300);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -200); // top-down
        Assert.Equal(new ImageSize(300, 200), Read(bmp));

        var core = new byte[32];
        "BM"u8.CopyTo(core);
        BinaryPrimitives.WriteUInt32LittleEndian(core.AsSpan(14), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(18), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(20), 32);
        Assert.Equal(new ImageSize(64, 32), Read(core));
    }

    [Fact]
    public void Ico_reports_its_largest_picture()
    {
        var ico = new byte[6 + 2 * 16];
        ico[2] = 1;
        ico[4] = 2;
        ico[6] = 16;
        ico[7] = 16;
        // The second entry is 256×256, written as zeros.
        Assert.Equal(new ImageSize(256, 256), Read(ico));
    }

    [Fact]
    public void WebP_lossy_lossless_and_extended_headers()
    {
        var lossy = Riff("VP8 ", b =>
        {
            b[23] = 0x9D;
            b[24] = 0x01;
            b[25] = 0x2A;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 800);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 600);
        });
        Assert.Equal(new ImageSize(800, 600), Read(lossy));

        var lossless = Riff("VP8L", b =>
        {
            b[20] = 0x2F;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21), (uint)((100 - 1) | ((50 - 1) << 14)));
        });
        Assert.Equal(new ImageSize(100, 50), Read(lossless));

        var extended = Riff("VP8X", b =>
        {
            b[24] = unchecked((byte)(1920 - 1));
            b[25] = (byte)((1920 - 1) >> 8);
            b[27] = unchecked((byte)(1080 - 1));
            b[28] = (byte)((1080 - 1) >> 8);
        });
        Assert.Equal(new ImageSize(1920, 1080), Read(extended));
    }

    [Fact]
    public void Jpeg_size_comes_from_the_frame_after_skipping_other_segments()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, .. new byte[14], // APP0, 16 bytes with its length
            0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00, // a short DQT
            0xFF, 0xC2, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, .. new byte[9], // progressive SOF: 480 high, 640 wide
        ];
        Assert.Equal(new ImageSize(640, 480), Read(jpeg));
    }

    [Fact]
    public void Unknown_or_cut_short_files_have_no_size()
    {
        Assert.Null(Read("<svg/>"u8.ToArray()));
        Assert.Null(Read(Png(10, 10)[..20]));
        Assert.Null(Read([0xFF, 0xD8, 0xFF, 0xD9]));
    }

    [Fact]
    public void An_image_over_the_cap_shows_its_size_but_is_not_embedded()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-preview-" + Guid.NewGuid().ToString("N")));
        try
        {
            var big = Path.Combine(dir.FullName, "big.png");
            File.WriteAllBytes(big, Png(4000, 3000, padTo: (int)AssetPreview.MaxPreviewBytes + 1));
            var markdown = AssetPreview.Markdown(big, dir.FullName)!;
            Assert.DoesNotContain("data:", markdown);
            Assert.Equal("`/big.png` · 4000×3000 · 256 KB", markdown);

            var small = Path.Combine(dir.FullName, "small.png");
            File.WriteAllBytes(small, Png(16, 16));
            Assert.StartsWith("![small.png](data:image/png;base64,", AssetPreview.Markdown(small, dir.FullName));
            Assert.EndsWith("`/small.png` · 16×16 · 33 B", AssetPreview.Markdown(small, dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Svg_minification_drops_editor_data_and_keeps_paths_byte_identical()
    {
        const string first = "M 10,10 L 20,20   C 30 30,\n 40 40, 50 50 Z";
        const string second = "m0 0h24v24H0z";
        var svg = $"""
            <?xml version="1.0"?>
            <!-- Created with Inkscape -->
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
                 xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" inkscape:version="1.3" viewBox="0 0 24 24">
              <sodipodi:namedview id="base" pagecolor="#ffffff"><inkscape:grid type="xygrid"/></sodipodi:namedview>
              <metadata><rdf:RDF><cc:Work/></rdf:RDF></metadata>
              <g inkscape:label="Layer 1" inkscape:groupmode="layer">
                <path d="{first}" style="fill:#000"/>
                <path sodipodi:nodetypes="cc" d="{second}"/>
              </g>
            </svg>
            """;
        var minified = AssetPreview.Minify(svg);
        Assert.Contains($"d=\"{first}\"", minified);
        Assert.Contains($"d=\"{second}\"", minified);
        foreach (var gone in (string[])["<!--", "inkscape", "sodipodi", "<metadata", "rdf:RDF", "namedview"])
            Assert.DoesNotContain(gone, minified);
        Assert.Contains("viewBox=\"0 0 24 24\"", minified);
        Assert.True(minified.Length < svg.Length / 2, $"{minified.Length} of {svg.Length}");
    }
}
