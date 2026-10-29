# WotR Multiplayer RTwP

**Français** · [English](README.en.md)

Mod multijoueur expérimental pour *Pathfinder: Wrath of the Righteous*, avec
prise en charge du combat en temps réel avec pause (RTwP), du tour par tour et
de leur synchronisation entre plusieurs joueurs.

[![Soutenir sur Ko-fi](https://img.shields.io/badge/Soutenir-Ko--fi-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/nthstudio)

Cette variante est développée sur mon temps libre. Si elle vous est utile et
que vous souhaitez soutenir son développement, vous pouvez m'offrir un
[Ko-fi](https://ko-fi.com/nthstudio). Merci !

## Origine du projet et crédits

Ce dépôt est un fork de
[`fl01/pathfinder-wotr-multiplayer`](https://github.com/fl01/pathfinder-wotr-multiplayer),
le projet multijoueur original créé par **fl01**.

Le code d'origine, son architecture réseau et ses fonctionnalités multijoueur
restent le travail de fl01 et des contributeurs du projet amont. J'ai repris ce
code pour poursuivre son développement, le modifier et expérimenter notamment
la synchronisation du combat RTwP, la reprise de session et la sauvegarde en
combat. Les droits sur le code d'origine restent à leurs auteurs respectifs.

Cette variante n'est ni officielle ni affiliée à Owlcat Games.

## Principales évolutions de cette variante

- combat en temps réel avec pause synchronisé, en complément du tour par tour ;
- hôte autoritaire pour l'état du combat, les commandes et les résultats
  aléatoires ;
- barrières de changement de mode, vérification d'état, réparation et repli
  sécurisé vers le tour par tour ;
- synchronisation renforcée des unités, buffs, projectiles, zones d'effet,
  invocations, dialogues, événements de carte et autres interactions ;
- montée de niveau réservée au propriétaire du personnage, avec mise en file
  invisible de la réplication distante afin que deux joueurs puissent faire
  progresser leurs personnages sans partager la même fenêtre modale ;
- reprise de session et reconnexion avec contrôles d'époque et de séquence ;
- sauvegarde et restauration synchronisées en plein combat au tour par tour ;
- interface et localisation françaises complètes ;
- outils de compilation, de diagnostic, de staging et de test local isolé.

Les détails techniques et l'historique des validations se trouvent dans
[`docs/`](docs/), notamment dans
[`docs/rtwp-local-development.md`](docs/rtwp-local-development.md) et
[`docs/sauvegarde-combat-tb-developpement-2026-10-17.md`](docs/sauvegarde-combat-tb-developpement-2026-10-17.md).

## État actuel

Le projet est une **version expérimentale** destinée aux essais et au
développement. Il ne faut pas l'installer en même temps que la version stable
du mod original.

Cette branche dérive de la série `0.4.16` du projet original. Les évolutions
amont plus récentes, notamment la série `0.5.x`, ne sont pas intégrées
automatiquement : elles doivent être adaptées volontairement pour ne pas
casser le protocole RTwP déjà testé.

La sauvegarde et la reprise d'un combat ont été vérifiées avec une instance
hôte et une instance cliente réellement connectées, dans deux installations et
deux profils isolés sur une même machine. Cette preuve ne remplace pas encore
une campagne complète ni une validation sur deux PC physiques.

La montée de niveau indépendante dispose de tests automatisés du propriétaire,
de la mise en file et de la reprise de la réplication. Son enchaînement visuel
complet reste à confirmer sur deux PC physiques avant de le considérer comme
validé en campagne.

Le mod conserve volontairement des protections strictes : une sauvegarde est
refusée pendant une barrière de changement de mode, de synchronisation ou de
réparation, et un état partiellement appliqué n'est jamais considéré comme une
réussite.

## Compatibilité

- *Pathfinder: Wrath of the Righteous* `2.7.0x` ;
- Windows ;
- Unity Mod Manager `0.32.4a` ou antérieur ;
- mêmes versions du jeu, des DLC et des mods de gameplay pour tous les joueurs ;
- clavier et souris ; l'interface manette n'est pas prise en charge.

La compatibilité peut évoluer avec les futures mises à jour du jeu ou du projet
amont.

## Installation

Les paquets binaires ne sont pas versionnés dans le dépôt. Lorsqu'une version
précompilée est publiée dans la section **Releases** :

1. télécharger l'archive de la version expérimentale ;
2. fermer le jeu ;
3. conserver une sauvegarde de l'ancien mod et de la campagne ;
4. installer le paquet avec Unity Mod Manager ;
5. vérifier qu'une seule variante multijoueur est active ;
6. utiliser une copie de sauvegarde pour les premiers essais.

Le slot stable est `00_WOTRMultiplayer` et le slot expérimental est
`00_WOTRMultiplayerRTwP`. Un slot inactif doit rester hors du dossier `Mods` :
Unity Mod Manager peut charger un dossier même si son nom se termine par
`.disabled`.

## Connexion

Le menu **Multijoueur** permet d'héberger ou de rejoindre une partie. La
connexion directe par IP reste la méthode de référence. Le jeu en dehors d'un
réseau local peut nécessiter une adresse publique, une redirection de port ou
un réseau virtuel tel que ZeroTier, Radmin VPN ou Hamachi.

Tous les participants doivent utiliser la même version expérimentale.

## Compilation et tests

Le SDK est fixé par [`global.json`](global.json). Depuis la racine du dépôt :

```powershell
.\tools\rtwp-local.ps1 -Action Status
.\tools\rtwp-local.ps1 -Action Build
dotnet test .\src\WOTRMultiplayer.UnitTests\WOTRMultiplayer.UnitTests.csproj -c Release
```

La configuration **Release** est obligatoire pour les essais et les
livraisons. La configuration Debug peut déployer automatiquement des fichiers
dans le jeu.

Les installations et profils de test locaux, journaux, sauvegardes, artefacts
de compilation et marqueurs de validation restent privés et sont exclus par
`.gitignore`.

## Projet original

- dépôt amont :
  [`fl01/pathfinder-wotr-multiplayer`](https://github.com/fl01/pathfinder-wotr-multiplayer) ;
- présentation vidéo originale :
  [YouTube](https://www.youtube.com/watch?v=_YZSPrPy9XI) ;
- page Nexus Mods originale :
  [Pathfinder WotR Multiplayer](https://www.nexusmods.com/pathfinderwrathoftherighteous/mods/1116).

Pour les fonctions héritées du mod original et ses explications détaillées,
consulter également [`docs/about.md`](docs/about.md).
