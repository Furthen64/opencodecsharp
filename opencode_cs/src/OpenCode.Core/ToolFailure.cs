namespace OpenCode.Core;

public class ToolFailure : Exception
{
    public ToolFailure(string message) : base(message) { }
}

public class ToolRegistrationError : Exception
{
    public string Name { get; }
    public ToolRegistrationError(string name, string message) : base(message)
    {
        Name = name;
    }
}
