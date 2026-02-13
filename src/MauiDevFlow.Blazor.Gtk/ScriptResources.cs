using System.Reflection;

namespace MauiDevFlow.Blazor.Gtk;

/// <summary>
/// Loads embedded JS resource files from the assembly.
/// </summary>
internal static class ScriptResources
{
    private static readonly Assembly Assembly = typeof(ScriptResources).Assembly;
    private static readonly Dictionary<string, string> Cache = new();

    internal static string Load(string filename)
    {
        if (Cache.TryGetValue(filename, out var cached))
            return cached;

        var resourceName = $"MauiDevFlow.Blazor.Gtk.Resources.Scripts.{filename.Replace('/', '.')}";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Cache[filename] = content;
        return content;
    }
}
