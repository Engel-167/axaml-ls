using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary>A "where is it" query at the cursor, answered with LSP locations.</summary>
internal abstract class LocationQueryHandler(Documents documents, string capability) : SyncRequestHandler
{
    protected abstract List<DefinitionTarget> Query(XamlAnalyzer analyzer, int offset);

    public override void Advertise(JsonObject capabilities) => capabilities[capability] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyzeAt(p, out var doc, out var analysis, out var offset)) return null;
        var array = new JsonArray();
        foreach (var target in Query(analysis.Analyzer, offset)) array.Add(Protocol.Location(target, doc.Uri, analysis.Lines));
        return array;
    }
}

/// <summary><c>textDocument/definition</c>: the view model member, class, handler, element or file a value names.</summary>
internal sealed class DefinitionHandler(Documents documents) : LocationQueryHandler(documents, "definitionProvider")
{
    public override string Method => "textDocument/definition";
    protected override List<DefinitionTarget> Query(XamlAnalyzer analyzer, int offset) => Definition.At(analyzer, offset);
}

/// <summary><c>textDocument/typeDefinition</c>: the type of the symbol under the cursor, or the <c>x:DataType</c> in scope.</summary>
internal sealed class TypeDefinitionHandler(Documents documents) : LocationQueryHandler(documents, "typeDefinitionProvider")
{
    public override string Method => "textDocument/typeDefinition";
    protected override List<DefinitionTarget> Query(XamlAnalyzer analyzer, int offset) => Navigation.TypeDefinition(analyzer, offset);
}

/// <summary><c>textDocument/implementation</c>: the code-behind class, or an event's handler method.</summary>
internal sealed class ImplementationHandler(Documents documents) : LocationQueryHandler(documents, "implementationProvider")
{
    public override string Method => "textDocument/implementation";
    protected override List<DefinitionTarget> Query(XamlAnalyzer analyzer, int offset) => Navigation.Implementation(analyzer, offset);
}
