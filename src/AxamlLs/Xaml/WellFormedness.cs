using System.Text.RegularExpressions;
using System.Xml;

namespace AxamlLs.Xaml;

/// <summary>Strict XML check (the same rules the Avalonia XAML compiler's XML reader enforces).</summary>
public static partial class WellFormedness
{
    /// <summary>Where the XML stops being well formed (0-based line and column) and why, in the XML reader's words.</summary>
    public sealed record XmlError(int Line, int Character, string Message);

    public static XmlError? Check(string text)
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
            return new XmlError(Math.Max(ex.LineNumber - 1, 0), Math.Max(ex.LinePosition - 1, 0), message);
        }
    }

    [GeneratedRegex(@"\s*Line \d+, position \d+\.\s*$")]
    private static partial Regex LinePositionSuffix();
}
