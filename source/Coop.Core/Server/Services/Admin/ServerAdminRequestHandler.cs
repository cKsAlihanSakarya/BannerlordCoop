using Common;
using Common.Commands;
using Coop.Core.Common.Session;
using Coop.Core.Server.Services.Shutdown;
using GameInterface.Services.GameState;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace Coop.Core.Server.Services.Admin;

/// <summary>Answers one admin request with one JSON line.</summary>
public interface IServerAdminRequestHandler
{
    string Handle(string requestLine);
}

/// <inheritdoc cref="IServerAdminRequestHandler"/>
/// <remarks>
/// Called from the pipe thread. Every read of campaign state and every command runs on the game
/// thread through the same <see cref="ICoopCommandRegistry"/> the developer console uses, so an
/// admin action behaves exactly like the operator typing it into the server console.
/// </remarks>
public class ServerAdminRequestHandler : IServerAdminRequestHandler
{
    internal const string UnstuckCommand = "coop.unstuck";
    internal const string ShutdownCommand = "coop.server.shutdown";

    private readonly Lazy<ICoopCommandRegistry> commandRegistry;
    private readonly ICoopCommandArgsFactory argsFactory;
    private readonly IPlayerManager playerManager;
    private readonly IObjectManager objectManager;
    private readonly IServerShutdownCoordinator shutdownCoordinator;
    private readonly Action<Action> runOnGameThread;
    private readonly Action scheduleQuit;
    private int quitScheduled;

    public ServerAdminRequestHandler(
        Lazy<ICoopCommandRegistry> commandRegistry,
        ICoopCommandArgsFactory argsFactory,
        IPlayerManager playerManager,
        IObjectManager objectManager,
        IServerShutdownCoordinator shutdownCoordinator,
        IGameProcessExit processExit)
        : this(
            commandRegistry,
            argsFactory,
            playerManager,
            objectManager,
            shutdownCoordinator,
            action => GameThread.Run(action, blocking: true, label: "ServerAdminRequest"),
            () => ScheduleQuit(processExit))
    {
        if (processExit == null) throw new ArgumentNullException(nameof(processExit));
    }

    internal ServerAdminRequestHandler(
        Lazy<ICoopCommandRegistry> commandRegistry,
        ICoopCommandArgsFactory argsFactory,
        IPlayerManager playerManager,
        IObjectManager objectManager,
        IServerShutdownCoordinator shutdownCoordinator,
        Action<Action> runOnGameThread,
        Action scheduleQuit)
    {
        if (commandRegistry == null) throw new ArgumentNullException(nameof(commandRegistry));
        if (argsFactory == null) throw new ArgumentNullException(nameof(argsFactory));
        if (playerManager == null) throw new ArgumentNullException(nameof(playerManager));
        if (objectManager == null) throw new ArgumentNullException(nameof(objectManager));
        if (shutdownCoordinator == null) throw new ArgumentNullException(nameof(shutdownCoordinator));
        if (runOnGameThread == null) throw new ArgumentNullException(nameof(runOnGameThread));
        if (scheduleQuit == null) throw new ArgumentNullException(nameof(scheduleQuit));

        this.commandRegistry = commandRegistry;
        this.argsFactory = argsFactory;
        this.playerManager = playerManager;
        this.objectManager = objectManager;
        this.shutdownCoordinator = shutdownCoordinator;
        this.runOnGameThread = runOnGameThread;
        this.scheduleQuit = scheduleQuit;
    }

    public string Handle(string requestLine)
    {
        if (!ServerAdminProtocol.TryParse(requestLine, out var request, out var error))
            return ServerAdminProtocol.Message(false, error);

        try
        {
            switch (request.Op)
            {
                case "status":
                    return OnGameThread(BuildStatus);
                case "unstuck":
                    return OnGameThread(() => RunCommand(UnstuckCommand, request.Argument));
                case "shutdown":
                    return OnGameThread(() => RunCommand(ShutdownCommand, request.Argument));
                case "cancel":
                    return OnGameThread(() => RunCommand(ShutdownCommand, "cancel"));
                case "exit":
                    return OnGameThread(Exit);
                default:
                    return ServerAdminProtocol.Message(false, $"Unknown request '{request.Op}'.");
            }
        }
        catch (TimeoutException)
        {
            return ServerAdminProtocol.Message(false, "The server's game thread did not respond in time.");
        }
        catch (OperationCanceledException)
        {
            return ServerAdminProtocol.Message(false, "The server session is ending.");
        }
        catch (Exception exception)
        {
            return ServerAdminProtocol.Message(false, $"Request failed: {exception.Message}");
        }
    }

    private string OnGameThread(Func<string> work)
    {
        string response = null;
        Exception failure = null;

        runOnGameThread(() =>
        {
            try
            {
                response = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        if (failure != null) throw failure;
        return response ?? ServerAdminProtocol.Message(false, "The request produced no response.");
    }

    private string RunCommand(string command, string argument)
    {
        if (Campaign.Current == null)
            return ServerAdminProtocol.Message(false, "No campaign is loaded yet.");

        CoopCommandResult result = commandRegistry.Value.ProcessCommand(
            command,
            argsFactory.FromValues(new[] { argument }));

        return ServerAdminProtocol.Message(result.Succeeded, result.Output);
    }

    /// <summary>
    /// Quits only when nothing unsaved can be lost: after a completed shutdown save, or before a
    /// campaign has loaded (a server stuck while loading).
    /// </summary>
    private string Exit()
    {
        bool campaignLoaded = Campaign.Current != null;
        if (campaignLoaded && shutdownCoordinator.Phase != ServerShutdownPhase.Completed)
        {
            return ServerAdminProtocol.Message(false,
                "Shut the server down first (shutdown <seconds>) so the campaign is saved, then exit.");
        }

        if (Interlocked.Exchange(ref quitScheduled, 1) == 0) scheduleQuit();
        return ServerAdminProtocol.Message(true, "The server is closing.");
    }

    private static void ScheduleQuit(IGameProcessExit processExit)
    {
        // Reply first, quitting on this game-loop pass would close the pipe before the answer is sent.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(500);
            GameThread.RunSafe(processExit.Quit, context: "ServerAdminExit");
        });
    }

    private string BuildStatus()
    {
        bool campaignLoaded = Campaign.Current != null;

        var players = new List<JsonObjectWriter>();
        if (campaignLoaded)
        {
            foreach (Player player in playerManager.Players.OrderBy(p => p.ControllerId, StringComparer.Ordinal))
            {
                players.Add(new JsonObjectWriter()
                    .String("controllerId", player.ControllerId)
                    .String("name", player.PlatformName)
                    .String("hero", GetHeroName(player))
                    .Bool("connected", playerManager.IsConnected(player)));
            }
        }

        var shutdown = new JsonObjectWriter()
            .String("phase", shutdownCoordinator.Phase.ToString())
            .String("detail", shutdownCoordinator.DescribeStatus());

        int processId;
        using (var process = Process.GetCurrentProcess())
        {
            processId = process.Id;
        }

        return new JsonObjectWriter()
            .Bool("ok", true)
            .Number("protocol", ServerAdminProtocol.Version)
            .Number("processId", processId)
            .String("buildVersion", ModInformation.BuildVersion)
            .String("launchSave", ManagedServerConfig.SaveName)
            .Number("port", ManagedServerConfig.Port)
            .Bool("campaignLoaded", campaignLoaded)
            .Object("shutdown", shutdown)
            .Array("players", players)
            .ToString();
    }

    private string GetHeroName(Player player)
    {
        if (string.IsNullOrEmpty(player.HeroId) ||
            !objectManager.TryGetObject(player.HeroId, out Hero hero) ||
            TextObject.IsNullOrEmpty(hero.Name))
            return null;

        return hero.Name.ToString();
    }
}
