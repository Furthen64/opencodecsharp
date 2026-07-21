using System.Text.Json;
using OpenCode.Schema;

namespace OpenCode.Data;

public record SessionMessageInfo(
    string Id,
    string SessionId,
    string Type,
    int Seq,
    long TimeCreated,
    long TimeUpdated,
    string Data
)
{
    public T? Deserialize<T>() where T : SessionMessageBase
    {
        return JsonSerializer.Deserialize<T>(Data);
    }

    public SessionMessageBase? Deserialize()
    {
        return JsonSerializer.Deserialize<SessionMessageBase>(Data);
    }
}
