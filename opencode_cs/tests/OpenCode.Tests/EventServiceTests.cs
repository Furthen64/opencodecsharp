using OpenCode.Core;

namespace OpenCode.Tests;

public sealed class EventServiceTests
{
    [Fact]
    public async Task DurableEventsReceiveMonotonicSequenceNumbers()
    {
        using var service = new EventService();
        var definition = new EventDefinition("test.event", true, "SessionId", 1);

        var first = await service.PublishAsync(definition, new { SessionId = "ses_1", Value = 1 });
        var second = await service.PublishAsync(definition, new { SessionId = "ses_1", Value = 2 });

        Assert.Equal(1, first.Durable?.Seq);
        Assert.Equal(2, second.Durable?.Seq);
        Assert.Equal([1, 2], (await service.ReplayAsync("ses_1")).Select(item => item.Seq));
    }

    [Fact]
    public async Task ConcurrentDurableEventsReceiveUniqueSequenceNumbers()
    {
        using var service = new EventService();
        var definition = new EventDefinition("test.event", true, "SessionId", 1);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(value => service.PublishAsync(definition, new { SessionId = "ses_1", Value = value })));

        var events = await service.ReplayAsync("ses_1", limit: 100);
        Assert.Equal(Enumerable.Range(1, 100), events.Select(item => item.Seq));
    }
}
