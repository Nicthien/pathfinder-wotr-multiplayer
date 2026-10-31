# Correction R1 — rechargement ordinaire depuis RTwP — 31 octobre 2026

**Résultat final :** R1 ne se reproduit plus dans le scénario testé sur deux instances réelles de la même machine. Le rechargement depuis RTwP en pause, la nouvelle rencontre, un déplacement client, la pause/reprise et la fermeture avec checkpoint final aboutissent. Les comparaisons après rechargement et après fin de combat ne montrent aucun écart de PV, PV maximum ou position pour les sept membres. Le paquet corrigé `3374C868…A36F7CB8` reste installé dans le slot expérimental ; les jeux sont fermés. Cela ne constitue pas une validation générale du mod ni un essai sur deux machines physiques.

Ce travail poursuit le [réessai des corrections A0](rapport-retest-a0-corrections-2026-10-31.md). Le défaut R1 de ce rapport concerne le chargement d'une sauvegarde ordinaire hors combat depuis un combat RTwP actif et en pause. Il ne concerne pas la restauration d'une archive de sauvegarde de combat TB.

## Cause et correction

L'ancien contexte de combat restait accessible pendant le remplacement du monde. Les callbacks de sortie de combat pouvaient lancer une capture finale sur des objets déjà déchargés. Côté client, les protections de présentation RTwP pouvaient encore intercepter les écritures natives nécessaires à la reconstruction des statistiques.

`MultiplayerActorBase.SaveLoad.cs` invalide désormais le combat, le dernier tour et le contexte RTwP avant le transfert ou le déchargement. Il renouvelle l'époque en conservant la préférence de mode et la politique de pause. Les captures différées qui dépendent de l'ancien contexte deviennent caduques. Les callbacks de combat et la publication de snapshots restent suspendus pendant ce chargement.

La cliente conserve la protection de son ancien monde pendant le transfert, puis libère les écritures de statistiques au début du chargement natif. La sauvegarde reste interdite jusqu'à la fin du cycle natif et de la pause de chargement de zone.

L'entrée depuis `Game.LoadGame` et celle depuis `LoadSavedGame` sont traitées explicitement : le chargeur natif déjà demandé par le réseau est admis une seule fois, sans nouveau transfert. Les demandes concurrentes ou faites pendant une barrière de combat lèvent un refus que le préfixe Harmony convertit en annulation du chargement natif.

## Vérifications automatisées

Sept régressions couvrent l'invalidation avant hydratation, l'annonce précédant les chunks, une capture finale déjà mise en file, la fin du cycle natif et de la pause de zone, l'entrée native hôte, l'admission unique d'une entrée préparée et le refus pendant une barrière.

La préparation du deuxième candidat en Release avec le SDK 9.0.318 et `HostPresentationPrototype=true` réussit : **1 411 tests principaux, 212 réseau et 33 de localisation**, soit **1 656 tests**, et six contrôles de protocole. Journal : `local-artifacts/verification-corrections/r1-release-candidate-final.log`.

Deuxième candidat :

- empreinte du paquet : `6BC0E0F1E90FCF8BD16C96389B2B95227C1CF251D7FAE185D8A8BFC8835FEDE8` ;
- SHA-256 de `WOTRMultiplayer.dll` : `A99B260677575F741B1F7C9E1DF2546BF2E286EB96F0054E5C097F00F2F8C031` ;
- protocole : `92` ; manifeste `runtimeValidated=false`.

## Essai intermédiaire conservé

Le premier candidat R1, empreinte `56F9B786656676F86863FE951949DDBA33779957D40FEE861646D7C7795F7DF4`, a restauré les PV normaux sur la cliente. Il a cependant révélé que l'entrée directe du chargeur hôte ne libérait pas son verrou de sauvegarde. Ce candidat n'est pas retenu comme résultat réussi. Les journaux sont conservés dans `local-artifacts/diagnostics/r1-load-20261031-1730/attempt-1-final/` ; les trois dernières régressions et l'admission native explicite corrigent ce défaut supplémentaire.

## Deuxième réessai : R1 corrigé, saturation des snapshots observée

Avec le candidat `6BC0E0F1…35FEDE8`, les deux instances atteignent un combat RTwP stable à l'époque 2, en pause, avec la même empreinte `CE4E5506BC8EA4A2`. Le chargement ordinaire commence à 17 h 52 min 37 s. Les deux contextes de combat sont invalidés avant le déchargement, et l'époque passe à 3.

La cliente termine son cycle natif à 17 h 53 min 05 s, l'hôte à 17 h 53 min 43 s. La pause de zone est levée automatiquement ; les deux mondes reviennent à `Playing` / `Default`, hors combat, phase `Stable`, sauvegarde débloquée. Les sept membres ont exactement les mêmes PV, PV maximum, positions et états de combat ; distance maximale : 0 m. Preuve : `reload-settled-comparison.json` dans le dossier de diagnostic. L'exception de capture finale, la défaite et le `host-id-timeout` de R1 ne se reproduisent pas.

Une nouvelle rencontre démarre ensuite à l'époque 4. Une pause cliente et le déplacement client `1:4:1` sont acceptés par l'hôte. Ce prolongement montre cependant une saturation distincte : l'hôte publie environ quatre snapshots par seconde alors que leur application native cliente peut prendre environ 0,75 seconde. À 17 h 54 min 15 s, des réponses pourtant valides prennent du retard et sortent de la fenêtre de conservation, déclenchant des réparations répétées. Le relevé `resumed-command-comparison.json` capture ce problème ; il n'est pas une preuve de reprise complète réussie. Les journaux correspondants sont dans `attempt-2-final/`.

## Correction de la saturation

`MultiplayerHost.SnapshotFlow.cs` limite les snapshots à un seul en attente d'application par tous les pairs synchronisés. L'accusé d'un pair ne libère pas celui d'un autre ; un doublon ne libère aucun emplacement supplémentaire. Les messages de commande, pause et réparation restent indépendants de cette limite.

La détection d'un pair muet repose sur une échéance de huit secondes plutôt que sur la publication forcée de 33 nouveaux snapshots. À l'expiration, l'hôte engage immédiatement une vérification en pause et conserve le verrou de sauvegarde. Les contextes et accusés anciens sont invalidés lors d'un changement de sauvegarde. Les 73 tests ciblés de snapshots et de rechargement passent (`r1-flow-focused.log`).

## Réessai du candidat avec régulation des snapshots

### Paquet et vérifications

- Empreinte exacte : `3374C8680B52A65F6C49E2B027E5EFEDE205EBFB676758C8FDB3F90FA36F7CB8`.
- DLL principale SHA-256 : `EF785257CD6274E0629A49B50FF5399D64BDD4E485598ED3795F765AD6E0CED2`.
- Protocole 92, build Release, `HostPresentationPrototype=true`, manifeste `runtimeValidated=false`.
- **1 412 tests principaux, 212 réseau, 33 localisation : 1 657 tests réussis**, plus les six contrôles de protocole. Journal : `local-artifacts/verification-corrections/r1-flow-release-candidate.log`.
- **73 tests ciblés réussis également avec `HostPresentationPrototype=false`** : `r1-flow-standard-focused.log`. Le build local de présentation est ensuite reconstruit à l'identique ; le paquet installé n'est pas remplacé par ce build standard.
- `git diff --check` ne signale pas d'erreur.

### Parcours en jeu

Les profils hôte et client de `local-artifacts/runtime-profiles/` utilisent la copie « Rencontre de rue ». Les commandes de la passerelle de test passent par les entrées natives et réseau existantes ; aucune injection de PV, téléportation ou levée forcée d'une barrière n'est utilisée.

1. Chargement initial partagé, attribution de Daeran à la cliente, premier combat TB puis transition RTwP. Avant le rechargement, les deux pairs sont en pause, `Running` / `Stable`, époque 2, empreinte commune `3D69AC226C181F13`, positions identiques (`flow-before-reload-comparison.json`).
2. À **18 h 05 min 50 s**, chargement ordinaire de la même copie depuis le combat RTwP actif en pause. Les contextes sont invalidés avant le déchargement, époque 3, sauvegarde bloquée.
3. Le cycle natif se termine à 18 h 06 min 18 s sur l'hôte et 18 h 07 min 00 s sur la cliente. La pause de zone est levée automatiquement à **18 h 07 min 03 s**. Les deux pairs reviennent hors combat, `Playing` / `Default` / `Stable`, sauvegarde débloquée. Les sept membres ont les mêmes PV et PV maximum (`40, 35, 40, 33, 26, 40, 29`) et les mêmes positions, distance maximale **0 m** (`flow-reload-check-comparison.json`). Aucune défaite, capture finale invalide ou erreur `host-id-timeout` ne se produit.
4. Une nouvelle rencontre démarre après rechargement et atteint RTwP stable à l'époque 4. La pause hôte est acceptée, puis le déplacement client `1:4:1` de Daeran est reçu et exécuté par l'hôte à **18 h 11 min 50 s**. La reprise hôte, puis la pause cliente, sont acceptées. Les relevés stabilisés placent Daeran exactement à `(55.64818, 40.1579742, 26.4046688)` sur les deux mondes ; les sept membres correspondent sans écart. Empreinte commune : `138EC7FFB27E4A6A` (`flow-resumed-command-comparison.json` et `flow-paused-stable-comparison.json`). La cliente reprend ensuite la partie. Aucun snapshot expiré ni boucle de réparation ne figure dans les journaux de ce parcours.
5. La fermeture autoritaire de la rencontre commence à **18 h 12 min 55 s**. La phase `Repairing` / époque 5 observée ensuite appartient à la reconstruction native protégée de fin de combat ; elle n'est pas assimilée à un échec de synchronisation. La référence native du checkpoint `4db9652a02204d17aaafdfbbf4394235` est vérifiée à 18 h 13 min 48,491 s. Après les étapes de certificat et de fermeture, l'hôte confirme **`Final combat checkpoint confirmed by all peers` à 18 h 13 min 48,993 s**, identifiant de fin `ee1ff776d580483eb703d3962062fabe`.
6. Comparaison finale : les deux mondes sont hors combat, phase `Stable`, époque 5, sauvegarde débloquée, empreinte **`779C259B26A2554B`**. Aucun écart de PV, PV maximum, état de combat ou position pour les sept membres ; distance maximale **0 m** (`flow-combat-end-comparison.json`).

Les journaux finaux sont conservés dans `local-artifacts/diagnostics/r1-load-20261031-1730/final/`. La confirmation hôte est ligne 2260 de `host/Player.log` ; la vérification native cliente ligne 4630 de `client/Player.log`. `final-before-close/` distingue les traces du scénario de celles de la fermeture des applications. Les réponses de passerelle horodatées sont dans `responses/` ; les jetons d'authentification ne sont pas inclus dans le rapport.

### Installation, conservation et limites

- Seul `00_WOTRMultiplayerRTwP` est actif dans `Mods`. Le paquet antérieur à R1 reste sauvegardé dans `local-artifacts/backups/20261031-1731-before-r1/`. Les deux candidats intermédiaires sont également conservés hors de `Mods`.
- Les paramètres Unity Mod Manager conservent leur SHA-256 initial `47E4FDE1A2687FC1861774228F93F3F215BEE4A0BB65B40B7E30EE1B5B7365B9`.
- La copie hôte conserve son SHA-256 `C6A4040F5E67D529CB5A709B5642372D27487E71C8F777989943DF66FDC0E30A`, ses 3 291 316 octets et sa date UTC du 23 octobre 2026 à 19 h 53 min 52 s. Elle reste l'unique archive `.zks` du profil hôte. Aucun original de campagne n'a été utilisé.
- Aucun marqueur `local-artifacts/RTWP_RUNTIME_VALIDATED.json` n'est créé. Les journaux restent privés.
- `completion-receipt.json` dans le dossier de diagnostic consigne l'empreinte installée, l'identité de la DLL locale reconstruite, le seul slot actif, l'absence de processus et de marqueur, ainsi que les empreintes inchangées des paramètres et de la sauvegarde.
- Des exceptions natives `CameraRig.OnDisable` et `WeatherThunderController.Dispose` apparaissent à la fermeture des jeux, après le scénario terminé. Les processus quittent ; ces traces de fermeture restent conservées et ne sont pas présentées comme corrigées.
- Aucun essai sur deux PC physiques, aucune campagne complète, aucun contrôle du doublage, des sorts complexes, des nouvelles sauvegardes de combat, de la reconnexion ou de toute la matrice de réparation n'est revendiqué.
