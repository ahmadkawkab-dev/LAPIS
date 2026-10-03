namespace Wukna.IntegrationTests;

using Wukna.Features.Chat;
using Xunit;

public sealed class ChatConnectionRegistryTests
{
    [Fact]
    public void Subscription_limit_and_disconnect_keep_state_bounded()
    {
        var registry = new ChatConnectionRegistry(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var user = Guid.NewGuid();
        registry.Connect("tab", user);
        var boards = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var board in boards) Assert.True(registry.Subscribe(new ChatSubscription("tab", user, board, Guid.NewGuid())));
        Assert.False(registry.Subscribe(new ChatSubscription("tab", user, Guid.NewGuid(), Guid.NewGuid())));
        Assert.False(registry.Subscribe(new ChatSubscription("tab", Guid.NewGuid(), boards[0], Guid.NewGuid())));
        registry.Disconnect("tab");
        Assert.All(boards, board => Assert.Empty(registry.ForBoard(board)));
        Assert.False(registry.AllowInvocation("tab"));
    }

    [Fact]
    public void Old_instance_removal_cannot_remove_a_new_subscription_on_the_same_connection()
    {
        var registry = new ChatConnectionRegistry(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var user = Guid.NewGuid();
        var board = Guid.NewGuid();
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        registry.Connect("tab", user);
        Assert.True(registry.Subscribe(new ChatSubscription("tab", user, board, old)));
        Assert.True(registry.Subscribe(new ChatSubscription("tab", user, board, current)));
        registry.Remove("tab", board, old);
        Assert.Equal(current, Assert.Single(registry.ForBoard(board)).MembershipInstanceId);
    }
}
