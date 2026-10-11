# Coop Server Panel

A small web panel for a dedicated co-op server. Players open it in a browser to start, stop or restart the server and to unstick a player, without the host being at the computer.

## How it works

The server side lives in `source/Coop.Core/Server/Services/Admin`. While a server session runs, `ServerAdminPipeHost` serves a local named pipe, `BannerlordCoop.ServerAdmin.<port>` (4200 by default). Each connection sends one request line and gets one JSON line back:

| Request | Does |
| --- | --- |
| `status` | process id, launch save, whether the campaign is loaded, shutdown phase, players |
| `unstuck <controller id>` | runs `coop.unstuck <controller id>` |
| `shutdown <seconds>` | runs `coop.server.shutdown <seconds>` |
| `cancel` | runs `coop.server.shutdown cancel` |
| `exit` | quits the process, only after the shutdown has saved or while no campaign is loaded |

Commands go through `ICoopCommandRegistry` on the game thread, the same path as the developer console. The pipe is local to the machine and its default ACL only lets the account running the server write to it.

The panel (this folder) runs on the same computer as the server. It launches `Bannerlord.exe` with the dedicated-server arguments, polls `status`, and serves one page. Stop and restart always use `coop.server.shutdown`, so players get the usual warnings and the campaign is saved before the process closes. If the server process exits on its own after it was running, the panel starts it again (at most 3 times in 15 minutes).

## Build

Needs the .NET 10 SDK. From the repo root:

```
dotnet publish tools\CoopServerPanel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o tools\CoopServerPanel\publish
```

This gives `publish\CoopServerPanel.exe`, which runs without .NET installed.

The server only answers the panel when its Coop build includes the admin pipe. The change adds no network messages, so players don't need anything new.

## Configure

The first run creates `panelsettings.json` next to the exe, prints a random panel password and exits. Edit the file, then start the panel again:

| Setting | Meaning |
| --- | --- |
| `PanelPassword` | password friends type to sign in |
| `ListenUrl` | `http://0.0.0.0:8090` accepts other computers, `http://127.0.0.1:8090` only this one |
| `BannerlordExe` | full path to `bin\Win64_Shipping_Client\Bannerlord.exe` |
| `Modules` | module ids in load order, same as the players use |
| `SaveName` | save to host, as shown in the save list without `.sav` |
| `ServerPassword` | join password, empty for none |
| `Visibility` | `public`, `friends_only` or `none` |
| `GamePort` | server port, also names the pipe |
| `WarningSeconds` | default warning before a stop or restart |
| `RestartOnCrash` | start the server again after an unexpected exit |
| `ExtraArguments` | appended to the launch arguments |

Steam has to be running on the host, the same as for hosting from the game.

## Reaching the panel from other computers

The panel speaks plain HTTP. The simplest safe setup is a private network such as Tailscale on the host and on each player's PC, then open `http://<host tailscale ip>:8090`. Forwarding TCP 8090 on the router also works but sends the panel password unencrypted.

## Manual test

1. Start the panel and sign in.
2. Press Start server. The page shows the server starting, then running with the save name once the campaign is loaded.
3. Join with a client, open the panel and press Unstuck next to that player. The activity list shows the result and `Coop_server.log` has an `Operator unstuck` line.
4. Press Restart with a 30 s warning. The client sees the restart notice, gets disconnected, the save is written and the server comes back with the same save.
5. Press Restart again and then Cancel shutdown during the countdown. The client sees the cancel notice and stays connected.
