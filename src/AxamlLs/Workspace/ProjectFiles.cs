using System.Text.RegularExpressions;

namespace AxamlLs.Workspace;

public static partial class ProjectFiles
{
    /// <summary>The .csproj that owns a file: the nearest one walking up the directory tree.</summary>
    public static string? FindOwningProject(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(dir))
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(dir, "*.csproj");
            }
            catch (Exception)
            {
                projects = [];
            }
            if (projects.Length == 1) return projects[0];
            if (projects.Length > 1)
            {
                var dirName = Path.GetFileName(dir);
                return projects.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == dirName) ?? projects.OrderBy(p => p.Length).First();
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || path.Contains($"{sep}bin{sep}", StringComparison.Ordinal) ||
               path.Contains($"{sep}.git{sep}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether {Binding} means a compiled binding: AvaloniaUseCompiledBindingsByDefault in the project or a
    /// Directory.Build.props, else Avalonia's default (true from Avalonia 12, false before).
    /// </summary>
    public static bool DetectCompiledBindingsDefault(string projectPath)
    {
        foreach (var file in ProjectAndBuildProps(projectPath))
        {
            var m = CompiledBindingsProperty().Match(SafeRead(file));
            if (m.Success) return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }
        var major = DetectAvaloniaMajorVersion(projectPath);
        return major >= 12;
    }

    public static int? DetectAvaloniaMajorVersion(string projectPath)
    {
        var files = ProjectAndBuildProps(projectPath).ToList();
        var dir = Path.GetDirectoryName(projectPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var central = Path.Combine(dir, "Directory.Packages.props");
            if (File.Exists(central)) files.Add(central);
            dir = Path.GetDirectoryName(dir);
        }
        foreach (var file in files)
        {
            var m = AvaloniaVersion().Match(SafeRead(file));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var major)) return major;
        }
        return null;
    }

    private static IEnumerable<string> ProjectAndBuildProps(string projectPath)
    {
        yield return projectPath;
        var dir = Path.GetDirectoryName(projectPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props)) yield return props;
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return "";
        }
    }

    [GeneratedRegex(@"<AvaloniaUseCompiledBindingsByDefault>\s*(true|false)\s*</", RegexOptions.IgnoreCase)]
    private static partial Regex CompiledBindingsProperty();

    [GeneratedRegex(@"Include=""Avalonia""\s+Version=""\[?(\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex AvaloniaVersion();
}
