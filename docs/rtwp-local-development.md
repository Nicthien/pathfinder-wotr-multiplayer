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
.\tools\rtwp-local.ps1 -Action StageTest -AllowUnvalidatedTest
.\tools\rtwp-local.ps1 -Action EnableTestExperimental -AllowUnvalidatedTest
.\tools\rtwp-local.ps1 -Action Install
.\tools\rtwp-local.ps1 -Action EnableExperimental
.\tools\rtwp-local.ps1 -Action EnableStable
```

`Install` creates a timestamped backup of an existing experimental installation and stages it disabled when the stable mod is active. The script checks the game directory and all source/destination slots before moving a mod, and refuses changes while `Wrath.exe` is running. Normal installation and activation remain locked until a local `local-artifacts/RTWP_RUNTIME_VALIDATED.json` records protocol version 8 and a successful two-instance combat test. For development only, `StageTest` stages an unvalidated build disabled; `EnableTestExperimental` requires the explicit `-AllowUnvalidatedTest` flag and switches away from the stable mod. Run such tests on a copy of a save, then use `EnableStable`. Neither action creates the validation marker. `Build` does not touch either installed mod.

## Current milestone

The first milestone provides the deterministic state machine for mode-switch barriers, epoch/sequence rejection, three pause policies, a canonical snapshot fingerprint, repair attempts and automatic turn-based fallback. A matching game/mod/DLC/protocol check prevents starting an incompatible lobby. Protocol version 8 adds host-to-client combat snapshots, client hash replies (including explicit application failures), host-validated move/attack/ability command messages, synchronized manual pause requests, a Prepare/Ack/Commit/Ack/Complete mode-switch transport, a paused Verify/Apply/Resume repair path with turn-based fallback after two failed applications, and epoch-numbered host AI choices. Incoming commands are executed on the host's main thread before acknowledgement, and the client only advances its command sequence after local application. The in-game Escape menu's multiplayer lobby now shows the combat phase and pause policy and gives the host mode, policy and manual-repair controls; both players see pause/resume controls. These controls compile and refresh from synchronized state, but their layout and behavior have not been visually tested in the running game. Initial client-side interception of player movement, attack and ability commands and host-originated order broadcast are present, but have not been exercised in game; unsupported commands are blocked in the experimental real-time path. A game save is denied during an active synchronization or repair barrier. The snapshot hash currently covers only the projected combat state, not active orders or the authoritative random stream. Client AI selection now waits for the host's choice, but rule-result authority, projectiles and telemetry for latency/packet loss are not yet complete. All protocol and repair paths still need two-instance game validation; the stable turn-based runtime remains the only supported in-game mode. Do not create the validation marker merely to bypass this lock.

The local variant includes a complete `frFR` pack matching all 189 English keys and placeholders. The build checks it before compiling. The two known local voice mods (`WOTR_AIVO` and `SpeechMod`) may differ between players; all other gameplay mods remain subject to strict version matching. This is a network compatibility rule, not proof that voice playback works in-game. See `docs/session-2026-09-30-diagnostics.md` for the evidence and outstanding voice check.

Snapshot failures now produce an explicit failed-application reply. When the state can be read but its fingerprint differs, the client includes a bounded list of differing unit and area-effect IDs in its reply; the host logs that list before starting paused verification. If even the turn-based fallback checkpoint cannot be applied, both sides remain paused and saving remains blocked. This is a safety stop, not a complete host-state reconstruction. Missing summons or area effects still require a fuller reconstruction mechanism before a campaign-ready release. The locale packs now also cover the new combat controls; their key count is greater than the original 189-key baseline.
