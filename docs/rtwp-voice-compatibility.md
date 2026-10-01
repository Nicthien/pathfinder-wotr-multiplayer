# WOTR_AIVO compatibility audit (local installation)

This is a static audit of the currently installed `WOTR_AIVOMod.dll` (SHA-256
`0F58BEDD08DE90374C6EEFD9F49AEA970981EA41D971C1F8287CABD623C9BFFF`),
not a claim that simultaneous RTwP combat and voice playback work in game. The
voice project's WAVs, SQLite database, banks, manager and installed DLL were not
modified.

The installed DLL was decompiled read-only with the locally bundled ILSpyCmd.
Three exact game-method patch overlaps with the multiplayer source were found:

| Game method | WOTR_AIVO | Experimental multiplayer | Static assessment |
| --- | --- | --- | --- |
| `SelectionManagerPC.SelectUnit` | Prefix changes `ask` to suppress an English selection voice when a French event exists | Transpiler changes which player's units can be selected | Different concerns, but joint Harmony order and behavior still require an in-game selection check. |
| `UISettingsManager.Initialize` | Postfix adds the voice settings group to `m_SoundSettingsList` | Postfix locks selected game/difficulty settings while multiplayer is active | Different lists; both postfixes must be observed together in game. |
| `LocalizationManager.OnLocaleChanged` | Postfix applies the voice localization pack | Prefix updates multiplayer's selected locale | The prefix/postfix ordering is compatible on inspection, but both visible strings and playback must be checked after a locale change. |

The voice DLL additionally patches `LocalizedString.PlayVoiceOver`,
`LocalizedString.PlayCueVoiceOver`, `SoundState.StopDialog` and Wwise playback.
The multiplayer Harmony patch tree does not patch those methods. This lowers
the likelihood of a direct audio-hook collision; it does not establish that
dialogue timing, combat barks, pause/resume and reconnection preserve voice
playback. A prior solo-host game smoke of protocol 25 loaded a save with
`WOTR_AIVO` enabled, but the protocol-29 menu-only smoke had it disabled.

Release evidence still required: check the three shared patch sites during
play, audible French dialogue and combat barks, pause/resume, locale change,
save/load, and a two-player RTwP encounter. Keep the exact build and game logs
with that test. Do not turn the runtime validation gate on from a startup smoke.

## Protocol-38 startup smoke (2026-10-01, 09:00–09:03 CEST)

The staged experimental package with fingerprint
`F1567ECF0404B9E7725D4C0910BE02D7E38E29C8A54E38878EDEE6A28801A90D`
was temporarily enabled alongside the installed `WOTR_AIVO` 0.2.0 on game
`2.7.0x` (`Wrath.exe` SHA-256
`E82781EF9C1E1438719B0AA39C6EC5A8B6C51875F279E98F3EE31D45E3FB7DDD`).
`Player.log` reports UMM successfully loaded exactly those two mods. The
multiplayer log reports 446 Harmony-patched methods, merged `frFR`, and created
host/join menu content. AIVO reported a 32,647-event French catalog, a
5,686-clip bark/skill-check catalog, and 184 production banks loaded with
`AK_Success`; the installed AIVO DLL hash still matches the static audit above.
It also logged `Missing localization pack for frFR` for its own interface.
Unobserved external `ConnectFailure` exceptions occurred without any game
session or direct-IP connection being started; they are not evidence of a
multiplayer failure.

This proves simultaneous startup and voice-asset loading only. No save,
spoken line, pause, combat or second peer was exercised. The game was closed
through its main window, the Unity Mod Manager preferences were restored to
their original SHA-256
`4F3F2BB1AB19A7A314FED91BD2620AFB94E91EA375B9A5849B5FF0E4BD1C1833`,
and stable multiplayer 0.4.16 was re-enabled. The voice project and installed
voice files were not changed. The exact-build runtime validation marker remains
absent.
