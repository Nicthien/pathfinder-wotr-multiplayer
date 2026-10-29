# Projectiles autoritaires — bilan hors jeu du 7 octobre 2026

Variante locale **0.4.16.6**, protocole **44**, schéma d'empreinte **12**. Suite du [bilan des animations](rtwp-action-presentation-2026-10-07.md).

Suite de cet incrément : le [bilan des callbacks sous Mono](rtwp-rule-dispatch-2026-10-07.md) documente la variante 0.4.16.7 et le nouveau point d'interception. L'échec Harmony décrit plus bas est un résultat historique sous .NET Framework, pas une impossibilité générale sous le Mono du jeu.

## Ce qui est implémenté

- L'hôte attribue une identité stable à chaque projectile vivant et publie son apparition, ses positions confirmées (au maximum dix mises à jour par seconde par projectile) et son impact visuel. Le préfixe d'impact capture également les projectiles courts avant la disparition de leur vue.
- Les événements utilisent le même flux séquencé que les animations : session, chargement, époque, séquence et déduplication. Un événement de mise à jour permet de recréer une représentation absente.
- La cliente instancie seulement le prefab visuel vérifié. Elle ne crée aucun objet logique `Projectile`, aucune trajectoire, aucun callback d'impact et aucun dégât. Les positions/orientations reçues sont interpolées visuellement sur 100 ms, sans extrapolation de gameplay.
- L'état de référence comprend les projectiles et leur empreinte canonique. La restauration prépare les nouvelles vues avant de modifier les anciennes, annule les créations partielles si la préparation/confirmation échoue et conserve les causes des échecs de rollback/nettoyage.
- Les données confirmées sont copiées et séparées des transforms rendus. Une transition en pause conserve le checkpoint des projectiles ; la sortie du client de présentation nettoie ses représentations.
- Un événement ne peut ni changer le blueprint d'une identité ni annuler un impact. Un état de référence peut reconstruire une vue antérieure à l'impact.

## Sécurité des prefabs et limites

Avant `Instantiate`, seules les classes de scripts `ProjectileView` et `FxDestroyOnStop` sont autorisées. Les scripts inconnus/manquants, animations à callbacks, colliders, rigidbodies et particules avec collision/trigger sont refusés. Cette liste est une protection de prototype, **pas une preuve que tous les prefabs de la campagne sont supportés**. Un refus doit conduire à la protection/au diagnostic, pas à une simulation locale de remplacement.

Le rendu des impacts secondaires, déviations, flèches plantées, effets sonores, particules spécifiques d'impact et liens avec les actions reste incomplet. La suppression est actuellement confirmée par l'état de référence, sans événement durable de fin dédié. La pause des particules Unity et le nettoyage lors des chargements restent à vérifier dans le moteur.

Le rollback décrit ici concerne les représentations de projectiles. Il ne démontre **pas** une transaction atomique de restauration du monde entier. Les buffs, zones et invocations ne sont pas reconstruits par ce composant.

## Essai du blocage des règles avant leurs callbacks

L'inspection de l'assembly installé confirme l'ordre suivant dans `Rulebook.TriggerEventInternal<T>` : `AssignSource`, `BeforeEvent`, `OnEventAboutToTrigger`, `OnTrigger`, `AfterEvent`, `OnEventDidTrigger` et `OnDidTrigger`.

Un essai hors jeu de patch Harmony sur la fermeture générique `TriggerEventInternal<RulebookEvent>` a échoué avec `System.NotImplementedException` dans `HarmonyLib.PatchFunctions.UpdateWrapper` lors de `PatchProcessor.Patch`. L'essai utilisait l'assembly du jeu et le `0Harmony.dll` installé, sous le runner .NET Framework 4.8.1. Aucun jeu n'a été lancé. Le nettoyage de l'essai a d'abord masqué la cause ; un second essai sans désinstallation d'un patch non installé a confirmé l'échec à l'installation.

**Le nouveau hook a été retiré, pas livré comme fonctionnel.** Ce résultat ne prouve pas que Mono/Unity échouerait de la même manière. Il interdit en revanche de prétendre que l'interception générique est vérifiée hors jeu. La protection existante sur `OnTrigger` reste insuffisante pour tous les callbacks antérieurs ; cette lacune doit être résolue avant la porte A.

## Vérification et livraison

Les tests ajoutés couvrent sérialisation réseau, identités/enveloppes, filtre hôte/session/chargement/époque, déduplication, absence d'exécution de commande cliente, restauration depuis référence suivie d'événements retardés, état/hash indépendant de l'ordre, données malformées, impact irréversible par événement, vues perdues et échecs injectés de création/confirmation/nettoyage.

Les résultats exacts et l'empreinte du candidat sont consignés ci-dessous après les deux compilations. La validation moteur demeure absente : aucun combat à deux instances, aucune compatibilité vocale en jeu ni repli jouable ne sont revendiqués. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` ne doit être créé.

Commandes de reproduction hors jeu :

```powershell
.\tools\rtwp-local.ps1 -Action Build
.\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
```

Ces commandes préparent seulement les sorties locales. Ne pas installer ni activer le candidat pendant la génération des voix.

## Résultats du candidat exact

- **518 tests réussis, zéro échec, zéro ignoré**, dans la configuration sans prototype puis dans la configuration prototype : 33 traduction, 38 réseau, 447 principaux.
- Catalogue français : 232/232 clés, paramètres de format concordants.
- Candidat prototype : `local-artifacts\a0-candidates\A2CF73373D425FFAC9BD60BC15939DF3C2422DD48B89A90AE229CF20A1648029`.
- Empreinte exacte du paquet : `A2CF73373D425FFAC9BD60BC15939DF3C2422DD48B89A90AE229CF20A1648029`. Le contrôle de parité C#/PowerShell est passé pendant sa préparation.
- Manifeste : protocole 44, `hostPresentationPrototype=true`, `runtimeValidated=false`.
- Le mod stable installé demeure 0.4.16 actif ; le slot expérimental antérieur 0.4.16.3 demeure désactivé. Aucun jeu, générateur de voix ni outil de production vocale lancé.

L'avertissement NU1903 existant sur AutoMapper 10.1.1 demeure ; la dépendance n'a pas été mise à niveau dans cet incrément.

Prochaine priorité : obtenir une interception de règles avant les callbacks démontrable et compatible avec le moteur, puis compléter les résultats mécaniques durables et la reconstruction des effets/entités. Le candidat présent n'est pas encore un combat ordinaire complet ni la version finale demandée.
