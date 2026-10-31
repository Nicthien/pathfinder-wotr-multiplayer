# Ordre et chevauchement des progressions — 30 octobre 2026

Suite aux [corrections de fin et de relais](corrections-fin-progression-relais-2026-10-30.md). Le format des messages reste celui du protocole 92.

## Réception dans le même ordre que l'application du démarrage

Le démarrage et les messages de progression traversent maintenant la même file du thread du jeu avant la recherche de leur identité. Une fin ou un choix reçu juste après un démarrage ne disparaît plus parce que celui-ci attend encore son dispatch. La réception valide toujours la session, le chargement, l'identité complète et l'autorité de l'expéditeur ; les contrôles du dernier dispatch et du service natif sont conservés.

La file capture aussi une génération locale de progression, invalidée à chaque remise à zéro de zone. Un ancien démarrage ne peut pas être réintroduit après cette remise à zéro, même si la référence de session et la graine de sauvegarde sont identiques. Une identité inconnue reçue avant son démarrage reste refusée : le correctif ne rend pas les messages inconnus acceptables.

## Confirmation locale pendant une relecture distante

Une demande locale peut avoir quitté la cliente avant le début d'une relecture distante, avec sa confirmation encore en transit. Cette confirmation ne remplace plus le contrôleur distant en cours. Elle attend dans une entrée associée à son identité, conserve les messages suivants et ouvre l'interface locale après la fin de la relecture. L'ouverture locale attendue est prioritaire sur les autres relectures différées.

L'entrée reste présente, avec sauvegarde bloquée, jusqu'à la création de la progression locale. Un démarrage déjà programmé n'est pas programmé une seconde fois ; une confirmation répétée pendant le nettoyage ne laisse pas une entrée susceptible de rouvrir ensuite une progression terminée. Les ouvertures forcées restent en attente de leur callback natif. Les erreurs d'application conservent l'arrêt protégé et ne deviennent pas des fins réussies.

## Propriétaire stable pendant une progression

Le propriétaire est publié dans l'identité réseau. Transférer le personnage avant la fin de sa progression retirait ses droits au joueur en train de la valider, alors que l'hôte attendait toujours les choix de ce propriétaire initial. L'hôte refuse désormais ce transfert tant que la progression du personnage, ou de son familier, est active ou attend sa synchronisation. Le transfert redevient possible après la fin appliquée. Une erreur de synchronisation garde son arrêt protégé.

Le menu réaffiche le propriétaire effectivement accepté après une sélection. Un refus n'affiche donc pas un transfert qui n'a pas eu lieu. L'explication du refus est disponible en français et en anglais, sans nouveau dialogue natif.

## Passes de vérification

Les six sondes de la dernière revue sont intégrées dans `MultiplayerLevelingOrderingTests.cs`. Les contrôles supplémentaires portent sur les rafales de démarrage/choix/validation ou annulation, le rejet après changement de session, de chargement ou de zone, la conservation du refus des identités inconnues et une rafale de 300 choix traitée avec le vrai `MainThreadAccessor` et sa limite de 256 actions par passage.

La passe suivante a reproduit puis corrigé la confirmation locale pendant une relecture distante. Elle teste également la confirmation répétée pendant le nettoyage, avec vérification qu'aucune interface ne se rouvre après la fin. `MultiplayerLevelingOwnershipTests.cs` couvre les transferts pendant une progression locale, distante et de familier, puis leur autorisation après la synchronisation. Au total : 21 nouveaux cas dans les suites normales.

Les preuves des échecs avant correction sont conservées sous `local-artifacts/review-final-dispatch-2026-10-30/` et `local-artifacts/verification-ordering-fixes-2026-10-30/{overlap-before,ownership-before}/`. Les contrôles intermédiaires réussis et l'ancienne passe complète à 1634 tests sont conservés séparément ; ils ne remplacent pas les résultats sur les sources finales.

Aucun déploiement, lancement du jeu, test audio, changement de sauvegarde ou marqueur de validation runtime dans ce travail. Les contrôleurs natifs sont simulés dans les tests d'acteurs ; la validation réelle sur deux instances et sur le build exact reste à effectuer. Les tests Python de la passerelle ne sont pas inclus dans les suites C# et n'ont pas été exécutés dans ces passes.

## Résultats sur les sources finales

SDK 9.0.318, solution entière compilée en Release, Playground inclus :

| Variante | Principaux | Réseau | Localisation | Total | Échecs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Standard | 1392 | 212 | 33 | 1637 | 0 |
| Prototype | 1392 | 212 | 33 | 1637 | 0 |

Les six contrôles du paquet réussissent pour chaque variante, avec le protocole 92 lu dans la DLL. Les 281 clés et paramètres de localisation concordent sous PowerShell 7 et Windows PowerShell 5.1. L'analyse syntaxique des scripts PowerShell de premier niveau sous `tools/` et `git diff --check` réussissent. Les avertissements préexistants AutoMapper NU1903 et ExternalConnectionService CS1998 subsistent.

TRX : `local-artifacts/verification-final-corrections-2026-10-30/{false,true}/`. Runner et sortie : `local-artifacts/verify-final-corrections-2026-10-30.ps1` et `local-artifacts/verification-final-corrections-2026-10-30.log`. Les reproductions antérieures restent conservées et leurs équivalents sont désormais couverts par les suites normales.

Bilan des passes : réception différée, chevauchement avec une relecture, confirmations répétées, changement de propriétaire et conservation des gardes revus après correction. Aucun défaut confirmé non corrigé restant dans les changements examinés à la fin de ces passes. Ce bilan concerne la revue de code et les tests automatisés ; il ne certifie pas une campagne multijoueur dans Unity. Aucun commit créé.
