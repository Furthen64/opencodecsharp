using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record ToolCall(
    string Id,
    string Name,
    object Input
);

public record ToolSettlement(
    ToolResultValue Result,
    ToolOutput? Output,
    List<string>? OutputPaths
);

public abstract record ToolResultValue
{
    public abstract string Type { get; }
}

public record ToolSuccessResult : ToolResultValue
{
    public override string Type => "success";
    public object Value { get; init; }
}

public record ToolErrorResult : ToolResultValue
{
    public override string Type => "error";
    public string Value { get; init; }
}

public interface IToolRegistry
{
    Task RegisterAsync(string name, Tool tool);
    Task<ToolMaterialization> MaterializeAsync(List<PermissionRule>? permissions = null);
    Tool? GetTool(string name);
}

public record ToolMaterialization(
    List<ToolDefinition> Definitions,
    System.Func<ToolCall, ToolContext, Task<ToolSettlement>> Settle
);

public class ToolRegistry : IToolRegistry
{
    readonly Dictionary<string, Tool> tools = new();

    public Task RegisterAsync(string name, Tool tool)
    {
        if (!ToolValidator.IsValidName(name))
            throw new ToolRegistrationError(name, $"Invalid tool name: {name}");
        tools[name] = tool;
        return Task.CompletedTask;
    }

    public Task<ToolMaterialization> MaterializeAsync(List<PermissionRule>? permissions = null)
    {
        var definitions = new List<ToolDefinition>();
        foreach (var (name, tool) in tools)
        {
            if (permissions != null && IsWhollyDisabled(tool.Permission ?? name, permissions))
                continue;
            definitions.Add(tool.ToDefinition(name));
        }

        async Task<ToolSettlement> Settle(ToolCall call, ToolContext context)
        {
            if (!tools.TryGetValue(call.Name, out var tool))
                return new ToolSettlement(new ToolErrorResult { Value = $"Unknown tool: {call.Name}" }, null, null);

            try
            {
                var output = await tool.ExecuteAsync(call.Input, context);
                return new ToolSettlement(new ToolSuccessResult { Value = output.Structured }, output, null);
            }
            catch (ToolFailure ex)
            {
                return new ToolSettlement(new ToolErrorResult { Value = ex.Message }, null, null);
            }
        }

        return Task.FromResult(new ToolMaterialization(definitions, Settle));
    }

    public Tool? GetTool(string name)
    {
        tools.TryGetValue(name, out var tool);
        return tool;
    }

    static bool IsWhollyDisabled(string action, List<Schema.PermissionRule> rules)
    {
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i];
            if (MatchesWildcard(action, rule.Action))
            {
                return rule.Resource == "*" && rule.Effect == PermissionEffect.Deny;
            }
        }
        return false;
    }

    static bool MatchesWildcard(string value, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.Contains('*'))
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(value, regex);
        }
        return value == pattern;
    }
}
