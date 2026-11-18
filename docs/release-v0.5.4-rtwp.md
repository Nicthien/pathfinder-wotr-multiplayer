# v0.5.4-rtwp — prise de main des actions partagées

Préversion expérimentale du 18 novembre 2026, protocole 94.

## Nouveauté

Les dialogues, les marchands et les combats de croisade peuvent désormais être contrôlés par n'importe quel joueur de la partie :

- la fenêtre concernée indique le joueur qui a actuellement la main ;
- les autres joueurs peuvent utiliser **Prendre la main** ;
- le transfert est automatique : l'hôte assure seulement l'ordre et la synchronisation, il ne choisit pas le joueur ;
- une demande attend un état sûr : pas de choix ou de transition de dialogue, pas de transaction de marchand, et pas de synchronisation de tour de croisade ;
- la demande expire sans changer le contrôleur si cet état sûr n'est pas atteint, et un départ du contrôleur rend la main à l'hôte.

Le vote des dialogues, l'attribution des personnages et les réglages avancés du salon restent inchangés. Tous les joueurs doivent installer cette même version : le protocole 94 bloque les sessions mélangeant des versions incompatibles.

## Téléchargement

Télécharger uniquement **`wotr-multiplayer-v0.5.4-rtwp.zip`**. C'est le paquet Release standard (`HostPresentationPrototype=false`) et il est le seul fichier destiné aux joueurs.

Installer exactement cette archive chez tous les joueurs. Elle cible `00_WOTRMultiplayerRTwP` ; un seul des slots stable et expérimental doit être actif et le slot inactif doit rester hors de `Mods`. Les bibliothèques propriétaires du jeu, configurations locales, sauvegardes et journaux privés ne sont pas inclus.

## Vérifications et limites

La compilation Release, les tests ciblés des transferts, les tests réseau, les tests de localisation et les contrôles de protocole ont réussi localement. Les essais réels à deux instances n'ont pas pu être réalisés car le laboratoire de test n'était pas disponible ; cette publication ne prétend donc pas valider ces parcours en jeu. Essayer d'abord sur des copies de sauvegarde.
