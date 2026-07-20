using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record ToolOutput(
    List<ToolOutputContent> Content,
    object? Structured
);

public abstract record ToolOutputContent
{
    public abstract string Type { get; }
}

public record ToolTextContent : ToolOutputContent
{
    public override string Type => "text";
    public string Text { get; init; } = null!;
}

public record ToolFileContent : ToolOutputContent
{
    public override string Type => "file";
    public string Uri { get; init; } = null!;
    public string Mime { get; init; } = null!;
    public string? Name { get; init; }
}

public abstract class Tool
{
    public string Name { get; }
    public string Description { get; }
    public string? Permission { get; }

    protected Tool(string name, string description, string? permission = null)
    {
        Name = name;
        Description = description;
        Permission = permission;
    }

    public abstract Task<ToolOutput> ExecuteAsync(object input, ToolContext context);
    public abstract ToolDefinition ToDefinition(string name);
}

public static class ToolValidator
{
    static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidName(string name) => NameRegex.IsMatch(name);
}
