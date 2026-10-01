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

Release evidence still required: load the current experimental build with
`WOTR_AIVO` enabled; check the three shared patch sites, French dialogue and
combat barks, pause/resume, locale change, save/load, and a two-player RTwP
encounter. Record `Player.log` and the exact DLL hashes. Do not turn the runtime
validation gate on from this static audit.
