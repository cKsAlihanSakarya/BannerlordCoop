# Coop Server Panel

A small web panel for the official dedicated server (`DedicatedServer\BannerlordCoopServer.exe`). Players open it in a browser to start, stop or restart the server and to unstick a player, without the host being at the computer.

Nothing in the mod or the server changes. The panel does what the host would do by hand:

- It starts `BannerlordCoopServer.exe` in its own window, the same as a double-click.
- It types commands into that window: `coop.debug.players.list`, `coop.unstuck <controller id>`, `coop.server.shutdown <seconds>`, `stop`, and the server's own `kick <id>`, `say <text>` and `save`.
- It reads the server's answers, its phase and its player list from `logs\coop-server-*.log` in the server's data folder. That folder is `Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer` by default. The server writes `@DS@{"ev":"state",...}` and `@DS@{"ev":"players",...}` lines there.

A stop with a warning runs `coop.server.shutdown <seconds>` first, which warns players, closes joins, disconnects everyone and saves. It then runs `stop`, which saves again and exits. When nobody is connected, or the warning is 0, it runs `stop` right away. If the server exits on its own after it was serving, the panel starts it again, at most 3 times in 15 minutes. An exit code of 0 counts as a stop from the server window and isn't restarted.

## Build

Needs the .NET 10 SDK. From the repo root:

```
dotnet publish tools\CoopServerPanel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o tools\CoopServerPanel\publish
```

This gives `publish\CoopServerPanel.exe`, which runs without .NET installed.

## Configure

The first run creates `panelsettings.json` next to the exe, prints a random panel password and exits. Edit the file, then start the panel again:

| Setting | Meaning |
| --- | --- |
| `PanelPassword` | password friends type to sign in |
| `ListenUrl` | `http://0.0.0.0:8090` accepts other computers, `http://127.0.0.1:8090` only this one |
| `ServerExe` | full path to `DedicatedServer\BannerlordCoopServer.exe` in the Coop module |
| `DataDir` | the server's data folder, empty for its default. Passed as `--data-dir` |
| `WarningSeconds` | default warning before a stop or restart |
| `RestartOnCrash` | start the server again after an unexpected exit |
| `ExtraArguments` | appended to the server's launch arguments |

What the server hosts (save, join password, port, Steam) stays in the server's own `server-config.json`. Keep `"logFile": true` there, because the panel reads the server through its log.

The panel and the server must run with the same rights. If the server is started as administrator, start the panel as administrator too. Otherwise Windows doesn't let the panel type into the server window.

## Reaching the panel from other computers

The simplest way is a Cloudflare quick tunnel, which gives an https address and needs nothing on the players' side. Keep `ListenUrl` at `http://127.0.0.1:8090` so the panel itself stays local, then on the host:

```
winget install --id Cloudflare.cloudflared
cloudflared tunnel --url http://localhost:8090
```

It prints a `https://<words>.trycloudflare.com` address to share. The address changes each time `cloudflared` restarts; a named tunnel on a Cloudflare account keeps a fixed one. Use a long panel password, because the address is reachable from anywhere.

## Manual test

1. Start the panel and sign in.
2. Press Start server. A server window opens. The page shows the server starting, then running with the save name once the log shows `SERVING`.
3. Join with a client. The player shows up with "Playing". Press Unstuck next to the player. The server window shows `> coop.unstuck <id>` and the activity list shows the result.
4. Press Restart with a 30 s warning. The client sees the restart notice and gets disconnected. The server saves, closes and comes back with the same save.
5. Press Restart again and then Cancel shutdown during the countdown. The client sees the cancel notice and stays connected.
