# Étape A — prototype de présentation, 4 octobre 2026

## Statut et périmètre

**Implémentation partielle de A, non validée dans le moteur. Pas de candidat jouable livré.**

Dépôt retrouvé : `D:\Codex\PathFinder\WotR-Multiplayer-RTwP`.
L'ancien emplacement `C:\AI\WotR-Multiplayer-RTwP` n'est pas disponible. Aucun nouveau projet n'a été créé à cet emplacement.
Base Git : `9bbcb61361fa9a393bfc761e7552c264d973167d` ; modifications locales non commitées.
Campagne de base uniquement ; DLC exclus.

Le rapport précédent décrit la référence historique au protocole 38. Le code courant utilise le protocole **41** et la variante locale **0.4.16.3**.

## Modifications réalisées

- Prototype activé uniquement à la compilation par `HostPresentationPrototype=true`. Désactivé par défaut.
- L'acceptation d'un ordre RTwP ne déclenche plus son exécution cliente dans cette configuration. Le suivi des ordres reste enregistré.
- L'hôte capture le démarrage, l'impact, l'interruption et la fin des animations `UnitAttack` et les diffuse dans un contrat extensible `NotifyRealTimeAuthoritativeEvent`, ordonné par identifiant d'événement et d'action.
- Chaque événement autoritaire porte session, sauvegarde, époque, séquence fiable et identifiant déterministe `époque:séquence`. La réutilisation contradictoire d'un identifiant est rejetée et déclenche la récupération protégée.
- Le client crée directement un handle d'animation ; il ne crée pas un `UnitAttack` pour afficher cette notification.
- Le message porte session, graine de chargement, époque, séquence de présentation, unité/cible, paramètres d'animation et temps logique de l'hôte.
- La file cliente applique les notifications dans l'ordre, ignore les doublons et ne confirme une séquence qu'après application. Fenêtre de 128 notifications ; trou persistant au-delà de cinq secondes signalé au dispositif de réparation existant.
- Les notifications d'autres joueurs, sessions, chargements ou époques sont ignorées. L'application vérifie de nouveau sa validité sur le thread principal.
- Les checkpoints portent désormais session, chargement et dernière séquence de présentation incluse. Leur application supprime les événements déjà couverts avant de reprendre le flux.
- Les positions logiques confirmées sont appliquées immédiatement ; seul le transform visuel est interpolé sur 300 ms. Les écarts supérieurs à 20 mètres sont corrigés immédiatement.
- Les intentions portent session, chargement, identifiant stable et empreinte observée. Les commandes refusées reçoivent une réponse motivée affichée en français.
- Suspension cliente des ticks de commandes, mouvements logiques et buffs des unités en combat ; sélection d'IA cliente suspendue en RTwP. Les contrôleurs de rendu restent actifs.
- Instrumentation et blocage de `OnTrigger` pour plusieurs familles de règles mécaniques. Une règle cliente détectée provoque un signalement de défaut au mécanisme existant.
- Le chemin de sélection d'IA en tour par tour reste conservé, y compris dans la compilation du prototype.
- L'outil local permet `Build` et `PrepareA0Candidate` ; cette dernière commande produit un dossier d'essai hors du jeu. Elle ne permet aucune installation ou activation automatique.

## Reproduction sans modifier le jeu

Depuis le dépôt, dans PowerShell :

```powershell
.\tools\rtwp-local.ps1 -Action Build -HostPresentationPrototype
.\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
```

Pour recompiler le chemin précédent, sans le prototype :

```powershell
.\tools\rtwp-local.ps1 -Action Build
```

Les deux commandes utilisent le même dossier `bin\Release\net481` : la dernière compilation remplace les sorties de la précédente. Ne pas confondre ces DLL avec le paquet expérimental déjà préparé. Les variantes ont des empreintes différentes ; les deux joueurs devront utiliser le même paquet exact.

## Preuves automatiques

- Référence avant modification : 407 tests réussis, vérifiés à nouveau dans ce dépôt retrouvé.
- Compilation prototype protocole 41 : 432 tests réussis (33 traduction, 35 réseau, 364 principaux), aucun ignoré.
- Les tests couvrent séquences réordonnées, doublons, fenêtre et expiration, host/session/chargement/époque, abandon après changement de session, absence d'exécution cliente après acceptation et validation des cibles Harmony présentes dans l'assembly du jeu.
- Données obsolètes ou malformées rejetées avant recherche d'une unité dans le service de présentation.
- Catalogue français : 232/232 clés, paramètres de traduction concordants. Cela ne valide pas la restitution audio.
- SHA256 de `Assembly-CSharp.dll` étudiée : `2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953`.
- SHA256 de la DLL principale du candidat A0 : `16E89CA83D312BC25CA92A58093088CED346B3FDA9040AAC0BF1E7932021489D`.
- Empreinte exacte du candidat A0 : `D02964DBE024025624A975723EE5117A083046B7376E22F6AC27F5E63A2E99C7`.
- Candidat conservé hors du jeu sous `local-artifacts\a0-candidates\D02964DBE024025624A975723EE5117A083046B7376E22F6AC27F5E63A2E99C7`, avec `runtimeValidated=false` dans son manifeste.

L'avertissement existant NU1903 concernant AutoMapper 10.1.1 reste présent. Aucune mise à niveau de dépendance n'a été mélangée à ce prototype.

## Ce qui n'est pas démontré

1. **Aucun combat exécuté dans deux véritables instances.** Les tests avec acteurs simulés ne remplacent pas cet essai.
2. L'interpolation visuelle est implémentée et testée hors moteur, mais son interaction avec la locomotion, les montures et la caméra n'est pas validée en jeu.
3. Le canal d'attaque possède les phases début, impact, interruption et fin, mais leurs hooks Harmony et leur rendu doivent être validés dans le moteur. Le nettoyage de secours de quinze secondes reste volontairement présent.
4. Les animations personnalisées/séquences complexes ne sont pas reconstruites. Les attaques spéciales et montures ne sont pas validées.
5. Les garde-fous `OnTrigger` n'interceptent pas tous les callbacks antérieurs du Rulebook ni tous les contrôleurs/scripts capables de changer un état. L'absence complète de mécanique autonome n'est donc **pas prouvée**.
6. L'état de référence indique désormais son point de reprise des événements de présentation, mais les dégâts/ressources restent intégrés aux états existants sans journal complet de résultats durables.
7. La restitution de buffs, invocations et zones sans rejouer leurs conséquences mécaniques n'est pas faite ; étapes B/C non entamées sur cette nouvelle séparation.
8. Les barrières, sauvegardes bloquées et mécanismes de repli existants sont conservés, mais leur reprise effective depuis un client de présentation n'est pas validée.
9. Aucune validation en jeu du doublage, des voix de combat ni de la campagne. Aucun fichier de doublage n'a été modifié.

## Isolation des essais moteur

L'assembly du jeu a été inspectée avec ILSpy :

- `Kingmaker.Utility.ApplicationPaths.Init()` utilise `UnityEngine.Application.persistentDataPath`.
- `Kingmaker.EntitySystem.Persistence.SaveManager` construit et met en cache son dossier à partir de ce chemin et de `Saved Games`.
- Modifier le chemin tardivement dans le mod ne garantit donc pas l'isolation ; les caches, paramètres, journaux et la réplication Steam doivent aussi être contrôlés.

Deux instances sous le même compte ne doivent pas être lancées en supposant leurs sauvegardes isolées.

Deux profils vides ont été préparés sous `local-artifacts\runtime-profiles\host` et `client`. Dans la compilation prototype uniquement, `WOTR_RTWP_TEST_PROFILE` redirige `ApplicationPaths.persistentDataPath`, le cache et le chemin du `SaveManager`. Le démarrage est refusé si le marqueur du profil manque ou si le gestionnaire de sauvegardes a déjà commencé à charger/écrire. Le lanceur `tools\start-rtwp-a0-instance.ps1` refuse également de démarrer tant que le mod stable est actif ou que la variante A0 n'est pas l'unique variante chargée.

Un contrôle de démarrage sans sauvegarde a montré que deux processus peuvent charger simultanément la variante 0.4.16.2 et `WOTR_AIVO` avec des `Player.log` et dossiers de sauvegardes distincts. Il a aussi révélé une `Sharing violation` sur `GameLogFull.txt`, que le jeu ouvre encore dans son dossier global sous `AppData`. Ce contrôle a été interrompu à la demande de Nicolas pendant la génération de voix ; il ne valide ni le menu, ni le réseau, ni un combat. Aucun nouveau lancement ne sera effectué sans accord explicite.

**Travail restant avant l'essai A0 :** ~~isoler ou sérialiser le journal global sans modifier le projet de doublage~~ fait : en compilation prototype, le bootstrap redirige `BuildModeUtility.GetLogsDir()` vers `<profil>\Logs`, donc chaque instance écrit son `GameLogFull.txt` dans son propre profil ; reste à placer explicitement une copie de sauvegarde dédiée. Le test moteur devra être repris seulement lorsque la production de voix sera terminée et avec l'accord explicite de Nicolas.

## Prochaine porte de validation

Avant d'étendre les sorts, projectiles et entités :

1. Valider dans le moteur l'affichage du déplacement et les quatre phases d'attaque ; compléter l'instrumentation des callbacks qui produisent encore des règles.
2. Exécuter une rencontre A0 sur des copies : déplacement simultané, attaque simple, dégâts confirmés, mort et pause.
3. Collecter les journaux des deux instances et établir qu'aucune conséquence mécanique autonome n'a été produite côté client.
4. Injecter une divergence de PV et montrer soit la réparation puis la poursuite, soit un repli réellement jouable.
5. Associer les preuves à l'empreinte du paquet exact, à la version du jeu et aux réglages.

Le marqueur `local-artifacts\RTWP_RUNTIME_VALIDATED.json` reste absent. Pas de promotion, installation ou activation du prototype avant les preuves prévues. Le mod stable installé 0.4.16 et le paquet expérimental désactivé antérieur restent intacts.
