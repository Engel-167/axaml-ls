using AxamlLs.Workspace;

namespace AxamlLs.Analysis;

/// <summary>A diagnostic to report, and whether the editor should fade its range instead of underlining it.</summary>
public readonly record struct ReportedDiagnostic(XamlDiagnostic Diagnostic, bool Unnecessary);

/// <summary>
/// Everything reported for a document: the analyzer's own diagnostics plus those that need other files (assets,
/// resources, selector classes). Those are added here, after analysis, because the project providers are set after it.
/// </summary>
public static class DocumentDiagnostics
{
    public static List<ReportedDiagnostic> Collect(XamlAnalyzer analyzer)
    {
        var all = new List<ReportedDiagnostic>();
        all.AddRange(analyzer.Result.Diagnostics.Select(d => new ReportedDiagnostic(d, false)));
        all.AddRange(Features.AssetDiagnostics(analyzer).Select(d => new ReportedDiagnostic(d, false)));
        all.AddRange(ResourceCheck.Unknown(analyzer).Select(d => new ReportedDiagnostic(d, false)));
        all.AddRange(Selectors.Validate(analyzer).Select(d => new ReportedDiagnostic(d, false)));
        if (analyzer.StyleProvider?.Invoke() is ProjectStyles styles)
            all.AddRange(Selectors.UnusedClasses(analyzer.Result.Doc, styles.UsedClasses).Select(d => new ReportedDiagnostic(d, true)));
        return all;
    }
}
