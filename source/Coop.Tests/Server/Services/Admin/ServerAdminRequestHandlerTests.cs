using Common.Commands;
using Coop.Core.Server.Services.Admin;
using Coop.Core.Server.Services.Shutdown;
using Coop.Tests.Server.Services.MobileParties;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using System;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace Coop.Tests.Server.Services.Admin;

/// <summary>Covers the handler with no campaign loaded; Campaign.Current is cleared for each test.</summary>
[Collection(nameof(CampaignCurrentCollection))]
public class ServerAdminRequestHandlerTests : IDisposable
{
    private readonly Campaign previousCampaign = Campaign.Current;
    private readonly Mock<ICoopCommandRegistry> registry = new Mock<ICoopCommandRegistry>(MockBehavior.Strict);
    private readonly Mock<ICoopCommandArgsFactory> argsFactory = new Mock<ICoopCommandArgsFactory>(MockBehavior.Strict);
    private readonly Mock<IPlayerManager> playerManager = new Mock<IPlayerManager>();
    private readonly Mock<IObjectManager> objectManager = new Mock<IObjectManager>();
    private readonly Mock<IServerShutdownCoordinator> coordinator = new Mock<IServerShutdownCoordinator>();
    private int gameThreadRuns;
    private int quits;

    public ServerAdminRequestHandlerTests()
    {
        Campaign.Current = null;
        playerManager.SetupGet(m => m.Players).Returns(Array.Empty<Player>());
        coordinator.SetupGet(m => m.Phase).Returns(ServerShutdownPhase.Idle);
        coordinator.Setup(m => m.DescribeStatus()).Returns("phase=Idle joins=open defaultSave=none");
    }

    public void Dispose()
    {
        Campaign.Current = previousCampaign;
    }

    private ServerAdminRequestHandler CreateHandler(Action<Action> runOnGameThread = null) =>
        new ServerAdminRequestHandler(
            new Lazy<ICoopCommandRegistry>(() => registry.Object),
            argsFactory.Object,
            playerManager.Object,
            objectManager.Object,
            coordinator.Object,
            runOnGameThread ?? (action =>
            {
                gameThreadRuns++;
                action();
            }),
            () => quits++);

    [Fact]
    public void Handle_MalformedRequest_RepliesWithoutTouchingTheGame()
    {
        string reply = CreateHandler().Handle("reboot");

        Assert.Equal("{\"ok\":false,\"message\":\"Unknown request 'reboot'.\"}", reply);
        Assert.Equal(0, gameThreadRuns);
    }

    [Fact]
    public void Handle_Status_ReportsTheShutdownPhaseOnTheGameThread()
    {
        string reply = CreateHandler().Handle("status");

        Assert.StartsWith("{\"ok\":true,\"protocol\":1,", reply);
        Assert.Contains("\"campaignLoaded\":false", reply);
        Assert.Contains("\"shutdown\":{\"phase\":\"Idle\",\"detail\":\"phase=Idle joins=open defaultSave=none\"}", reply);
        Assert.EndsWith("\"players\":[]}", reply);
        Assert.Equal(1, gameThreadRuns);
    }

    [Theory]
    [InlineData("unstuck 76561198000000001")]
    [InlineData("shutdown 60")]
    [InlineData("cancel")]
    public void Handle_CommandsBeforeTheCampaignLoads_AreRefused(string request)
    {
        string reply = CreateHandler().Handle(request);

        Assert.Equal("{\"ok\":false,\"message\":\"No campaign is loaded yet.\"}", reply);
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public void Handle_ExitBeforeTheCampaignLoads_QuitsOnce()
    {
        var handler = CreateHandler();

        Assert.Equal("{\"ok\":true,\"message\":\"The server is closing.\"}", handler.Handle("exit"));
        Assert.Equal("{\"ok\":true,\"message\":\"The server is closing.\"}", handler.Handle("exit"));
        Assert.Equal(1, quits);
    }

    [Fact]
    public void Handle_GameThreadTimeout_SaysTheServerIsNotResponding()
    {
        var handler = CreateHandler(_ => throw new TimeoutException());

        Assert.Equal(
            "{\"ok\":false,\"message\":\"The server's game thread did not respond in time.\"}",
            handler.Handle("status"));
        Assert.Equal(0, quits);
    }
}
