using System.Text.Json.Serialization;

namespace OpenCode.Protocol;

[JsonPolymorphic]
[JsonDerivedType(typeof(EventPayload), typeDiscriminator: "event")]
public abstract record EventPayload(
    string Id,
    string Type,
    object? Data,
    EventDurable? Durable,
    LocationRef? Location,
    Dictionary<string, object>? Metadata
);

public record EventDurable(
    string AggregateID,
    int Seq,
    int Version
);
