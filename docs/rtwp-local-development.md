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

`Install` creates a timestamped backup of an existing experimental installation. The script refuses to change installed folders while `Wrath.exe` is running. Installation and activation are deliberately locked until a local `local-artifacts/RTWP_RUNTIME_VALIDATED.json` records protocol version 2 and a successful two-instance combat test. `Build` does not touch either installed mod.

## Current milestone

The first milestone provides the deterministic state machine for mode-switch barriers, epoch/sequence rejection, three pause policies, a canonical snapshot fingerprint, repair attempts and automatic turn-based fallback. A matching game/mod/DLC/protocol check prevents starting an incompatible lobby. Protocol version 2 adds a dormant host-to-client combat snapshot and client hash reply, with a game-frame publisher, main-thread state collection and stale-sequence rejection. This is not yet a playable real-time simulation: client commands, AI/rule authority, safe mode-switch commits, repair transport and UI are not connected. A hash mismatch is only logged, not repaired; in moving combat it may also be a timing difference rather than a real divergence. The stable turn-based runtime remains the only supported in-game mode. Do not create the validation marker merely to bypass this lock.

The local variant includes a complete `frFR` pack matching all 188 English keys and placeholders. The build checks it before compiling. The two known local voice mods (`WOTR_AIVO` and `SpeechMod`) may differ between players; all other gameplay mods remain subject to strict version matching. This is a network compatibility rule, not proof that voice playback works in-game. See `docs/session-2026-09-30-diagnostics.md` for the evidence and outstanding voice check.
