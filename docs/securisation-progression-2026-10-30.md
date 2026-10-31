# Sécurisation des progressions répliquées — protocole 92

30 octobre 2026. Complément aux corrections de progression et de dialogue du protocole 91.

Suite de la revue : [fin de progression au changement de zone et validation du relais](corrections-fin-progression-relais-2026-10-30.md).

## Sauvegarde et échec d'application

Les deux acteurs bloquent désormais la sauvegarde lorsqu'une progression est active, attend son application ou est en échec. Une exception de relecture pose un verrou lié à la session et au chargement courant, conserve l'état de défaut et demande une pause. Une remise à zéro de zone ne peut pas effacer ce verrou. Une progression interrompue par un changement de zone suit également ce chemin protégé.

Le service signale aussi les échecs survenant dans ses callbacks internes : le retour du callback extérieur n'est plus assimilé à la réussite de la mutation mise en file. Les cibles mécaniques manquantes, classes, aptitudes, sorts ou phases introuvables ne sont plus simplement journalisées puis ignorées pendant une application protégée. Les rafraîchissements facultatifs des boutons d'une fenêtre absente conservent leur comportement sans mutation mécanique.

Le message français et anglais précise que la sauvegarde reste bloquée et demande de revenir au menu principal puis de recharger une sauvegarde sûre de l'hôte. Ce chemin est un arrêt protégé, pas une reconstruction automatique de la progression. Un nouveau chargement ou une nouvelle session ne reprend pas les callbacks de l'ancien état. Une validation ou une annulation correctement répliquée libère normalement le verrou d'attente.

## Tous les messages entrants sont protégés jusqu'à l'application

Le chemin direct des messages de progression porte maintenant la même autorisation que les relectures différées. Le service capture cette autorisation avant sa mise en file, puis vérifie la session, le chargement et la progression avant l'action réelle. Un choix reçu dans une ancienne session ne consulte donc plus la fenêtre ouverte dans la suivante.

Les handlers directs et les notifications de démarrage sont eux-mêmes dispatchés sur le thread du jeu avec une revérification de session. Un démarrage mis en file ne peut pas créer une progression dans une session qui a remplacé celle de sa réception ; son ouverture de fenêtre bénéficie également du contrôle dans le service.

Une erreur après le début de l'application reste signalée même si le callback a entre-temps terminé la progression. La fin d'un contrôleur ne peut pas masquer une mutation partielle suivie d'une exception.

## Ouvertures imposées par le jeu

Le message `NotifyLevelingStarted` distingue explicitement les ouvertures forcées avec `IsForced`. Il s'agit d'un changement du protocole, désormais **92** : tous les joueurs doivent utiliser le même nouveau paquet.

- Si la notification hôte arrive d'abord, son identité et les choix suivants sont conservés en attendant l'ouverture native. Aucun contrôleur de relecture ni fenêtre supplémentaire n'est créé pour cette attente. L'ouverture native adopte ensuite cette identité et applique les choix en ordre.
- Si l'ouverture native arrive d'abord, la cliente attend l'identité hôte avant d'autoriser les décisions. La notification adopte l'interface existante et conserve le propriétaire réel.
- Un callback natif répété ne remet pas une identité déjà confirmée en attente. Un chevauchement avec une progression incompatible échoue de façon protégée plutôt que de remplacer silencieusement l'état local.

Les accusés de phase sont réémis après adoption de l'identité hôte. Les callbacks de phase périmés vérifient également leur identité après une attente.

## Régressions et validation

Les reproductions de la revue sont intégrées dans `MultiplayerLevelingSafetyTests.cs`. Elles couvrent les deux ordres d'arrivée et les deux propriétaires, les choix reçus avant l'ouverture native, la protection d'un dispatch direct, les échecs de service, la persistance du verrou après remise à zéro de zone et sa libération après une validation ou une annulation réussie. Le transport du drapeau forcé est testé dans les deux sens.

Résultats définitifs, SDK 9.0.318 et configuration Release :

| Variante | Principaux | Réseau | Localisation | Total | Échecs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Standard | 1 357 | 208 | 33 | 1 598 | 0 |
| Prototype | 1 357 | 208 | 33 | 1 598 | 0 |

La solution complète, Playground compris, compile dans les deux variantes. Les six contrôles du paquet réussissent pour chacune avec le protocole 92 lu dans la DLL. Les contrôles de localisation sous PowerShell 7 et Windows PowerShell 5.1 confirment 280/280 clés et paramètres ; `git diff --check` réussit. Les TRX sont conservés sous `local-artifacts/verification-leveling-safety-complete-2026-10-30/`.

La validation utilise le SDK 9.0.318 imposé par `global.json`. Les artefacts et les anciens résultats sont conservés. Aucun jeu ou audio lancé, aucune installation ou activation du mod, aucune sauvegarde originale utilisée ou modifiée, aucun marqueur de validation runtime et aucun commit créé. Les tests de contrôleur en monde Unity et la validation multijoueur réelle restent à effectuer ; les tests automatisés ne les remplacent pas. L'avertissement AutoMapper NU1903 préexistant reste hors de ce correctif.
