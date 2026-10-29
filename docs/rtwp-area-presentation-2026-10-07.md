# Zones de présentation et branchement réseau — 7 octobre 2026

Variante 0.4.16.19 ; protocole 51 et empreinte schéma 16 inchangés.

## Branchement au service de combat

L'application d'un état RTwP peut maintenant préparer une zone absente : résolution de sa scène et de son blueprint, reconstruction de son contexte calculé, fabrique de données détachées, création d'une vue contrôlée, puis enregistrement transactionnel. Les données manquantes ne sont plus refusées uniquement parce que la zone n'existe pas encore. Leur structure, les cibles, occupants et références restent contrôlés ; aucune invocation manquante n'est créée implicitement.

Les représentations de zones supplémentaires créées par cette variante peuvent être retirées. Une zone native ordinaire supplémentaire reste refusée : ses abonnements mécaniques ne sont pas assimilés à ceux d'une représentation inerte.

La préparation des contextes et assets précède les changements de registre. La transaction possède les nouvelles vues dès leur création. Un échec ultérieur les détruit ; les anciennes vues retirées ne sont détruites qu'après validation. L'état réel est relu pour comparer l'empreinte et les temps avant validation. Le succès de la tâche réseau est publié après le nettoyage transactionnel, afin qu'un échec de nettoyage ne soit pas masqué par un résultat déjà annoncé.

## Vues sans constructeur mécanique

`RealTimeAreaVisual` crée une racine inactive, marque sa propriété avant activation, initialise la forme native et lie les données brutes sans `AttachView`, `AttachToData`, activation des faits ou constructeur mécanique de zone. L'initialisation reçoit une cible ponctuelle dérivée de la position confirmée : elle ne lit pas la position simulée d'une unité cliente, même pour une zone attachée.

Les formes cylindre, mur et zone globale sont admises. La croisade est explicitement exclue de ce branchement. Les positions et orientations des vues et FX suivent uniquement les états confirmés de l'hôte.

Six chemins natifs sont protégés pour les vues possédées, même après déconnexion : `OnEnable`, `OnDisable`, `OnDestroy`, `CreateEntityData`, `SpawnFxs` et `EntityViewBase.UpdateViewActive`. Cela évite notamment la découpe de navigation des fosses, l'abonnement automatique et la création mécanique. Le renderer gère ses FX et son nettoyage explicitement. Le marqueur faible de propriété survit aux callbacks de destruction différée Unity.

## Visibilité dans les pools natifs

Le cache d'`EntityService` n'est pas le pool `PersistentState.AreaEffects`. Le pool conserve sa propre liste et son énumérateur filtre `m_IsInGame` et la suppression. La fabrique remet explicitement `m_IsInGame=false`, car le constructeur JSON natif le laisse à `true` même sur une entité détachée.

`RealTimeAreaPoolMembership` gère les listes de pools et ce drapeau sans événement de scène. Le service inscrit les zones dans `AreaEffects`, `Entities` et les pools filtrés dont leurs propriétés natives satisfont le prédicat. Les inscriptions, retraits et remplacements sont annulables en ordre inverse, y compris lorsque plusieurs pools partagent le même drapeau.

La vérification qui bloque sauvegarde et réutilisation mécanique inspecte la liste complète d'un pool natif, pas son énumération filtrée. Une représentation inactive ou supprimée de l'énumération par la suppression native doit toujours bloquer ces opérations : elle existe encore dans le graphe de sauvegarde.

## Limites explicites

- Le rendu, les locators, le système de FX et la destruction différée ne peuvent pas être vérifiés sans Unity en fonctionnement. Leur présence dans le code n'est pas une preuve de rendu correct.
- Les prefabs sont contrôlés avant instanciation. Seul le script visuel `FxDestroyOnStop` est actuellement admis ; scripts non audités, animations pilotées par composants, colliders, rigidbodies et particules avec collision/trigger sont refusés. Il faut auditer et étendre cette couverture pour les FX de campagne ; aucun effet refusé n'est remplacé silencieusement par une zone invisible.
- Un blueprint sans prefab FX conserve sa zone de données et sa forme, comme une zone native sans FX. Cela ne garantit pas qu'un prefab introuvable soit intentionnel : ce cas doit être vérifié dans les essais moteur.
- La suppression native des entités et les pools filtrés doivent être observés lors du premier essai moteur, notamment aux changements de scène et de visibilité.
- Une vue perdue d'une zone encore présente, une zone native supplémentaire, les invocations absentes et la restauration mécanique avant retour au tour par tour restent des chantiers distincts.
- La transaction ne restaure pas arbitrairement toutes les valeurs préexistantes modifiées par un état échoué. L'état reste donc protégé après échec ; aucune reprise jouable complète n'est annoncée.

## Vérifications

Tests ciblés : contrôle préalable des zones reconstructibles, refus des données invalides avant accès au monde, références manquantes, propriété des vues, cibles de patch natives, nettoyage après échec, refus des doubles propriétaires, poursuite du nettoyage après exception, visibilité dans l'énumérateur natif et annulation multi-pools.

Le diagnostic Mono appelle les six méthodes natives protégées sur une vue synthétique sans initialiser Unity. Il vérifie également l'énumérateur natif de pool sur les données synthétiques. Il ne crée aucune vue réelle et ne lance ni `Game.Instance`, ni jeu, ni combat.

Les deux compilations passent 761 tests : 33 traduction, 41 réseau et 687 principaux. Les 233 clés françaises et leurs paramètres sont vérifiés. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Compilation par défaut : empreinte `0A5E1178924300EF6BEC40CCE0E1FE5FAAD5B5B297B59E2429A188840839FFC6`.

Candidat prototype exact : `C50B5CD7C902F7849E862EA218DEB6CA9208A5661FA04A23C39AB4F37DB0A6A7`. DLL SHA-256 : `47C203DB108C135467772472510EAC2F5A986828E8A6BE803CAAD8B8BECE0C01`.

Diagnostic Mono du candidat exact réussi : `visual-lifecycle-without-unity=True`, `native-pool-enumeration=True`, plus les protections et transactions précédentes. Preuves dans `local-artifacts/a0-candidates/C50B5CD7C902F7849E862EA218DEB6CA9208A5661FA04A23C39AB4F37DB0A6A7/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`.

Ces preuves valident les interceptions, données et énumérations synthétiques, pas l'exécution du branchement complet dans un monde Unity. Aucun marqueur de validation moteur ne doit être créé à partir de ces preuves hors jeu.

Aucune installation, sauvegarde originale ni production de voix modifiée. La variante reste désactivée.
