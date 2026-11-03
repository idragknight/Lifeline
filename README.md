# Lifeline

A connection-blip detection and recovery plugin for **[VibeSkua 2.0+](https://github.com/NinjaXz/VibeSkua)**. 

Lifeline waits for your network to actually come back, checks if the client is still healthy, and only restarts when it isn't — bringing you back with the same account and the same script. A restarted client will also automatically signal the host to switch to Grid View after login.

**Author:** Excite  
**Version:** 1.0.0  
**Requires:** VibeSkua 2.0.0 or newer (Avalonia client)

---

## What is Lifeline?

When a connection blip occurs, VibeSkua's built-in auto-relogin may try to recover the client while the network is still dead. This rapid retry loop frequently re-initializes the Flash OCX, which triggers the infamous **"Clean Flash missing/installed incorrectly"** popup. Once that modal appears, the client is effectively dead and requires a manual restart.

Lifeline takes a **passive, safe approach** to this problem:
1. It detects the blip early and suppresses VibeSkua's auto-relogin to prevent the two systems from fighting over the Flash control.
2. It waits for the network to be stable for 10 seconds before doing anything.
3. It performs a deep health check on the client (checking player status, map state, and script activity).
4. If the client survived, it simply resumes the script in-process.
5. If the client is truly dead, it hands off to an external helper to kill and relaunch the client safely.

### Features
* **Passive Blip Detection:** Pings a target host on an interval. Detects when the connection drops and stops VibeSkua from immediately trying to relogin.
* **Network Wait & Grace Period:** After the network returns, waits for a stable connection (10s) and a grace period (10s) before judging the client's health.
* **Deep Health Check:** Verifies that the player is logged in, actively playing, the map is loaded, the cell is valid, and the username is populated.
* **In-Process Script Recovery:** If the client is healthy but the script stopped, Lifeline safely calls `StartScript()` to resume it without touching the Flash control.
* **External Restart Helper:** If the client is completely dead, Lifeline writes a request for `LifelineHelper.exe`. The helper waits for the old client to exit, waits for the network to be reachable, and relaunches the client with your original arguments.
* **Gated Flash Modal Watcher:** Only activates during a blip window. It catches the "Reinstall Flash" popup and immediately triggers the external restart helper.
* **Rate Limiting:** Protects against infinite restart loops by limiting restarts to a maximum of 5 per hour per account.

---

## Installation

> **⚠️ Windows Only:** This plugin is built for Windows. It relies on Windows Forms for the background helper tray icon and Win32 API calls for the Flash modal watcher.

### Requirements
* Windows 10 or 11
* VibeSkua 2.0.0+
* .NET 10 SDK (if building from source)

### Method 1: Build Script (Recommended)
If you have the .NET SDK installed, you can build and auto-install the plugin by running this command in the root folder:
```powershell
./build.ps1
```

This script will compile the plugin and helper, then automatically copy all required files into `%APPDATA%\VibeSkua\plugins\`.

### Method 2: Manual Installation

If you downloaded pre-compiled files or built them manually, place them in your VibeSkua plugins folder exactly like this:

```
%APPDATA%\VibeSkua\plugins\
├── Lifeline.dll
└── Lifeline\
    ├── LifelineHelper.exe
    ├── LifelineHelper.dll
    ├── LifelineHelper.deps.json
    └── LifelineHelper.runtimeconfig.json
```

**Important:** The `Lifeline` subfolder must contain **ALL FOUR** helper files. The helper is a framework-dependent .NET application and will crash on launch if you only copy the `.exe`.

---

## Configuration

Settings are available in-game under **Plugins → View Plugins → Lifeline**. You can also open the settings by right-clicking the Lifeline Helper icon in your Windows system tray.

- **Enable Lifeline:** Master switch.
- **Restart on Health Check Failure:** Allows the helper to restart the client if it fails the health check.
- **Restart on 'Reinstall Flash' Popup:** Triggers the helper immediately if the Flash error modal is detected.
- **Restart script if stopped:** Safely starts the script in-process if the client is alive but the script stopped.
- **Suppress VibeSkua auto-relogin:** Prevents VibeSkua from triggering its own relogin during a blip.
- **Enter Grid View after restart:** Signals the host to switch to Grid View once the client logs in.
- **Respect --exit-on-script-stop:** Prevents restarting clients that were intentionally designed to close when a script finishes.
- **Ping target:** Host used to test network connectivity (Default: `8.8.8.8`).
- **Failure threshold:** Consecutive failed pings required to consider the network "bliped" (Default: 2).
- **Ping timeout (ms):** Timeout per ping attempt.
- **Restore stability window (s):** Seconds the network must stay online after a blip.
- **Grace after restore (s):** Extra wait time before checking the client's health.
- **Activity window (s):** How long to wait for stats to change to confirm the script is active.
- **Max restarts per hour:** Loop protection. Prevents constant restart loops.

---

## Disclaimers

- **Tested Environment:** This plugin is built for and tested only on **Windows** using the **normal (default) VibeSkua installation path**. It has not been tested with custom or portable VibeSkua installations.
- **Educational & Personal Use Only:** This project is provided "as-is" under the MIT License.
- **No Affiliation:** Lifeline is an independent, community-created plugin. It is not affiliated with Artix Entertainment or NinjaXz/VibeSkua.
- **Use At Your Own Risk:** Use of automation software may violate the Terms of Service of the associated game. The author assumes no responsibility for any account bans, suspensions, or other consequences taken by game developers. By using this tool, you acknowledge that you do so entirely at your own risk.
- **Flash Dependency:** Lifeline attempts to recover the client by managing the Flash Player OCX, but it cannot fix a genuinely broken Flash installation. If you are repeatedly hitting the "Clean Flash" modal, you must manually reinstall the Flash runtime.

