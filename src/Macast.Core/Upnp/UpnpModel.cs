namespace Macast.Core.Upnp;

public sealed class StateVariable
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required bool SendEvents { get; init; }
    public required string Service { get; init; }
    public string? DefaultValue { get; init; }
    public List<string> AllowedValues { get; } = new();
    public long? Minimum { get; set; }
    public long? Maximum { get; set; }
    public string Value { get; set; } = string.Empty;
}

public sealed class ActionArgument
{
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public required string RelatedStateVariable { get; init; }
}

public sealed class UpnpAction
{
    public required string Name { get; init; }
    public List<ActionArgument> Inputs { get; } = new();
    public List<ActionArgument> Outputs { get; } = new();
}

public sealed class ServiceDefinition
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public Dictionary<string, UpnpAction> Actions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StateVariable> StateVariables { get; } = new(StringComparer.Ordinal);
}
