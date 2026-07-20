using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenCode.Core;

public abstract record ConfigEntry
{
    public abstract string Type { get; }
}

public record ConfigDocument : ConfigEntry
{
    public override string Type => "document";
    public string? Path { get; init; }
    public ConfigInfo Info { get; init; } = null!;

    public ConfigDocument(ConfigInfo info, string? path = null)
    {
        Info = info;
        Path = path;
    }
}

public record ConfigDirectory : ConfigEntry
{
    public override string Type => "directory";
    public string Path { get; init; } = null!;
}

public static class ConfigLoader
{
    static readonly string[] ConfigFileNames = { "opencode.json", "opencode.jsonc" };

    public static ConfigInfo? TryParse(string text)
    {
        try
        {
            var cleaned = StripComments(text);
            return JsonSerializer.Deserialize<ConfigInfo>(cleaned);
        }
        catch
        {
            return null;
        }
    }

    static string StripComments(string text)
    {
        var result = new System.Text.StringBuilder();
        var inString = false;
        var escape = false;
        int i = 0;
        while (i < text.Length)
        {
            if (escape)
            {
                result.Append(text[i]);
                escape = false;
                i++;
                continue;
            }

            if (text[i] == '\\')
            {
                escape = true;
                result.Append(text[i]);
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                inString = !inString;
                result.Append(text[i]);
                i++;
                continue;
            }

            if (!inString && i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (!inString && i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i += 2;
                continue;
            }

            result.Append(text[i]);
            i++;
        }

        return result.ToString();
    }

    public static T? GetLatest<T>(List<ConfigEntry> entries, string key) where T : notnull
    {
        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            if (entry is ConfigDocument doc && doc.Info is not null)
            {
                var prop = typeof(ConfigInfo).GetProperty(key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null)
                {
                    var value = prop.GetValue(doc.Info);
                    if (value is T typed && !Equals(typed, default(T)))
                        return typed;
                }
            }
        }
        return default;
    }
}
