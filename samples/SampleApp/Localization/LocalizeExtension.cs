using Avalonia.Markup.Xaml;

namespace SampleApp.Localization;

/// <summary>A custom markup extension: {loc:Localize some_key}.</summary>
public sealed class LocalizeExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public string? Fallback { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Fallback ?? Key;
}
