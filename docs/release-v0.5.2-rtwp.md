# v0.5.2-rtwp — récupération du tour et synchronisation des cinématiques

Préversion expérimentale du 6 novembre 2026, protocole 92.

## Correctifs

- Lorsqu'un client reçoit le tour confirmé par l'hôte alors que son jeu conserve encore un tour natif périmé, l'ancien tour est désormais terminé et libéré avant la reprise. Cela rétablit les déplacements et les attaques qui pouvaient disparaître comme si le personnage était sorti du combat.
- Le démarrage d'une cinématique chez un autre joueur crée désormais une barrière de pause locale. Le monde reste arrêté jusqu'à la fin de toutes les cinématiques annoncées, ce qui évite qu'un joueur continue d'avancer pendant qu'un piège ou une scène se déclenche chez l'autre.
- La barrière respecte une pause déjà active, les pauses forcées et les transitions de synchronisation du combat. Une cinématique locale simultanée n'est pas interrompue et la commande de pause manuelle ne peut pas contourner la barrière.
- Les déconnexions, réinitialisations et changements de session effacent l'état transitoire de la barrière afin qu'il ne soit pas réutilisé dans la partie suivante.

## Téléchargements

- `wotr-multiplayer-v0.5.2-rtwp.zip` : variante Release standard (`HostPresentationPrototype=false`).
- `wotr-multiplayer-v0.5.2-rtwp-a0-prototype.zip` : variante Release A0 (`HostPresentationPrototype=true`), pour les essais du moteur de présentation RTwP.
- `SHA256SUMS.txt` : empreintes des deux archives.

Choisir une seule variante et installer exactement le même paquet chez tous les joueurs. Les deux archives ciblent `00_WOTRMultiplayerRTwP`. Un seul des slots stable et expérimental doit être actif ; conserver le slot inactif hors de `Mods`. Les bibliothèques propriétaires du jeu, configurations locales, sauvegardes et journaux privés ne sont pas inclus.

## Vérifications et portée

Les deux variantes sont compilées et testées en Release avec le SDK 9.0.318 et la version d'assembly 0.5.2.0 : **1 661 tests C# par variante** (1 416 principaux, 212 réseau et 33 localisation), six contrôles de protocole et contrôle des 281 clés de localisation. Les tests automatisés couvrent la récupération atomique d'un tour client périmé, le début et la fin de la barrière de cinématique, la conservation d'une pause antérieure, le chevauchement d'une cinématique locale et le nettoyage lors des réinitialisations de session. Les preuves de compilation proviennent des suites locales ; aucune réussite de CI GitHub n'est revendiquée.

Ces correctifs n'ont pas été validés en direct sur deux instances du jeu. Le manifeste reste donc `runtimeValidated=false` et aucun marqueur de validation runtime n'est fourni. Cette préversion ne certifie ni une campagne complète, ni deux PC physiques, ni toute la matrice de reconnexion et de réparation. Essayer sur des copies de sauvegardes avec les installations et profils de test adaptés. L'avertissement AutoMapper NU1903 préexistant subsiste.
