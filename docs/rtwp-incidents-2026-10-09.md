# Correctif local 0.4.16.42 — incidents du 9 octobre 2026

Protocole 63 conservé : aucun contrat réseau n'est changé. Les deux joueurs doivent néanmoins installer exactement le même ZIP .42, avec la même empreinte.

Empreinte du candidat compilé : `BD0940230DCEA47C301A05DA7DC530E00D911A83BF059DFB61AECA60D1AE7071`. Compilation Release finale : zéro erreur, deux avertissements NU1903 existants concernant AutoMapper 10.1.1. ZIP prévu : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.42-UMM.zip` ; l'export contrôle la structure à plat et compare tous les fichiers archivés aux sources du candidat.

## Preuves disponibles

Journal hôte `Player.log`, session du 9 octobre, variante .41 :

- Vers 20h55 : aucune transition réseau enregistrée. Le préfixe de `IngameMenuVM.SwitchTBM` retournait simplement `false` en multijoueur.
- 21h52:59 : transmission de `SummonNaturesAllyAasimard3`, blueprint `37f4f8bdbcfb97641979b52bfb6d30db`, sans spellbook ni ConvertedFromId.
- 21h53:00 : trois grenouilles créées sur l'hôte, IDs `UN_-164100303_1`, `_2`, `_3`.
- 21h53:09 : tour hôte `_3`, tour annoncé par la cliente `e948bc11-116e-4821-98f4-3fd887973261`. Désaccord confirmé.
- 21h54:07 : Commit temps réel puis Abort refusés par la cliente (`Applied=False`, empreinte 0). Répétition du même Abort toutes les 30 secondes.

Le journal cliente n'est pas disponible. La raison exacte de l'absence des grenouilles reste donc à confirmer.

## Modifications

1. Le raccourci du jeu passe par la barrière réseau de l'hôte. Hors combat, il modifie la préférence du prochain combat. La cliente ne peut pas changer indépendamment son moteur. Une demande hôte indisponible affiche un message français.
2. La recherche de capacités donne priorité au blueprint transmis plutôt qu'à l'identifiant d'instance potentiellement partagé avec la capacité parente. Elle parcourt les variantes appartenant aux capacités de l'unité, jusqu'à quatre niveaux. L'inspection statique du jeu confirme que `AbilityData.GetConversions()` construit ces variantes sans lancer leur effet. Les sources de conversions restent recherchées par leur propre identifiant.
3. Une erreur indépendante était présente : la branche trouvant un sort spécial retournait le sort personnalisé, potentiellement null. Elle retourne désormais le sort spécial trouvé.
4. Un Abort explicitement refusé n'est plus retransmis indéfiniment. Une absence de réponse permet deux retransmissions au maximum. Ensuite, arrêt protégé communiqué aux deux PC, message explicite et sauvegarde toujours interdite. Ce changement ne constitue PAS une récupération jouable.
5. Journaux ajoutés pour les demandes clavier, variantes résolues/absentes et raison d'arrêt de la barrière.

## Limites et vérification

Compilation Release uniquement, aucun lancement du jeu, aucune suite de tests, aucune installation et aucune modification des voix ou sauvegardes originales. Aucun marqueur de validation moteur n'est créé.

Le défaut de recherche de variantes est compatible avec le symptôme des grenouilles, mais sa responsabilité dans cet incident n'est pas prouvée par le seul journal hôte. Le correctif doit être essayé sur les deux PC : trois grenouilles visibles, tour suivant jouable, puis changement de mode par T. Ne pas annoncer les invocations ou le RTwP comme validés avant cet essai.

Une reconstruction de toutes les entités après échec de transition reste à réaliser : la pause protégée n'est pas une solution permettant de poursuivre le combat.

Installation : ZIP dédié UMM uniquement, sur les deux PC et jeu fermé. Ne pas utiliser les scripts d'activation de la livraison historique, qui exigent une sauvegarde du mod stable. Le mod installé et la copie stable ne sont pas modifiés automatiquement.
