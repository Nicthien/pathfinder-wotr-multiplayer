# Rapport d’essai à deux instances — v0.5.0-rtwp A0, 31 octobre 2026

## Verdict

La préversion `wotr-multiplayer-v0.5.0-rtwp-a0-prototype` **n’est pas jouable dans le scénario essayé**. Le salon et le chargement partagé fonctionnent, mais le premier combat tour par tour se bloque. Une nouvelle rencontre avec le temps réel déjà sélectionné reste bloquée en synchronisation tout en laissant la simulation native combattre sans cadre RTwP stable.

Quatre défauts sont consignés ci-dessous. Les deux plus urgents sont la simulation autonome pendant `Resyncing` et l’absence de pause de protection dans cet état, car ils peuvent faire diverger une partie sans que les protections n’arrêtent le combat.

## Cadre de l’essai

| Élément | Valeur contrôlée |
|---|---|
| Archive | `wotr-multiplayer-v0.5.0-rtwp-a0-prototype.zip` |
| SHA-256 de l’archive | `0779D1B8BD34FAD008051861AA9FCAFB0218CD34EE4D8ED125600F7224DD5DF4` |
| Commit de publication | `cda55a056aeaa067c2cfda7c2d2ed0a06153e21f` |
| Version / protocole | `0.5.0` / `92` |
| Empreinte du paquet installé | `02400A39509D8D0F0BD42D726DBBE98F67377537D2896554B6BED461ABAF8174` |
| Contrôle du contenu | 47 fichiers conformes au manifeste de publication |
| Jeu | WotR `2.7.0x`, assembly SHA-256 `2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953` |
| Instances | Deux processus réels sur une même machine, PIDs hôte `49992`, cliente `50152` |
| Profils | `local-artifacts/runtime-profiles/host` et `client`, isolés et marqués pour le test |
| Sauvegarde | Copie `Manual_119_Rencontre_de_rue__13_Rova__IX__4715__00_36_19.zks` |
| Connexion | IP directe `127.0.0.1:1024` |

Il ne s’agit **pas** d’un essai sur deux PC physiques. Les commandes ont été envoyées à la passerelle de test du mod, qui appelle les workflows réels du jeu ; aucune téléportation, aucun résultat aléatoire et aucune validation de combat n’ont été simulés.

Le paquet initialement présent affichait déjà `0.5.0`, mais sa DLL principale différait de l’archive publiée. Il a été sauvegardé avant l’essai et remplacé par le paquet exact ci-dessus.

## Ce qui a fonctionné

- Chargement du seul mod expérimental sur les deux instances : `SUCCESSFUL LOADED 1/1 MODS`, 940 méthodes Harmony patchées.
- Création du salon hôte et connexion directe de la cliente.
- Attribution de Caina à la cliente et passage des deux joueurs prêts.
- Transfert et chargement synchronisés de la sauvegarde.
- Arrivée dans `RE_KenabresStreets02`, avec sept entrées de groupe, les mêmes identifiants, PV et positions initiales des deux côtés.
- Levée synchronisée de la pause de chargement à 10:54:44. Une différence de pause observée pendant le chargement était donc transitoire et n’est pas signalée comme bug.

## Bugs à corriger

### A0-01 — Bloquant : le premier tour tour par tour ne démarre pas

**Reproduction**

1. Charger la copie de sauvegarde avec un hôte et une cliente.
2. Déclencher le combat contre `Baphomet Cultist` (`UN_-555712917_1`).
3. Laisser l’initialisation tour par tour atteindre `Running`.

**Résultat attendu :** le premier tour de `UN_-1506828567_1` démarre sur les deux instances.

**Résultat obtenu :** l’hôte attend indéfiniment l’initialisation de la cliente. La cliente sort du combat natif à 10:55:55. Les demandes de récupération répétées à 10:55:55, 10:55:57, 10:56:00, 10:56:02 et 10:56:04 ne rétablissent pas le tour.

**Preuves**

- Hôte, lignes 343–360 : passage à `Running`, puis `Unable to start turn due to missing players turn initialization` et cinq messages `[TurnRecovery]`.
- Cliente, lignes 314–316 : `Combat state changed. InCombat=False`, puis `Combat ended`.

**Piste de correction :** sécuriser l’accusé d’initialisation du premier tour et le cas où la cliente a déjà quitté le combat. Le chemin `OnNotifyInvalidCombatTurnStarted` exige actuellement `Stage == Running` (`src/WOTRMultiplayer/Services/MultiplayerClient.cs:3247`) ; il ne peut donc pas réparer une cliente dont le combat natif est terminé. La cause initiale de cette sortie de combat reste à identifier.

### A0-02 — Critique : `Resyncing` ne termine pas et la mécanique continue hors autorité RTwP

**Reproduction**

1. Après le blocage précédent, conserver `RealTimeWithPause` comme préférence sur les deux instances.
2. Déclencher une nouvelle rencontre contre le même cultiste.
3. Observer les états réseau et les journaux pendant 45 secondes.

**Résultat attendu :** le combat reste gelé pendant l’initialisation, puis les deux pairs atteignent un état RTwP stable, avec époque et séquence valides. En cas d’échec, une réparation ou un arrêt protégé doit empêcher toute simulation autonome.

**Résultat obtenu :** l’hôte passe à `Resyncing` à 10:57:06.502 et n’atteint jamais `Running`. La cliente déclare la fin du combat à 10:57:06.865, puis le redémarre et le termine encore. Malgré cela, les attaques et les jets de dégâts continuent.

**Preuves**

- Hôte, ligne 414 : dernier stade réseau `Resyncing`.
- Hôte, dès la ligne 408 : nombreux `NotifyAIActionSelected` avec `RealTimeEpoch=0` et `RealTimeSequence=0`.
- Cliente, lignes 465–466 et 505 : jets locaux `RuleAttackRoll` et `RuleAttackWithWeapon`, avec `CombatSeed=0`.
- Relevé final : les sept personnages de l’hôte sont encore `isInCombat=true`, ceux de la cliente `isInCombat=false`. Plusieurs positions diffèrent de plus de 0,75 m.
- Le relevé des deux instances indique `isInCombat=false`, `networkCombatStage=null`, `combatBarrierActive=false` et `savingBlocked=false`, alors que les unités de l’hôte combattent encore.

Cela viole deux invariants : l’hôte n’est plus seul à produire les résultats mécaniques, et un état non synchronisé n’est pas traité comme un échec protégé.

**Piste de correction :** geler réellement la simulation et bloquer la sauvegarde pendant toute l’initialisation RTwP. Interdire les choix d’IA, attaques et jets locaux tant que le combat n’est pas dans une époque stable. Ajouter un délai maximal à `Resyncing`, suivi d’une récupération protégée ou d’un arrêt explicite ; ne jamais laisser le moteur natif poursuivre seul.

### A0-03 — Majeur : `NullReferenceException` après l’échec du redémarrage de combat

**Résultat obtenu :** une bascule TB → RTwP demandée pendant le premier blocage entraîne un redémarrage de combat sur la cliente. L’hôte réinitialise son propre combat, attend 20 secondes, puis lève une exception non gérée. La même exception apparaît une seconde fois pendant la rencontre suivante.

**Preuves**

- Hôte, lignes 382–392 : première exception à 10:56:29 dans `OnNotifyCombatInitiated`.
- Hôte, lignes 704–716 : deuxième exception à 10:57:36.
- Pile : `MultiplayerHost.OnNotifyCombatInitiatedAsync`.

**Cause directe localisée :** `src/WOTRMultiplayer/Services/MultiplayerHost.cs:2739` ignore le résultat booléen de `WaitWhileTrue`. Après le délai, `Game.Combat` peut encore être nul ; la ligne 2740 accède alors à `Game.Combat.PlayersInCombat`.

**Correction minimale :** vérifier le résultat de l’attente et la référence `Game.Combat` avant tout accès. L’échec doit suivre une récupération protégée et ne pas produire d’exception asynchrone non gérée. Cette garde ne remplace pas la correction des blocages A0-01 et A0-02.

### A0-04 — Majeur : impossible de pauser pendant la simulation non synchronisée

**Résultat obtenu :** la demande normale de pause de l’hôte pendant A0-02 retourne `accepted=false`, `requestedPaused=true`, `isPaused=false`. Deux secondes plus tard, les deux instances restent non pausées et les personnages de l’hôte continuent le combat natif.

**Résultat attendu :** une pause de protection locale doit rester disponible même si le stade réseau est absent ou incohérent. Une demande multijoueur peut être refusée, mais elle ne doit pas laisser une simulation divergente sans moyen sûr de l’arrêter.

**Piste de correction :** dissocier la pause de sécurité locale du vote normal de pause. L’activer automatiquement dès la détection d’un stade perdu, d’une époque nulle en combat ou d’un dépassement de délai de synchronisation, puis conserver le blocage de sauvegarde jusqu’à une récupération vérifiée.

## Chronologie synthétique

| Heure locale | Événement |
|---|---|
| 10:49:14–10:49:22 | Chargement du mod publié sur les deux instances |
| 10:52:25 | Lancement du chargement partagé |
| 10:54:44 | Pause de chargement levée des deux côtés |
| 10:55:47 | Première rencontre, mode tour par tour |
| 10:55:53 | Initialisation réseau `Running` |
| 10:55:55 | Premier tour bloqué ; sortie de combat de la cliente |
| 10:56:07 | Demande de bascule vers RTwP |
| 10:56:29 | Première `NullReferenceException` de l’hôte |
| 10:57:03 | Seconde rencontre avec RTwP préféré |
| 10:57:06 | Passage à `Resyncing`, sans passage ultérieur à `Running` |
| 10:57:13–10:58 | Attaques et dégâts natifs malgré l’absence d’état RTwP stable |
| 10:57:36 | Deuxième `NullReferenceException` de l’hôte |
| 10:58 environ | Pause normale refusée ; divergence hôte/cliente constatée |

## Scénarios non validés

Le blocage a empêché de valider les commandes volontaires de déplacement et d’attaque en RTwP stable, la pause/reprise normale, la mort, la convergence finale, la réparation, le repli tour par tour et la sauvegarde. Les dialogues, montées de niveau, changements de zone, invocations, sorts complexes, montures, effets audio et le rendu visuel n’ont pas été testés. Aucune campagne complète ni validation sur deux PC physiques n’est revendiquée.

## Preuves et remise en état

- Journaux finaux : `local-artifacts/diagnostics/a0-v0.5.0-published-20261031-1049/final/host/` et `.../client/`.
- Journaux antérieurs conservés dans `local-artifacts/diagnostics/a0-v0.5.0-published-20261031-1049/avant-lancement/`.
- La sauvegarde d’essai n’a pas été réécrite : sa date de modification UTC reste `2026-10-23T19:53:52Z`, antérieure au test, et le profil hôte contient toujours une seule archive `.zks`.
- SHA-256 de cette copie après l’essai : `C6A4040F5E67D529CB5A709B5642372D27487E71C8F777989943DF66FDC0E30A`.
- Les deux processus ont été fermés. Aucun fichier de sauvegarde de campagne n’a été créé volontairement et aucune sauvegarde n’a été forcée.
- Le paquet présent avant le test a été restauré dans le jeu ; le paquet publié testé est conservé hors `Mods` sous `local-artifacts/mod-slots/a0-published-v0.5.0-protocole92-teste-20261031`.
- Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` n’a été créé. Cet essai ne valide pas le build pour une installation normale.
