# Instructions — WotR-Multiplayer-RTwP

## Portée et organisation

Ce fichier contient les consignes actives du projet `WotR-Multiplayer-RTwP`.

- Communiquer avec Nicolas en français.
- Le projet concerne la variante C# du mod multijoueur de *Pathfinder: Wrath of the Righteous*, avec le travail sur le temps réel avec pause et la synchronisation.
- Les chemins mentionnés ci-dessous sont relatifs à la racine de ce dépôt. Ne pas réutiliser les anciens chemins absolus d'une conversation après un déplacement du workspace.
- Exécuter les commandes Git, de compilation et de test depuis la racine de ce dépôt.
- Les archives externes au dépôt sont à consulter comme historique, pas comme sources actives. Tout ancien fichier de consignes qui y subsisterait est une pièce historique ; les consignes actuelles sont celles du présent fichier.

## Méthode de travail

- Lire le README, les sources et la documentation du sujet avant de modifier. Les rapports datés décrivent une situation historique : vérifier le code et les preuves actuelles avant d'annoncer un état ou une réussite.
- Préserver les modifications existantes, les sauvegardes et les données produites. Limiter les changements au périmètre demandé.
- Ne pas afficher ni versionner les secrets des configurations locales.
- Après une modification, effectuer les vérifications adaptées et préciser ce qui a été testé et ce qui reste à vérifier. Une compilation réussie ne prouve pas un fonctionnement en jeu.
- Ne jamais utiliser de dialogue JavaScript natif (`alert`, `confirm`, `prompt` et variantes). Utiliser une modale intégrée, accessible, traduite, responsive et compatible avec les thèmes clair/sombre.

## Développement et validation

- Solution : `src/WOTRMultiplayer.sln` ; projets de tests : `src/*.UnitTests/` ; outils PowerShell : `tools/`.
- Respecter le SDK de `global.json` (actuellement 9.0.318 avec `latestPatch`).
- Lire `docs/rtwp-local-development.md` pour le workflow, puis les documents `docs/rtwp-*.md` pertinents. Confirmer les règles actuelles dans le code et les scripts : ce document contient aussi un long historique.
- Pour la compatibilité audio, consulter `docs/rtwp-voice-compatibility.md` et vérifier les deux projets si le changement touche leur intégration.

Commandes depuis la racine du dépôt :

```powershell
.\tools\rtwp-local.ps1 -Action Status
.\tools\rtwp-local.ps1 -Action Build
dotnet test .\src\WOTRMultiplayer.UnitTests\WOTRMultiplayer.UnitTests.csproj -c Release
```

Choisir les autres projets de tests et contrôles de `tools/` selon le changement (réseau, localisation, protocole, dispatch). Inspecter les paramètres du script avant tout déploiement.

### Invariants à préserver

- L'hôte fait autorité sur l'état de combat et les résultats aléatoires.
- Préserver les protections de session, d'époque, de séquence et de synchronisation ; ne pas traiter un état partiellement appliqué comme une réussite.
- Ne pas autoriser une sauvegarde pendant une barrière de changement de mode ou de réparation. Une réparation échouée doit suivre le mécanisme de repli sécurisé ; ne pas contourner une protection pour débloquer artificiellement la partie.
- `00_WOTRMultiplayer` est le slot stable ; `00_WOTRMultiplayerRTwP` est le slot expérimental. Un seul doit être actif ; les slots inactifs restent hors du dossier `Mods`.
- Construire et tester en **Release** : la configuration Debug peut déployer automatiquement dans le jeu. Ne jamais copier un build Debug dans le slot stable. Utiliser les scripts de staging et conserver les sauvegardes prévues.
- Pour les essais de sauvegarde en combat, utiliser les installations et profils isolés sous `local-artifacts/combat-save-lab/`. Lire `docs/sauvegarde-combat-tb-developpement-2026-10-17.md` puis vérifier les preuves courantes. Ne pas remplacer les mods de l'installation habituelle ni utiliser les sauvegardes originales.
- Ne pas créer ni falsifier `local-artifacts/RTWP_RUNTIME_VALIDATED.json` pour contourner le verrou de validation. Une validation doit correspondre au build exact et à des essais réels sur deux instances.
- Tester sur une copie des sauvegardes. Distinguer explicitement compilation, tests automatisés, démarrage du jeu, test solo et validation multijoueur réelle.
- Ne pas déduire la compatibilité du doublage ou la convergence du combat du seul chargement des mods.
