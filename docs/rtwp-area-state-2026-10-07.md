# Données autoritaires des zones — 7 octobre 2026

Variante 0.4.16.16, protocole 51, schéma d'empreinte 16.

## Travail réalisé

Le contrat de zone transporte désormais son orientation (champ protobuf 17) et sa chaîne de contextes calculés (champ 18). Cette chaîne réutilise le contrat et les vérifications déjà employés pour les buffs : références, paramètres, rangs, valeurs partagées, propriétés, descripteurs et cible. Il ne s'agit pas de rejouer le sort.

Le contrôle préalable vérifie les nouvelles données. Avant de modifier les buffs ou les unités, le service vérifie également la compatibilité de chaque contexte natif existant avec la chaîne reçue. Les cibles ou topologies incompatibles restent refusées : elles nécessitent une reconstruction, pas une application partielle forcée.

`RealTimeAreaReplication` applique ensuite les données brutes :

- résolution de tous les occupants avant la première écriture ;
- valeurs calculées du contexte sans `Recalculate` ;
- position et orientation confirmées ;
- date de création et durée, y compris le passage à une durée illimitée ;
- liste d'occupants de l'hôte sans callbacks d'entrée/sortie.

Le service met séparément à jour le transform de la vue. Il ne déclenche ni `UpdateViewAndUnits`, ni `Tick`, ni `HandleEnd`. L'empreinte canonique inclut maintenant orientation et contexte ; les diagnostics distinguent `orientation` et `calculated-context`.

La capture de contexte est activée pour toute la compilation prototype, comme pour les buffs, afin de conserver la même structure lors des barrières de changement de mode. Les deux joueurs doivent employer le même nouveau paquet.

## Preuves et limites

Treize nouveaux tests couvrent les données natives sans monde ni vue, les durées, les occupants non résolus ou dupliqués, les valeurs non finies, le contexte incompatible avant mutation, le transport protobuf et les divergences d'empreinte/diagnostic. Le diagnostic Mono est étendu à l'application des champs natifs, dont les champs readonly de temps.

Compilations prototype et par défaut réussies : 692 tests chacune (33 traduction, 41 réseau, 618 principaux), 233 clés françaises vérifiées. Candidat prototype exact : `8097E6C0FE302B7C1F88E35423B659CE325EDD97307A679F477E990693D62BD3`. DLL SHA-256 : `0E7BAA7CA7C69220162124F13CE10A23D101F65CAB1936A1D8BFC77AA2CEA5E8`. Build par défaut : `3EFBC2DAD86CE34862F87D234FA1CFF4932F10AB733113334EEFE39740CF88EA`.

Le diagnostic Mono du candidat exact réussit, notamment `raw-state-without-world=True`. Preuves sous `local-artifacts/a0-candidates/8097E6C0FE302B7C1F88E35423B659CE325EDD97307A679F477E990693D62BD3/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false` et `runtimeValidated=false`.

Aucun lancement du jeu, aucune installation/activation, aucune écriture dans les sauvegardes ou le projet de doublage. L'avertissement AutoMapper `NU1903` préexistant reste ouvert. Le premier essai de compilation a échoué parce que les tests accèdent à l'assembly natif non publicisé ; les inspections de champs ont été corrigées par réflexion, puis les deux compilations et leurs tests ont été réexécutés avec succès.

Inspection préparatoire pour la suite : le setter natif `EntityDataBase.UniqueId` écrit seulement son champ ; en revanche, `AttachView` appelle `AttachToData`, `OnViewDidAttach`, puis les notifications des gestionnaires `Parts` et `Facts`. Le constructeur JSON n'initialise pas ces deux gestionnaires. Une future fabrique ne doit donc pas simplement appeler `AttachView` sur une entité JSON nue. Le chemin `AreaEffectView.CreateEntityData` utilise le constructeur mécanique ordinaire et ne constitue pas une alternative sûre pour une représentation cliente.

Ce travail est une étape de la reconstruction des zones, **pas son achèvement** : création des entités et vues absentes, suppression des zones supplémentaires, représentation des formes/FX, enregistrement transactionnel et reconstruction mécanique pour repli restent à développer. Les contrôles préalables continuent de refuser ces cas. Les tests n'observent pas le rendu Unity et ne constituent pas une validation en combat.
