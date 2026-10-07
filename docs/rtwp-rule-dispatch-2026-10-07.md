# Autorité des règles : interception des callbacks hors jeu

Variante **0.4.16.7**, protocole **44**, schéma d'empreinte **12**. Cet incrément complète la protection `OnTrigger` du [bilan des projectiles](rtwp-projectile-presentation-2026-10-07.md). Aucun jeu n'est lancé.

## Problème et approches rejetées

`Rulebook.TriggerEventInternal<T>` invoque plusieurs callbacks avant et après `OnTrigger`. Bloquer uniquement cette dernière méthode laisse passer les callbacks antérieurs.

Le diagnostic reproductible a établi trois faits distincts :

1. Avec Harmony 2.3.6 sous .NET Framework 4.8.1, un hook ordinaire fonctionne mais un hook générique échoue dans `MonoMod.Core.Interop.Fx.V48.MethodTable.GetMethodDescForSlot` avec `NotImplementedException`.
2. Le même hook générique s'installe sous le Mono fourni avec le jeu. Cependant, fermer le corps partagé sur `object` remplace aussi son utilisation de `typeof(T)` : le chemin laissé libre renvoie le type de fermeture au lieu du type concret de l'appel. **Ne pas patcher tout `TriggerEventInternal<T>` de cette façon : les handlers typés de l'hôte seraient altérés.**
3. Une propriété non générique utilisée comme garde peut être déjà inlinée dans un appelant générique. Le probe préchauffé contourne alors le hook. Cette approche est également rejetée.

## Point d'interception retenu

Le prototype patche seulement `Rulebook.SaveExecute<T>(T, Action<T>)`. Ce corps exécute un délégué déjà créé par le moteur. Le délégué conserve son type concret, ce qui est vérifié séparément dans le probe ; le corps de dispatch typé reste intact.

- Chez l'hôte et hors client de présentation : exécution normale du délégué.
- Chez la cliente RTwP : blocage de chaque callback des règles mécaniques, aléatoires et inconnues. Une liste explicite de requêtes de calcul destinées à l'interface reste permise. `RuleCalculateDamage`, malgré son nom, n'appartient pas à cette liste.
- Une règle rejetée produit un seul diagnostic `autonomous-client-rule-dispatch:<type>` via le mécanisme existant de pause/réparation.
- Avant suppression, le holder `EventHandler<T>` est préparé sans appeler `EventBus`. Cela permet au `finally` natif de le nettoyer sans exception, même pour la toute première règle rejetée. Le type de holder est tiré du délégué, pas simplement de la classe de la règle.
- Le patch n'est installé que dans la compilation prototype. Les protections `OnTrigger`, ressources et projectiles existantes restent en place.
- Le module moteur et l'IL de `SaveExecute` doivent correspondre exactement à la version examinée. Un changement de jeu invalide cette preuve plutôt que d'appliquer silencieusement un hook générique à un nouveau corps.

Module vérifié : `90a9869c-2792-4c7b-bfb7-5a8b33da7c82`.
SHA-256 de l'IL de `SaveExecute` : `0ADE0D9A2A12100868F3539A47AE0A2A5742A75E0B74B5E469EF416F017123BD`.

## Essai Mono isolé

`tools/check-rule-dispatch.ps1` compile un petit hôte C++ qui charge seulement le runtime Mono et le programme de diagnostic. Il ne démarre ni l'exécutable du jeu ni `UnityPlayer`, n'accède pas à `Game.Instance`, aux sauvegardes ou au projet de voix. Les assemblies installés sont lus, pas modifiés.

Le programme exerce le `Rulebook` installé avec des règles de test sans unité réelle, puis vérifie le callback typé côté hôte. Le diagnostic de multijoueur est remplacé par un objet de test ; cela ne prouve pas une communication réseau ni une pause effective dans Unity.

L'essai final installe la classe Harmony de production, avec ses attributs et sa condition de compilation, et utilise son préfixe réel. Seule sa dépendance `IMultiplayer` est remplacée par un proxy de test ; le corps de protection n'est pas recopié dans le probe.

Reproduction après préparation d'un candidat prototype :

```powershell
.\tools\rtwp-local.ps1 -Action Build
.\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
.\tools\check-rule-dispatch.ps1 -CandidateFingerprint <empreinte_du_candidat>
```

Le dernier outil vérifie l'empreinte complète du candidat, le DLL réellement chargé et consigne les empreintes du jeu, de Mono, d'Harmony et des programmes de diagnostic. La preuve reste strictement hors jeu : `runtimeValidated=false`. Elle est conservée à côté du paquet, dans `offline-rule-dispatch`, jamais comme `RTWP_RUNTIME_VALIDATED.json`.

## Limites toujours ouvertes

- Ce test n'exécute pas de vrais buffs, unités, dégâts ou ressources. Il prouve le point de suppression et la conservation du délégué typé testé, pas un combat complet.
- Les règles peuvent avoir des effets dans leurs constructeurs ou des méthodes appelées sans passer par `Rulebook`. Les contrôleurs, scripts et callbacks directs restent à auditer.
- Les callbacks des requêtes d'interface permises ne sont pas une preuve générale d'absence de mutations directes. Les points d'écriture des effets/entités doivent encore être isolés.
- Les conséquences durables du combat, les reconstructions de buffs/zones/invocations et la reprise d'actions restent incomplètes.
- Aucune réparation jouable, session à deux PC, compatibilité vocale dans le moteur ni promotion finale n'est revendiquée.

## Résultats vérifiés

- **534 tests réussis**, zéro échec/ignoré, avec et sans prototype : 33 traduction, 38 réseau, 463 principaux. Les 16 nouveaux cas vérifient la politique, les callbacks multiples, le diagnostic unique, le nettoyage du holder typé, les rejets malformés et la cible moteur.
- Essai du paquet exact sous le Mono installé : `PresentationPrototype=True`, deux règles clientes rejetées, **zéro `OnTrigger` exécuté**, deux diagnostics, pile de contexte vide ; callback de contrôle hôte exécuté une fois avec son type concret conservé. Sortie du diagnostic : 0.
- Candidat hors jeu : `local-artifacts\a0-candidates\8D6D54567B9103D176DE3EFA211270BA5B24B52D0CCD1A29A1E3E44D1BAF3FB7`.
- Empreinte du paquet vérifiée à nouveau par le programme Mono : `8D6D54567B9103D176DE3EFA211270BA5B24B52D0CCD1A29A1E3E44D1BAF3FB7`.
- SHA-256 du DLL exact testé : `B1C011B0734325B0EAAF0EB18FA98EA9E9866F3C89BAEBAC2555EFBFE4EE2C4C`.
- Preuves conservées : dossier `offline-rule-dispatch` adjacent au paquet, avec journal, manifeste et deux exécutables de diagnostic.
- SHA-256 moteur : `2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953` ; Mono : `7DE766D6172E91BE8D40C07CD854E34657205814E12853726AB768CA19AF7B06` ; Harmony : `DF043F05C6E43602FA8E8F38F52A3A2CF962AAB7A65A9D85573EA25F01A63E80`.

Le candidat n'est ni installé ni activé. Le stable 0.4.16 demeure actif ; le slot expérimental antérieur 0.4.16.3 demeure désactivé. Aucun marqueur de validation en combat n'a été créé. L'avertissement existant NU1903 sur AutoMapper demeure.
