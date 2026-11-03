# Lifeline

A connection-blip detection and recovery plugin for **[VibeSkua 2.0+](https://github.com/NinjaXz/VibeSkua)**. 

Lifeline waits for your network to actually come back, checks if the client is still healthy, and only restarts when it isn't — bringing you back with the same account and the same script. A restarted client will also automatically signal the host to switch to Grid View after login.

**Author:** Excite  
**Version:** 1.0.0  
**Requires:** VibeSkua 2.0.0 or newer (Avalonia client)

## The Problem It Solves

When a connection blip occurs, VibeSkua's built-in auto-relogin may try to recover the client while the network is still dead. This rapid retry loop frequently re-initializes the Flash OCX, which triggers the infamous **"Clean Flash missing/installed incorrectly"** popup. Once that modal appears, the client is effectively dead and requires a manual restart.

Lifeline takes a **passive, safe approach**:
1. It detects the blip early and suppresses VibeSkua's auto-relogin to prevent the two systems from fighting over the Flash control.
2. It waits for the network to be stable for 10 seconds before doing anything.
3. It performs a deep health check on the client (checking player status, map state, and script activity).
4. If the client survived, it simply resumes the script in-process.
5. If the client is truly dead, it hands off to an external helper to kill and relaunch the client safely.

## What It Does

* **Passive Blip Detection:** Pings a target host on an interval. Detects when the connection drops and stops VibeSkua from immediately trying to relogin.
* **Network Wait & Grace Period:** After the network returns, waits for a stable connection (10s) and a grace period (10s) before judging the client's health.
* **Deep Health Check:** Verifies that the player is logged in, actively playing, the map is loaded, the cell is valid, and the username is populated. If a script was running, it also confirms that stats are still changing.
* **In-Process Script Recovery:** If the client is healthy but the script stopped, Lifeline safely calls `StartScript()` to resume it without touching the Flash control.
* **External Restart Helper:** If the client is completely dead, Lifeline writes a request for `LifelineHelper.exe`. The helper waits for the old client to exit, waits for the network to be reachable, and relaunches the client with your original arguments, preserving the script and account.
* **Flash Modal Watcher:** Polls every 200ms for the "Reinstall Flash" popup. If it detects the modal (even if it's owned by `FlashUtil.exe` or a separate dialog window), it immediately triggers the external restart helper.
* **Grid View Integration:** When the helper relaunches a client, it injects a `--lifeline-restart` flag. Once logged in, the client notifies the host, and the host automatically switches to Grid View.
* **Rate Limiting:** Protects against infinite restart loops by limiting restarts to a maximum of 5 per hour per account.

## Versioning

Lifeline uses a central version system. The version for both the plugin and the helper is controlled by the `<Version>` tag inside **`Directory.Build.props`**. To release a new version, simply change that single number (e.g., `1.0.1`), and both the DLL and the EXE will compile with the new version stamp. You can view the installed version of both in the plugin's **About** tab.

## Building from Source

You will need **Git** and the **.NET 10 SDK**.

```bash
# Windows (auto-installs to your VibeSkua plugin folder)
./build.ps1

# Linux / macOS (builds only, you copy the files manually)
chmod +x build.sh
./build.sh
```

### Windows: Build and Auto-Install

The `build.ps1` script automatically builds the plugin and helper, and then copies all required files directly into `%APPDATA%\VibeSkua\plugins\Lifeline\`. **You do not need to copy anything manually.**

### Linux / macOS: Build Only

The `build.sh` script builds the files but **does not auto-install** them.

| Output | Location | What It Is |
|---|---|---|
| **`Lifeline.dll`** | `src/bin/Release/net10.0-windows/` | The plugin itself. Loaded by VibeSkua at startup. |
| **`LifelineHelper.exe`** | `helper/bin/Release/net10.0-windows/` | The external helper. A windowless process that relaunches dead clients. |

### What `LifelineHelper.exe` actually does

The helper is the reason Lifeline can recover a truly dead client. It runs in the background and:

1. **Watches for requests.** It polls `%APPDATA%\VibeSkua\lifeline\requests\` for JSON files written by the plugin.
2. **Waits for the old client to exit.** After picking up a request, it waits up to 30s for the dying client to close on its own, then force-kills it if it's hung.
3. **Waits for the network.** Before relaunching, it pings the configured target until it gets a response (up to 10 minutes). This prevents relaunching a client into a dead network — the #1 cause of the Flash modal reappearing.
4. **Relaunches with the original arguments.** It reads the JSON payload written by the plugin (which includes the account, password, server, and script), reconstructs the command line, and starts a fresh client.
5. **Adds `--lifeline-restart`.** This flag tells the new client it was a Lifeline restart, so it can notify the host to enter Grid View after login.

One helper process serves all clients on the machine. The plugin starts it automatically on first load and re-spawns it within a minute if it disappears.

### Manual Installation (if not using `build.ps1`)

If you built manually or are on Linux/macOS, copy the files as follows. **The `Lifeline` folder must contain ALL of the helper's files, not just the `.exe`.** The helper is a framework-dependent .NET application and will crash on launch if you only copy the `.exe`.

```
%APPDATA%\VibeSkua\plugins\
├── Lifeline.dll
└── Lifeline\
    ├── LifelineHelper.exe
    ├── LifelineHelper.dll
    ├── LifelineHelper.deps.json
    └── LifelineHelper.runtimeconfig.json
```

> **Optional — single-file helper:** if you'd rather have only one file in that folder, publish the helper as self-contained + single-file:
> 
> ```
> cd helper
> dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
> ```
> 
> Then the only file you need in the `Lifeline` folder is the `.exe` (~60 MB, no runtime required).

## Configuration

Settings are available in-game under **Plugins → View Plugins → Lifeline → Options**.

| Option | Default | Description |
|---|---|---|
| **Enable Lifeline** | On | Master switch. Turns all detection and recovery off. |
| **Restart on Health Check Failure** | On | Allows the helper to restart the client if it fails the health check. |
| **Restart on 'Reinstall Flash' Popup** | On | Triggers the helper immediately if the Flash error modal is detected. |
| **Restart script if stopped** | On | Safely starts the script in-process if the client is alive but the script stopped. |
| **Suppress VibeSkua auto-relogin** | On | Prevents VibeSkua from triggering its own relogin during a blip. |
| **Enter Grid View after restart** | On | Signals the host to switch to Grid View once the client logs in. |
| **Respect --exit-on-script-stop** | On | Prevents restarting clients that were intentionally designed to close when a script finishes. |
| **Ping target** | `8.8.8.8` | Host used to test network connectivity. |
| **Failure threshold** | 2 | Consecutive failed pings required to consider the network "bliped". |
| **Ping timeout (ms)** | 2000 | Timeout per ping attempt. |
| **Restore stability window (s)** | 10 | Seconds the network must stay online after a blip. |
| **Grace after restore (s)** | 10 | Extra wait time before checking the client's health. |
| **Activity window (s)** | 30 | How long to wait for stats to change to confirm the script is active. |
| **Max restarts per hour** | 5 | Loop protection. Prevents constant restart loops. |

### Testing from the Settings window

The **Test & Diagnostics** tab has four one-click tests:

- **Test Ping** — pings the configured target and reports latency.
- **Test Grid** — toggles VibeSkua's grid view via the host (grid ↔ tab).
- **Check Helper Status** — reports whether `LifelineHelper.exe` is running.
- **Test Restart** — writes a restart request and exits immediately, so you can verify the helper brings the client back. Use this to confirm your install is working end-to-end.

## Disclaimers

- **Educational & Personal Use Only:** This project is provided "as-is" under the MIT License.
- **No Affiliation:** Lifeline is an independent, community-created plugin. It is not affiliated with Artix Entertainment or NinjaXz/VibeSkua.
- **Use At Your Own Risk:** Use of automation software may violate the Terms of Service of the associated game. The author assumes no responsibility for any account bans, suspensions, or other consequences taken by game developers. By using this tool, you acknowledge that you do so entirely at your own risk.
- **Flash Dependency:** Lifeline attempts to recover the client by managing the Flash Player OCX, but it cannot fix a genuinely broken Flash installation. If you are repeatedly hitting the "Clean Flash" modal, you must manually reinstall the Flash runtime.

