# Corrections de session multijoueur — 11 novembre 2026

Ce rapport reprend les deux blocages restants de la discussion « Corriger les bugs
de session multijoueur ». Les essais utilisent exclusivement les installations,
profils et copies de sauvegardes de `local-artifacts/combat-save-lab/`.

## Sauvegarde native pendant un combat TB

### Correction

- L'hôte actif conserve les restrictions natives autres que `Player.IsInCombat`,
  mais le mod ne refuse plus la demande à cause de ses propres barrières.
- Lorsque le jeu écrit `tbm.json`, l'hôte capture son combat courant, les
  propriétaires, les générateurs, les graines et les champs omis par le
  sérialiseur natif. Le ZIP natif conserve son nom et son parcours habituels.
- Avant `ZipSaver.Save`, le contenu est validé et chaque fichier reçoit un hash
  dans `multiplayer-combat.state`. Ionic doit d'abord enregistrer ses entrées
  en mémoire pour permettre leur lecture ; le flush ne ferme pas le `ZipSaver`.
- Aucun accord préalable du client ni aucune comparaison de divergence ne
  bloque cette écriture. Le manifeste ne prétend pas que les deux mondes étaient
  identiques au moment de la sauvegarde.
- Si la capture ou la validation n'est pas possible, la sauvegarde native reste
  écrite sans manifeste. Elle n'est alors pas présentée comme une sauvegarde de
  reprise de combat.

### Restauration

- La projection capturée conserve les membres réellement présents dans le
  combat natif plutôt que l'historique de présentation RTwP.
- Les emplacements de sorts recréés différemment par le chargeur sont normalisés
  sur l'hôte et le client dans la même transaction avec rollback.
- La transaction de chargement utilise la projection de restauration qui gère
  les arrondis et identifiants natifs. Les hashes temps réel ordinaires restent
  stricts hors chargement.
- Une exception pendant la finalisation cliente suit désormais le mécanisme de
  refus existant au lieu d'échapper au gestionnaire et d'attendre le timeout.

### Preuves et limites

- Les archives `Manual_121` et `Manual_123` contiennent le manifeste du protocole
  93 et les hash de leurs 72 fichiers natifs.
- Après rechargement, hôte et client sont en combat TB `Running`, phase `Stable`,
  sans pause forcée ni blocage de sauvegarde. Les confirmations
  `RestorePrepare`, `RestoreCommit`, `RestoreComplete` et la libération sont
  présentes dans les journaux. La comparaison du groupe ne signale aucun écart.
- Preuves conservées notamment sous
  `test-runs/normal-save-restored-20261111-145907/` et
  `test-runs/normal-save-final-20261111-151040/`.
- Une sauvegarde demandée avant que le tour réseau atteigne `Playing` peut rester
  une sauvegarde native sans manifeste. La reprise en RTwP, les actions après
  rechargement, une fin de combat et la matrice complète d'erreurs restent à
  vérifier pour ce nouveau chemin.
- Une tentative de passage en RTwP après une restauration a révélé un défaut
  distinct : `RuleEnterStealth` est exécuté comme une action autonome cliente,
  puis la reconstruction native n'est pas validée et le repli reste protégé.
  Ce résultat n'est pas une réussite du changement de mode.

## Combat démarré pendant un dialogue

- Le début du combat ferme le dialogue actif et efface son état réseau sur le
  pair concerné. Le message natif de fin de mode continue de synchroniser l'autre
  pair.
- Si cette fermeture laisse le contrôleur TB non initialisé, la notification
  native `HandlePartyCombatStateChanged(true)` est rejouée avant la construction
  de l'état réseau. Cela reconstruit la liste des participants au lieu de lancer
  les jets d'initiative sur une liste vide.
- Le test unitaire vérifie la fermeture du dialogue ; l'appel natif est limité à
  une session multijoueur réellement active afin de ne pas initialiser le moteur
  dans les mocks.
- Essai sur deux instances : dialogue ouvert sur les deux mondes, combat forcé
  sans fermeture manuelle, puis `Finalizing -> Running`. Les dialogues sont
  fermés, aucune erreur de récupération n'apparaît et la comparaison du groupe
  est convergente. Journaux : `test-runs/dialog-combat-success-20261111-151902/`.


## Correctif complémentaire — capture native et passage RTwP

- Le diagnostic avant/après capture du dernier essai montre exactement pourquoi la
  reconstruction native échoue : deux invisibilité/cutscene natives perdent
  leur `UniqueId` et voient leur `NextTickTime` réinitialisé pendant le cycle
  natif de suspend/reprise.
- Le correctif mémorise les faits existants au démarrage de la capture et
  réutilise ces identifiants sur le même objet fact au re-attach, tout en
  restaurant horaires et rank des buffs. Un nouveau fait pendant la barrière
  échoue toujours.
- Une sauvegarde demandée lorsque le tour réseau n'est pas encore `Playing`
  reste explicitement une sauvegarde native ordinaire : la capture écrit un
  message de diagnostic et ne génère pas de manifeste de reprise.
- Un test unitaire verrouille désormais la sauvegarde demandée avant
  `Playing` : elle reste native et sans manifeste.
- Le patch `RuleEnterStealth` ne devient pas autorisé côté client. Le mécanisme
  de protection reste identique ; seul le rejeu de reconstruction native
  pouvait échouer sur la divergence produite par la capture.
- Le changement n'a pas été validé en jeu : relancer le scénario de deux
  instances, vérifier ensuite une commande après rechargement, une fin de
  combat et l'état de sauvegarde avant `Playing`.

## Vérifications ciblées après le correctif

- Compilation Release sans exécuter les tests.
- Tests ciblés `CombatSave` : **91 réussites**.
- Tests protocolaires RTwP ciblés : **173 réussites**.
- Suite réseau : **217 réussites**.
- Suite de localisation : **33 réussites**.
- Suite complète de cette session : **1 685 tests**, **1 679 réussis** ; les six
  échecs restants sont les tests locaux de cibles Harmony dépendant d'un
  jeu complet absent ici. Ils ne valident ni n'invalident le correctif.

## Vérifications

- Suite principale Release avec présentation prototype : **1 434 réussites**.
- Suite réseau : **217 réussites**.
- Suite de localisation : **33 réussites** ; contrôle FR/EN : **284/284** clés.
- Tests ciblés supplémentaires après les corrections d'échec : **263 réussites**.
- `git diff --check` ne signale pas d'erreur de contenu.
- L'avertissement AutoMapper connu reste présent.

Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` n'a été créé. Les corrections ne
sont pas installées dans le jeu habituel. Les essais sont locaux sur deux
instances de la même machine ; une campagne et un test sur deux PC physiques
restent nécessaires. Le dernier code local doit encore être considéré comme un
candidat de développement, pas comme une release validée.





