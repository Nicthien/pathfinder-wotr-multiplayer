# Local RTwP variant

This tree is an experimental, local-only derivative of multiplayer 0.4.16. It must not replace the stable installation until the real-time protocol and in-game synchronization are validated on two machines.

## Safety invariants

- `00_WOTRMultiplayer` is the stable slot.
- `00_WOTRMultiplayerRTwP` is the experimental slot.
- Only one slot may be enabled at a time.
- Never copy a Debug build into the stable slot.
- Never save while a mode-switch or repair barrier is active.
- The host is authoritative for combat state and random outcomes.
- A failed repair falls back to turn-based combat.

## Local commands

```powershell
.\tools\rtwp-local.ps1 -Action Build
.\tools\rtwp-local.ps1 -Action Status
.\tools\rtwp-local.ps1 -Action Install
.\tools\rtwp-local.ps1 -Action EnableExperimental
.\tools\rtwp-local.ps1 -Action EnableStable
```

`Install` creates a timestamped backup of an existing experimental installation. The script refuses to change installed folders while `Wrath.exe` is running. Installation and activation are deliberately locked until a local `local-artifacts/RTWP_RUNTIME_VALIDATED.json` records protocol version 1 and a successful two-instance combat test. `Build` does not touch either installed mod.

## Current milestone

The first milestone provides the deterministic state machine for mode-switch barriers, epoch/sequence rejection, three pause policies, a canonical snapshot fingerprint, repair attempts and automatic turn-based fallback. A matching game/mod/DLC/protocol check now prevents starting an incompatible lobby. These components are not yet connected to the in-game real-time simulation. The stable turn-based runtime remains the only supported in-game mode until transport messages, snapshots, host-side command execution and the integrated menu are connected and verified. Do not create the validation marker merely to bypass this lock.
