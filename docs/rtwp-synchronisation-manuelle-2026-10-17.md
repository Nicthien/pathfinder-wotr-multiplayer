# Synchronisation manuelle — 0.4.16.54 / protocole 70

Bouton « Forcer la synchronisation » dans les réglages combat du menu multijoueur, uniquement chez l'hôte. Le bouton est créé avant le masquage des réglages pendant les transitions et reste donc accessible lorsque le combat attend une synchronisation.

- Tour TB en démarrage : redemande les confirmations manquantes, sans fabriquer d'accusés.
- Synchronisation de début de tour : renvoie exactement le checkpoint mémorisé pour ce tour. Le client déduplique son application et renvoie sa confirmation sans rejouer les effets de zone.
- Synchronisation de fin de tour : renvoie le checkpoint existant et réarme les tentatives, en conservant le blocage de sauvegarde tant que les confirmations n'ont pas réussi.
- RTwP : utilise la réparation existante. Une transition possédant déjà un checkpoint reçoit une relance de son message au lieu d'une réparation concurrente.
- Action TB en cours, reconstruction native en cours ou état sans checkpoint utilisable : demande refusée avec message explicatif. Un arrêt protégé de transition TB peut rester non récupérable par ce bouton.

Les messages de début de tour portent maintenant la partie, le chargement, le combat, le round et l'unité. Un doublon déjà appliqué ne relance ni règles ni effets. Une application en échec n'est pas rejouée aveuglément. Limitation des clics à une demande toutes les trois secondes ; journal `[ManualSync]`.

Choix livré : synchronisation forcée, sans bouton de victoire/fin de combat artificielle. Les ennemis, l'XP, le butin et les scripts de victoire ne sont pas modifiés par ce contrôle.

Vérification : compilation Release, 19 tests ciblés réussis (synchronisation manuelle et récupération de session, dont rejet d'une ancienne époque), résultat `src/WOTRMultiplayer.UnitTests/TestResults/manual-sync-054-final.trx`. Aucun jeu lancé, aucune installation modifiée, aucune preuve de validation moteur créée.
