using AxamlLs;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Text;
using AxamlLs.Workspace;

if (args.Length > 0 && args[0] is "--version" or "-v")
{
    Console.WriteLine(typeof(LspServer).Assembly.GetName().Version?.ToString(3));
    return 0;
}

if (args.Length > 0 && args[0] == "check")
{
    return await CheckAsync(args.Skip(1).ToArray());
}

if (args.Contains("--verbose")) Log.Verbose = true;
using var server = new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();
return 0;

// `axaml-ls check <file-or-dir>...` prints diagnostics like a compiler and exits with 1 when there are errors.
static async Task<int> CheckAsync(string[] targets)
{
    var noProject = targets.Contains("--no-project");
    var files = new List<string>();
    foreach (var t in targets.Where(t => !t.StartsWith("--", StringComparison.Ordinal)))
    {
        if (Directory.Exists(t))
            files.AddRange(Directory.EnumerateFiles(t, "*.axaml", SearchOption.AllDirectories).Where(f => !ProjectFiles.IsBuildOutput(f)));
        else if (File.Exists(t)) files.Add(Path.GetFullPath(t));
        else Console.Error.WriteLine($"not found: {t}");
    }
    if (files.Count == 0)
    {
        Console.Error.WriteLine("usage: axaml-ls check [--no-project] <file-or-directory>...");
        return 2;
    }

    using var workspace = new WorkspaceManager();
    var errors = 0;
    var warnings = 0;
    foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
    {
        var host = noProject ? null : workspace.GetHostFor(file);
        if (host != null) await host.EnsureLoadedAsync();
        var text = await File.ReadAllTextAsync(file);
        var analyzer = XamlAnalyzer.Analyze(text, host?.Current, host?.CompiledBindingsDefault ?? false);
        var lines = new LineIndex(text);
        foreach (var d in analyzer.Result.Diagnostics.OrderBy(d => d.Start))
        {
            var (line, col) = lines.GetPosition(d.Start);
            var severity = d.Severity == Severity.Error ? "error" : d.Severity == Severity.Warning ? "warning" : "info";
            if (d.Severity == Severity.Error) errors++;
            else warnings++;
            Console.WriteLine($"{Path.GetRelativePath(Environment.CurrentDirectory, file)}({line + 1},{col + 1}): {severity} {d.Code}: {d.Message}");
        }
    }
    Console.WriteLine($"{files.Count} file(s), {errors} error(s), {warnings} warning(s)");
    return errors > 0 ? 1 : 0;
}
