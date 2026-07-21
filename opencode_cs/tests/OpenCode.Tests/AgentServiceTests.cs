using OpenCode.Core;

namespace OpenCode.Tests;

public sealed class AgentServiceTests
{
    [Fact]
    public async Task TransformsAreReplayedAndRemovedWithTheirRegistration()
    {
        var service = new AgentService();
        var transformable = await service.TransformAsync();
        var registration = await transformable.TransformAsync(draft =>
        {
            draft.Update("review", agent => agent with { Description = "Reviews code" });
            draft.SetDefault("review");
            return Task.CompletedTask;
        });

        Assert.Equal("Reviews code", (await service.GetAsync("review"))?.Description);
        Assert.Equal("review", (await service.DefaultAsync())?.Id);

        await registration.Dispose();

        Assert.Null(await service.GetAsync("review"));
        Assert.Null(await service.DefaultAsync());
    }
}
