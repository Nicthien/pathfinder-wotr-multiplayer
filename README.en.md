# WotR Multiplayer RTwP

[Français](README.md) · **English**

Experimental multiplayer mod for *Pathfinder: Wrath of the Righteous*, with
support for real-time with pause (RTwP), turn-based combat, and synchronization
between multiple players.

[![Support on Ko-fi](https://img.shields.io/badge/Support-Ko--fi-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/nthstudio)

This variant is developed in my spare time. If you find it useful and would
like to support its development, you can buy me a
[Ko-fi](https://ko-fi.com/nthstudio). Thank you!

## Project origin and credits

This repository is a fork of
[`fl01/pathfinder-wotr-multiplayer`](https://github.com/fl01/pathfinder-wotr-multiplayer),
the original multiplayer project created by **fl01**.

The original code, its network architecture, and its multiplayer features
remain the work of fl01 and the upstream contributors. I took over this code to
continue its development, modify it, and experiment in particular with RTwP
combat synchronization, session recovery, and combat saves. Rights to the
original code remain with their respective authors.

This variant is unofficial and is not affiliated with Owlcat Games.

## Main changes in this variant

- synchronized real-time with pause combat in addition to turn-based combat;
- host-authoritative combat state, commands, and random results;
- mode-switch barriers, state verification, repair, and safe fallback to
  turn-based mode;
- stronger synchronization for units, buffs, projectiles, areas of effect,
  summons, dialogues, map events, and other interactions;
- session recovery and reconnection with epoch and sequence checks;
- synchronized saving and restoration during turn-based combat;
- complete French interface and localization;
- isolated local build, diagnostics, staging, and testing tools.

Technical details and validation history are available in [`docs/`](docs/),
especially [`docs/rtwp-local-development.md`](docs/rtwp-local-development.md)
and
[`docs/sauvegarde-combat-tb-developpement-2026-10-17.md`](docs/sauvegarde-combat-tb-developpement-2026-10-17.md).

## Current status

This project is an **experimental version** intended for testing and
development. Do not install it alongside the stable version of the original
mod.

This branch is based on the original project's `0.4.16` series. Newer upstream
changes, including the `0.5.x` series, are not integrated automatically: they
must be adapted deliberately to avoid breaking the already tested RTwP
protocol.

Saving and resuming combat have been tested with one host instance and one
connected client instance, using two isolated installations and profiles on the
same machine. This does not yet replace a full campaign test or validation on
two physical PCs.

The mod deliberately keeps strict safeguards: saving is rejected during a
mode-switch, synchronization, or repair barrier, and a partially applied state
is never treated as a success.

## Compatibility

- *Pathfinder: Wrath of the Righteous* `2.7.0x`;
- Windows;
- Unity Mod Manager `0.32.4a` or earlier;
- identical game, DLC, and gameplay-mod versions for all players;
- keyboard and mouse; controller UI is not supported.

Compatibility may change with future game or upstream project updates.

## Installation

Binary packages are not stored in this repository. When a prebuilt version is
published under **Releases**:

1. download the experimental release archive;
2. close the game;
3. keep a backup of the previous mod and of your campaign;
4. install the package with Unity Mod Manager;
5. ensure that only one multiplayer variant is active;
6. use a copy of your save for initial testing.

The stable slot is `00_WOTRMultiplayer`, while the experimental slot is
`00_WOTRMultiplayerRTwP`. An inactive slot must remain outside the `Mods`
folder: Unity Mod Manager may load a directory even when its name ends in
`.disabled`.

## Connection

The **Multiplayer** menu lets you host or join a game. Direct IP remains the
reference connection method. Playing outside a local network may require a
public address, port forwarding, or a virtual network such as ZeroTier, Radmin
VPN, or Hamachi.

All participants must use the same experimental version.

## Build and tests

The SDK version is pinned in [`global.json`](global.json). From the repository
root:

```powershell
.\tools\rtwp-local.ps1 -Action Status
.\tools\rtwp-local.ps1 -Action Build
dotnet test .\src\WOTRMultiplayer.UnitTests\WOTRMultiplayer.UnitTests.csproj -c Release
```

The **Release** configuration is mandatory for testing and delivery. The Debug
configuration may automatically deploy files into the game.

Local test installations and profiles, logs, saves, build artifacts, and
validation markers remain private and are excluded through `.gitignore`.

## Original project

- upstream repository:
  [`fl01/pathfinder-wotr-multiplayer`](https://github.com/fl01/pathfinder-wotr-multiplayer);
- original video presentation:
  [YouTube](https://www.youtube.com/watch?v=_YZSPrPy9XI);
- original Nexus Mods page:
  [Pathfinder WotR Multiplayer](https://www.nexusmods.com/pathfinderwrathoftherighteous/mods/1116).

For inherited features and detailed explanations from the original mod, also
see [`docs/about.md`](docs/about.md).
