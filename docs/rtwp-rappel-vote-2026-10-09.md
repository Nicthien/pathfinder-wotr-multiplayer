# Rappel discret de vote — 0.4.16.44, protocole 64

Empreinte du candidat : `5B4BA2C0103ABDA58529787529EF488739D224051F54B0379097E140EAFA9078`. Compilation finale : zéro erreur, deux avertissements NU1903 préexistants concernant AutoMapper. ZIP : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.44-UMM.zip`.

Correction demandée après une session réelle : le premier vote de l'hôte affichait toute la notice en une bannière démesurée.

- Premier vote : aucune notification ; les indicateurs de vote existants restent présents.
- Changement de réponse : aucune notification.
- Nouveau clic sur la même réponse dans la même réplique et le même tour de vote : court rappel de deux secondes, « En attente du même vote de l’autre joueur. »
- Même comportement côté hôte et cliente. Limite de deux secondes entre rappels pour ne pas prolonger continuellement la bannière en cliquant rapidement.
- Nouveau tour ou nouvelle réplique : le premier clic ne compte pas comme une répétition. L'historique local est distinct de la liste des votes reçus par le réseau.
- Accord atteint : l'hôte valide avant tout rappel. Le vote seul après déconnexion n'est pas modifié.
- L'ancien chemin de suggestion sans clic de vote n'affiche plus la notice.

Les styles et dimensions des notifications générales du jeu ne sont pas modifiés : c'est le texte excessivement long qui est remplacé. Les éventuelles notifications distinctes de chargement/réplique non vue et de sélection d'un personnage restent conservées.

Compilation Release et contrôle du ZIP uniquement, aucun lancement du jeu ou suite de tests. L'apparence réelle du rappel court reste à confirmer dans le jeu. Aucun changement au doublage, aux sauvegardes ou aux mods installés. Installer le même ZIP .44 sur les deux PC.
