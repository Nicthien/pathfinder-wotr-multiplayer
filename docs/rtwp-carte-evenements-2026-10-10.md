# Carte globale — événements côté cliente

Version : **0.4.16.47**, protocole **66**. Même ZIP requis sur les deux PC.

## Symptôme et niveau de preuve

Nicolas rapporte une rencontre visible chez l'hôte, avec un son mais aucun événement visible chez sa nièce. L'heure exacte manque. Les journaux fournis attestent la réception de certaines rencontres et contiennent aussi des messages « popup déjà ouverte » ; ils ne prouvent pas à eux seuls la cause de l'événement manquant.

Le code présentait trois fragilités corrigées ici : aucune vérification du marqueur après création, abandon des fenêtres quand une autre modale existe, et dépendance au déclenchement local des fenêtres de collecte et d'arrivée.

## Changements

- Les rencontres de l'hôte portent un identifiant dédupliqué et un contexte session/sauvegarde/zone. Les messages anciens sont rejetés. Une carte encore en initialisation met la rencontre en attente.
- Après création, la cliente vérifie le marqueur : présence, activation et position. Deux essais de recréation visuelle au maximum, sans relancer la rencontre. Un marqueur hors écran provoque un recentrage unique de la caméra.
- Collecte, fatigue et épuisement attendent une fenêtre disponible au lieu d'écraser une autre modale. Une fenêtre déjà affichée naturellement n'est pas dupliquée. Les boutons suivent les droits réseau existants.
- La collecte reconstruite cible le lieu confirmé par l'hôte ; la validation distante n'accepte plus une modale sans rapport. Le drapeau de collecte du lieu empêche une deuxième collecte par ce chemin.
- L'arrivée confirmée transmet le lieu exact. La cliente peut recréer la fenêtre lorsque son groupe a effectivement atteint ce lieu, sans téléportation ni nouveau trajet. Les prévisualisations de destination de l'hôte restent locales.
- Les attentes sont bornées à trente secondes, les files à seize éléments. Changement de contexte et fermeture confirmée annulent les attentes concernées. Un échec produit une notification française et un journal `[MapEvent]`, pas un faux accusé de réussite.

Les fenêtres propres aux armées et à la croisade conservent leurs callbacks existants. Aucun nouveau tirage de rencontre n'est introduit dans la réparation du marqueur. Les modifications précédentes, dont invocations et montée de niveau réservée au propriétaire, sont conservées.

## Vérifications et limites

Compilation Release sans lancement du jeu ni suite de tests. Archive UMM vérifiée par structure et empreintes. La compilation signale la vulnérabilité connue de la dépendance AutoMapper 10.1.1 ; cette dépendance n'est pas remplacée dans ce correctif ciblé.

Paquet local : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.47-UMM.zip`.

Empreinte du candidat : `47B8ED773FF5C3E148C51AF9B9D048B103F23309322D40CBFF77A7578D867F8F`.

SHA256 du ZIP : `BA7003F7F4E9948A7734AF3743B0F7123C3918D04B6D516DFA4D31D04CB0AB86`.

**Pas de validation en jeu** : visibilité effective des marqueurs, enchaînement des fenêtres, compteurs de ressources et poursuite du voyage sont à confirmer à deux. Le contrôle du marqueur ne prouve pas son rendu dans tous les cas d'occlusion ou de matériau. Ce paquet ne garantit pas la correction de toute désynchronisation ni la finalisation du RTwP.

Le mod stable, les sauvegardes originales et le doublage n'ont pas été modifiés. Aucun paquet installé ou marqueur de validation moteur créé.

## Installation et retour utilisateur

Jeu fermé, installer le même ZIP `.47-UMM.zip` avec Unity Mod Manager sur les deux PC. Ne pas activer simultanément le mod multijoueur stable et la variante RTwP.

Si un événement reste invisible, noter l'heure et le lieu, puis conserver les journaux des deux joueurs. Les lignes `[MapEvent]` distinguent attente de fenêtre, réception ancienne, duplication, réparation de marqueur, recentrage et échec d'affichage.
