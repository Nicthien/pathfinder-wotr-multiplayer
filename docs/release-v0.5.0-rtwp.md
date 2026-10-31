# v0.5.0-rtwp — protocole 92

Préversion expérimentale du 31 octobre 2026.

## Changements

- Identité de progression conservée sur les 39 contrats de choix, avec validation de session, de chargement et de propriétaire avant retransmission.
- Traitement ordonné des démarrages, choix, validations et annulations, même lorsque le thread du jeu est occupé.
- Ouvertures forcées et confirmations locales conservées pendant les relectures distantes ; protection contre les ouvertures répétées et les transferts de propriétaire pendant une progression.
- Sauvegardes protégées pendant les attentes et les échecs de synchronisation ; absence de faux échec au changement de zone après une progression réussie.
- Contrôle des votes et des témoins de dialogue avant l'exécution d'une réponse par l'hôte.
- Corrections du consommateur réseau, des répliques de zones, de l'inspection native, du transfert de checkpoint et des outils d'essai isolés.
- Services du Playground remis en cohérence avec les interfaces courantes ; traductions françaises et anglaises complétées.
- CI alignée sur le SDK 9.0.318 et sur le dossier expérimental `00_WOTRMultiplayerRTwP`.

## Paquets

- `wotr-multiplayer-v0.5.0-rtwp.zip` : compilation Release standard (`HostPresentationPrototype=false`).
- `wotr-multiplayer-v0.5.0-rtwp-a0-prototype.zip` : compilation Release du prototype A0 (`HostPresentationPrototype=true`), réservée aux essais du moteur de présentation.

Choisir un seul paquet et utiliser exactement la même variante chez tous les joueurs. Les deux archives ciblent le dossier expérimental `00_WOTRMultiplayerRTwP`. Ne pas les superposer ; un seul des slots stable et expérimental doit être actif, et le slot inactif doit rester hors du dossier `Mods`. Les archives ne contiennent pas les bibliothèques propriétaires du jeu.

## Validation et limites

Les sources ont passé 1 637 tests C# dans chacune des deux variantes Release : 1 392 principaux, 212 réseau et 33 localisation. Les contrôles de protocole et les 281 clés françaises et anglaises concordent. Les paquets publiés sont recompilés et retestés avec le SDK 9.0.318 et la version d'assembly 0.5.0.0.

Cette publication ne constitue pas une validation en jeu du build exact : ni campagne, ni convergence réelle à deux instances, ni compatibilité audio ne sont certifiées. Essayer uniquement avec des installations et profils isolés, sur des copies de sauvegardes. Ne pas remplacer l'installation stable ni utiliser les sauvegardes originales. Aucun marqueur de validation runtime n'est fourni ou créé. Les contrôles d'installation existants restent en place.

Les avertissements AutoMapper NU1903 et ExternalConnectionService CS1998 préexistants subsistent. Les tests Python de la passerelle ne font pas partie des suites C# exécutées.

La CI GitHub dépend des références privées `fl01/pathfinder-wotr-libs` et du secret `WOTR_LIBS_TOKEN`, non configuré sur ce dépôt au moment de cette publication. Les preuves de compilation et de tests de cette préversion proviennent donc du workflow local, pas d'une CI GitHub réussie.

Voir [les dernières passes de correction](corrections-ordre-progressions-2026-10-30.md) pour les reproductions et les contrôles de régression.
