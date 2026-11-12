using System.Text.RegularExpressions;
using System.Xml;

namespace AxamlLs.Xaml;

/// <summary>Strict XML check (the same rules the Avalonia XAML compiler's XML reader enforces).</summary>
public static partial class WellFormedness
{
    public sealed record Error(int Line, int Character, string Message);

    public static Error? Check(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
            }
            return null;
        }
        catch (XmlException ex)
        {
            var message = LinePositionSuffix().Replace(ex.Message, "");
            return new Error(Math.Max(ex.LineNumber - 1, 0), Math.Max(ex.LinePosition - 1, 0), message);
        }
    }

    [GeneratedRegex(@"\s*Line \d+, position \d+\.\s*$")]
    private static partial Regex LinePositionSuffix();
}
