# Audit du chargement natif : échecs interceptés et contrôleurs reconstruits

7 octobre 2026 — variante 0.4.16.29, protocole 55, schéma d'empreinte 17. Campagne de base sans DLC.

## Résultat de l'audit

L'inspection des métadonnées du jeu installé confirme que `SaveManager.LoadRoutine` appelle `EntityDataBase.PostLoad`, puis `TurnOn`, après la désérialisation. Il ne s'agit pas d'une méthode `Player.PostLoad` déclarée sur `Player`.

Plusieurs wrappers interceptent les exceptions des callbacks, les journalisent et poursuivent. `EntityDataBase.PostLoad` positionne même son indicateur avant certains callbacks ; `EntityFact.PostLoad` et `EntityPart.PostLoad` le positionnent en sortie malgré un échec. Le seul indicateur `IsPostLoadExecuted` ne prouve donc pas une restauration réussie.

`UnitEntityData.OnPostLoad` appelle notamment `UnitDescriptor.PostLoad`, puis crée de nouveaux `UnitCombatState` et `UnitCommands`. `UnitDescriptor.PostLoad` rétablit caractéristiques, ressources, corps, progression, livres de sorts et cerveau. `CharacterStats.PostLoad` appelle les hooks de caractéristiques, puis `UpdateValue` dans une boucle qui peut elle-même intercepter les exceptions. Ces appels ne doivent pas être assimilés à la simple hydratation JSON ni autoriser des règles clientes.

L'inspecteur IL affiche maintenant les types propriétaires des méthodes et champs, et accepte les types imbriqués sous leur nom complet. Les références génériques non décodées sont explicitement marquées `<TypeSpecification>` ; leur propriétaire n'est pas deviné. Cela évite d'attribuer un callback au mauvais type d'après son seul nom.

## Correction implémentée

`RealTimeNativeLifecycleFailurePatches` observe les exceptions aux points de journalisation des wrappers suivants :

- `EntityDataBase.PostLoad` et `TurnOn` ;
- `EntityPart.PostLoad` et `TurnOn` ;
- `EntityFact.PostLoad` et `Activate` ;
- `EntityFactComponent.PostLoad` et `Activate` ;
- `CharacterStats.PostLoad` et `ModifiableValue.PostLoad`.

L'instrumentation conserve le logger, l'exception, le message, les arguments et l'appel de journalisation natif. Elle n'exécute aucun callback supplémentaire, ne réactive aucune règle et ne modifie pas le contrôle de flux natif. Pendant un chargement possédé, elle rend la première erreur persistante dans son bail : type/méthode d'origine et type d'exception figurent dans la cause. Le bail ne peut plus produire son reçu de succès, ni poursuivre ses écritures/hydratations autorisées. La pause et l'obligation mécanique restent maintenues pendant le drainage du travail natif.

Sans bail de chargement, aucune propriété globale n'est créée et le comportement de journalisation reste celui du jeu. L'instrumentation n'est installée que dans le prototype activé à la compilation. Une méthode attendue ou un site de journalisation disparu provoque une erreur explicite d'installation, pas une couverture silencieusement perdue.

**Cette couverture n'est pas exhaustive** : erreurs sans exception, erreurs interceptées dans d'autres wrappers, liens incorrects et effets mécaniques silencieux nécessitent toujours un audit structurel et fonctionnel. Aucune obligation de reconstruction n'est levée par cette correction.

## Preuves hors jeu

- **1 037 tests réussis dans chacune des deux compilations** : 33 traduction, 89 réseau, 915 principaux ; 233 clés françaises vérifiées.
- Treize nouveaux cas : dix inspections des corps natifs garantissant que leurs appels de log sont conservés et instrumentés, deux cas de propriété/erreur persistante, un vrai patch Harmony exécuté uniquement sur un wrapper synthétique. Aucun `PostLoad` natif n'est invoqué par ces tests.
- Diagnostic Mono du candidat exact réussi : dix wrappers natifs patchés sans les invoquer ; exception interceptée par un wrapper synthétique observée ; log d'origine conservé ; reçu de succès refusé. Les protections hors jeu précédentes restent vérifiées.
- Sept contrôles du protocole 55 réussis sur le candidat exact.
- Premiers essais de compilation corrigés : référence explicite de l'assembly de logging dans le projet de tests et utilisation de l'API de désinstallation disponible dans la version Harmony du jeu. Les validations complètes sont postérieures à ces corrections.

Paquets et empreintes :

- Build par défaut : `E14354206FB82A54596911EEC9B3C2CDD1A94F7C49E08ED70063613AEC1A84C7`.
- Candidat prototype : `49370B1F0F0EB0A5A27104ECB08A9FCA4BDD0E8910DDCAB4A987290468DF7927`.
- DLL principale SHA-256 : `05D5D1E26635341020DA9613657FE32A412F472E0E10D1451BF8CE37BBCF6787`.
- Assembly du jeu inspecté SHA-256 : `2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 49370B1F0F0EB0A5A27104ECB08A9FCA4BDD0E8910DDCAB4A987290468DF7927
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 49370B1F0F0EB0A5A27104ECB08A9FCA4BDD0E8910DDCAB4A987290468DF7927
& .\tools\inspect-game-method.ps1 -TypeName 'Kingmaker.EntitySystem.Persistence.SaveManager+<LoadRoutine>d__63' -MethodName MoveNext
& .\tools\inspect-game-method.ps1 -TypeName Kingmaker.EntitySystem.Entities.UnitEntityData -MethodName OnPostLoad
& .\tools\inspect-game-method.ps1 -TypeName Kingmaker.EntitySystem.Stats.CharacterStats -MethodName PostLoad
```

## Suite avant reprise

1. Vérifier les propriétaires, composants et références des objets reconstruits ; compléter l'audit des callbacks et des calculs interdits pendant l'activation.
2. Restaurer explicitement état de combat, commandes/journal et séquences : le chargement recrée certains de ces contrôleurs au lieu de conserver leur identité.
3. Terminer la barrière de validation/Commit, puis raccorder réparation automatique, reconnexion et repli jouable ou sortie sûre.
4. Vérifier campagne, voix et sauvegarde/rechargement dans deux véritables instances lorsque leur lancement sera autorisé.

Le candidat est préparé hors installation, `runtimeValidated=false`. Aucun jeu ou monde Unity lancé, aucune activation, aucune sauvegarde originale ni production de voix modifiée. Stable 0.4.16 et ancien slot expérimental désactivé préservés. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper `NU1903` préexistant non résolu.
