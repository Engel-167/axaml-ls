using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Loads samples/SampleApp once (needs `dotnet restore samples/SampleApp`, which `mise run test` does).</summary>
public sealed class SampleProjectFixture : IAsyncLifetime
{
    public static string SampleDir { get; } = FindSampleDir();
    public ProjectHost Host { get; } = new(Path.Combine(SampleDir, "SampleApp.csproj"));
    public ProjectSnapshot Project => Host.Current ?? throw new InvalidOperationException($"Sample project did not load: {Host.LoadError}");

    public Task InitializeAsync() => Host.EnsureLoadedAsync();

    public Task DisposeAsync()
    {
        Host.Dispose();
        return Task.CompletedTask;
    }

    public XamlAnalyzer Analyze(string text) => XamlAnalyzer.Analyze(text, Project, Host.CompiledBindingsDefault);

    public XamlAnalyzer AnalyzeFile(string relative) => Analyze(File.ReadAllText(Path.Combine(SampleDir, relative)));

    private static string FindSampleDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples", "SampleApp"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("samples/SampleApp"), "samples", "SampleApp");
    }
}

[CollectionDefinition("sample")]
public sealed class SampleCollection : ICollectionFixture<SampleProjectFixture>;
