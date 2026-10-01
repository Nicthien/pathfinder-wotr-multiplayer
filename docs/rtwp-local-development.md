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

`Install` creates a timestamped backup of an existing experimental installation and stages it outside the game's `Mods` folder when the stable mod is active. Unity Mod Manager loads any mod folder inside `Mods`, even if its name ends in `.disabled`; inactive slots therefore live in `local-artifacts/mod-slots`. The script migrates older `.disabled` folders out of `Mods` with the game closed, checks the game directory and all source/destination slots before moving a mod, and refuses changes while `Wrath.exe` is running. Normal installation and activation remain locked until a local `local-artifacts/RTWP_RUNTIME_VALIDATED.json` records protocol version 15 and a successful two-instance combat test. For development only, `StageTest` stages an unvalidated build outside `Mods`; `EnableTestExperimental` requires the explicit `-AllowUnvalidatedTest` flag and switches away from the stable mod. Run such tests on a copy of a save, then use `EnableStable`. Neither action creates the validation marker. `Build` does not touch either installed mod.

## Current milestone

The first milestone provides the deterministic state machine for mode-switch barriers, epoch/sequence rejection, three pause policies, a canonical snapshot fingerprint, repair attempts and automatic turn-based fallback. A matching game/mod/DLC/protocol check prevents starting an incompatible lobby. Protocol version 14 adds host-to-client combat snapshots, client hash replies (including explicit application failures), host-validated move/attack/ability command messages, synchronized manual pause requests, a Prepare/Ack/Commit/Ack/Complete/Ack mode-switch transport, a paused Verify/Apply/Resume/Ack repair path with turn-based fallback after two failed applications, epoch-numbered host AI choices, a synchronized preferred combat mode and application-level snapshot response telemetry. Incoming commands are executed on the host's main thread before acknowledgement, and the client only advances its command sequence after local application. The in-game Escape menu's multiplayer lobby now shows the combat phase, preferred mode and pause policy and gives the host mode, preference, policy and manual-repair controls; both players see pause/resume controls. A preference mismatch is applied once at the next running combat, while a manual switch wins for the remainder of that combat. These controls compile and refresh from synchronized state, but their layout and behavior have not been visually tested in the running game. Initial client-side interception of player movement, attack and ability commands and host-originated order broadcast are present, but have not been exercised in game; unsupported commands are blocked in the experimental real-time path. A game save is denied during an active synchronization or repair barrier. The moving-snapshot hash covers projected combat state and the last 32 accepted host orders, but not the live command lifecycle or authoritative random stream. Client AI selection now waits for the host's choice, but rule-result authority and projectiles are not yet complete. The displayed response time includes client processing; missing application replies are not a measure of underlying TCP packet loss. All protocol and repair paths still need two-instance game validation; the stable turn-based runtime remains the only supported in-game mode. Do not create the validation marker merely to bypass this lock.

The local variant includes a complete `frFR` pack matching all 189 English keys and placeholders. The build checks it before compiling. The two known local voice mods (`WOTR_AIVO` and `SpeechMod`) may differ between players; all other gameplay mods remain subject to strict version matching. This is a network compatibility rule, not proof that voice playback works in-game. See `docs/session-2026-09-30-diagnostics.md` for the evidence and outstanding voice check.

On 2026-10-01, a local launch with game version `2.7.0x` exposed two startup failures and both were corrected: Unity Mod Manager also loaded the inactive `.disabled` folder inside `Mods`, and a Harmony prefix bound `UnitCommands.Run` by the wrong source parameter name. With inactive slots outside `Mods` and positional `__0` binding, the experimental mod loaded as the only active multiplayer mod (green status), and the French multiplayer host/join menu opened without a save. This does **not** validate the in-game combat controls, voice playback (`WOTR_AIVO` was disabled in that local session), or a two-player fight. The game was closed and stable 0.4.16 restored after the smoke check.

Snapshot failures now produce an explicit failed-application reply. When the state can be read but its fingerprint differs, the client includes a bounded list of differing unit and area-effect IDs in its reply; the host logs that list before starting paused verification. If even the turn-based fallback checkpoint cannot be applied, both sides remain paused and saving remains blocked. This is a safety stop, not a complete host-state reconstruction. Missing summons or area effects still require a fuller reconstruction mechanism before a campaign-ready release. The locale packs now also cover the new combat controls; their key count is greater than the original 189-key baseline.

Until rules, random streams, summons and effects are fully authoritative and reconstructable, saves are refused throughout an active real-time combat, even when its latest checkpoint matches. Switching safely to turn-based or ending combat permits the normal synchronized save path. This conservative restriction prevents a save from capturing a divergence during the interval between snapshots.

The mode and repair barriers now retransmit their current message twice before counting a timeout as a failed attempt. Duplicate Prepare and Commit messages are acknowledged idempotently on the client, while a full Abort or Fallback checkpoint can recover a client that missed Prepare entirely. This improves behavior under dropped/reordered application messages, but is not a substitute for a two-instance fault-injection run in the game.

Mode completion now has its own acknowledgement. The host remains paused with saving blocked until every connected client acknowledges the final Complete message; if it is missed, the host retransmits it without aborting into a potentially conflicting mode. If the peer disconnects during this last barrier, the host can finish in solo play. Both successful commits and rollback completions use this last barrier.

Repair completion likewise holds the host paused and blocks saving until each connected client acknowledges Resume. A lost Resume is retransmitted, and a duplicate is acknowledged by a client that has already resumed. This applies to both a repaired RTwP state and the turn-based fallback.

Before applying any RTwP checkpoint, the client now verifies that every referenced unit and area-effect entity exists locally. If a summon or effect is absent, no partial checkpoint is applied; the failure reply includes bounded entity IDs and the host enters paused repair. This improves diagnosis and safety, but does not yet create the missing entity; a repeatedly missing entity still causes the protected turn-based fallback stop.

The preflight also validates units referenced inside active areas and by combat engagements. A missing area occupant or engagement target is rejected before any unit, buff or area mutation, with a bounded cause such as `missing-area-unit:cloud:summon` or `missing-engagement-target:hero:enemy`.
It rejects incomplete positional, health/state, buff and area payloads before mutation as well. Scripted allies without a combat-state component are allowed, and the checkpoint updater skips engagement changes for them instead of dereferencing a null component.

The moving fingerprint now also contains a bounded ledger of the last 32 orders accepted by the host, including a digest of each move/attack/ability payload. The client records an order only after successfully executing the host's accepted message. This detects missing or altered command traffic; it is not yet a complete projection of all currently executing Unity commands.

Protocol version 15 additionally fingerprints prone state and both sides of unit engagement, so a knockdown or attack-of-opportunity relationship mismatch enters the same repair path. Engagement collections are sorted canonically before hashing. Elapsed engagement rounds are deliberately omitted from the moving hash because the game derives them from local game time and host/client snapshot reads are not simultaneous.
When a checkpoint still differs, the bounded unit diagnostic now labels prone and engagement causes where applicable instead of reporting only the unit ID.

The local test suite now also connects host and client protocol actors through an in-memory queued transport. It deliberately drops and duplicates mode-switch messages and drops a repair Prepare, then asserts both peers return to the same stable mode and epoch. A projected-unit mismatch is also tested with a mocked game service that successfully applies the host checkpoint. These are protocol-level two-actor tests, not proof that the real Unity service can reconstruct a missing unit or that combat rules converge under network faults.

The inverse case is tested as well: when the mocked client continues to report a divergent unit after every host checkpoint, both actors remain in the fallback phase with saving blocked. That protects the save, but is explicitly not the campaign-ready recovery path; reconstructing the missing game entity remains open.

Three consecutive snapshot replies aging out (a window of 32 snapshots, normally about eight seconds) now make the host pause and start the same verification barrier. An acknowledged snapshot resets the consecutive counter. This is an application-level liveness guard, not a claim that TCP packets were lost.
