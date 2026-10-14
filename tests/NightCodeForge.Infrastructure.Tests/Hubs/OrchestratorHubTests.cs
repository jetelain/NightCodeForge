using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NightCodeForge.Infrastructure.Hubs;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Hubs;

public class OrchestratorHubTests
{
    [Fact]
    public void GetTaskGroupName_ReturnsFormattedTaskGroup()
    {
        var groupName = OrchestratorHub.GetTaskGroupName(42);
        Assert.Equal("task-42", groupName);
    }

    [Fact]
    public async Task JoinTaskGroup_AddsCurrentConnectionToTaskGroup()
    {
        var hub = new OrchestratorHub();
        var fakeContext = new FakeHubCallerContext("test-connection-123");
        var fakeGroups = new FakeGroupManager();

        hub.Context = fakeContext;
        hub.Groups = fakeGroups;

        await hub.JoinTaskGroup(101);

        Assert.Single(fakeGroups.Added);
        Assert.Equal("test-connection-123", fakeGroups.Added[0].ConnectionId);
        Assert.Equal("task-101", fakeGroups.Added[0].GroupName);
    }

    [Fact]
    public async Task LeaveTaskGroup_RemovesCurrentConnectionFromTaskGroup()
    {
        var hub = new OrchestratorHub();
        var fakeContext = new FakeHubCallerContext("test-connection-456");
        var fakeGroups = new FakeGroupManager();

        hub.Context = fakeContext;
        hub.Groups = fakeGroups;

        await hub.LeaveTaskGroup(202);

        Assert.Single(fakeGroups.Removed);
        Assert.Equal("test-connection-456", fakeGroups.Removed[0].ConnectionId);
        Assert.Equal("task-202", fakeGroups.Removed[0].GroupName);
    }

    private class FakeHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;

        public FakeHubCallerContext(string connectionId)
        {
            _connectionId = connectionId;
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private class FakeGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> Added { get; } = new();
        public List<(string ConnectionId, string GroupName)> Removed { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}
