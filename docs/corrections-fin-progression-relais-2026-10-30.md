# Fin de progression et autorisation du relais — 30 octobre 2026

Complément à [la sécurisation du protocole 92](securisation-progression-2026-10-30.md). Aucun format de message modifié.

Suite des passes et correctifs : [ordre, chevauchements et propriétaire des progressions](corrections-ordre-progressions-2026-10-30.md).

## Fin appliquée et changement de zone

Une relecture différée conserve désormais explicitement le fait que son callback de validation ou d'annulation a terminé la progression. Le nettoyage asynchrone peut encore être en attente : un changement de zone ne transforme plus cette entrée terminée en interruption de synchronisation. La boucle de relecture ne prend pas une progression ouverte ensuite pour la précédente.

Une progression encore en attente ou partiellement appliquée reste protégée. Une exception après le callback terminal, y compris après une remise à zéro de zone, conserve le chemin d'échec : pause demandée et sauvegarde bloquée pour la session et le chargement courants. Le nouveau marqueur ne neutralise pas le signalement des erreurs.

## Validation avant retransmission

Le transport de l'hôte ne retransmet plus automatiquement les messages `ILevelingMessage`. Il les remet à l'acteur avec leur véritable expéditeur. L'acteur contrôle le chargement, l'identité complète et le propriétaire avant d'autoriser leur relais. Un message direct est de nouveau contrôlé sur le thread du jeu ; un choix différé est relayé après validation de son entrée d'attente, sans attendre la fermeture d'une autre fenêtre locale.

Un accusé de phase doit nommer son véritable expéditeur, enregistré dans la partie. Les choix et accusés acceptés sont retransmis aux autres pairs une seule fois, sans les renvoyer à l'émetteur et sans recommencer le relais pendant la relecture différée.

Le transport rejette également un expéditeur inconnu avant toute retransmission, y compris pour les messages ordinaires. Les notifications ordinaires d'un expéditeur enregistré conservent leur relais automatique.

## Régressions

`MultiplayerLevelingRelayTests.cs` reprend les quatre sondes de la revue et ajoute la validation, l'annulation, les exceptions après fin et changement de zone, le relais autorisé des choix directs et différés et l'authenticité des accusés de phase. `NetworkHostForwardingTests.cs` couvre le maintien du relais ordinaire, l'absence de relais automatique des progressions et le rejet des expéditeurs inconnus. Les tests utilisent les acteurs et le transport du mod avec des dépendances de jeu simulées.

Aucun déploiement, lancement du jeu, test audio ou essai multijoueur réel n'est effectué dans cette correction. Les tests automatisés ne valident pas le comportement du contrôleur natif dans Unity.

## Résultats

SDK 9.0.318, solution entière compilée en Release, Playground inclus :

| Variante | Principaux | Réseau | Localisation | Total | Échecs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Standard | 1371 | 212 | 33 | 1616 | 0 |
| Prototype | 1371 | 212 | 33 | 1616 | 0 |

Les 18 nouveaux cas sont intégrés aux suites normales. Les quatre reproductions initiales passent désormais dans chaque variante. Les six contrôles du paquet réussissent pour chacune, avec le protocole 92 lu dans la DLL. La localisation est vérifiée à 280/280 clés et paramètres sous PowerShell 7 et Windows PowerShell 5.1 ; `git diff --check` réussit. Les avertissements préexistants AutoMapper NU1903 et ExternalConnectionService CS1998 restent présents.

Les TRX sont conservés sous `local-artifacts/verification-relay-fixes-complete-2026-10-30/{false,true}/`, avec le runner `local-artifacts/verify-relay-fixes-complete-2026-10-30.ps1` et son log. Aucun commit ni marqueur de validation runtime créé.
