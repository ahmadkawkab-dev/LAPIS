namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wukna.Features.Realtime;

internal sealed class BoardTransportSpy : IHubContext<BoardHub>, IGroupManager
{
    public record Delivery(string Kind, string[] Targets, string EventName, object?[] Args);
    public ConcurrentQueue<Delivery> Deliveries { get; } = new();
    public Func<string, Task> Removal { get; set; } = _ => Task.CompletedTask;
    public Func<string, Task> Publication { get; set; } = _ => Task.CompletedTask;
    public int RemovalAttempts;
    public IHubClients Clients { get; }
    public IGroupManager Groups => this;
    public BoardTransportSpy()
    {
        Clients = DispatchProxy.Create<IHubClients, BoardClientRouter>();
        ((BoardClientRouter)Clients).Transport = this;
    }
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
    {
        Interlocked.Increment(ref RemovalAttempts);
        return Removal(connectionId);
    }
    public BoardRealtimePublisher Publisher(BoardConnectionRegistry registry) => new(this, registry,
        new NoteGeometryPreviewRegistry(), new NoteEditingRegistry(), new BoardCursorRegistry(), NullLogger<BoardRealtimePublisher>.Instance);
    public BoardGroupCleanupService Worker(BoardConnectionRegistry registry, TimeProvider clock, IServiceScopeFactory scopes) =>
        new(registry, this, scopes, clock, NullLogger<BoardGroupCleanupService>.Instance);
}

public class BoardClientRouter : DispatchProxy
{
    internal BoardTransportSpy Transport { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var kind = targetMethod!.Name;
        if (kind is not ("Clients" or "Users" or "Client" or "User"))
            throw new InvalidOperationException("Sensitive publications must never use native groups or all-client addressing.");
        var targets = args![0] is string id ? [id] : ((IEnumerable<string>)args[0]!).ToArray();
        return new BoardClientProxy(Transport, kind, targets);
    }
    private sealed class BoardClientProxy(BoardTransportSpy transport, string kind, string[] targets) : ISingleClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            transport.Deliveries.Enqueue(new(kind, targets, method, args));
            return transport.Publication(method);
        }
        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
