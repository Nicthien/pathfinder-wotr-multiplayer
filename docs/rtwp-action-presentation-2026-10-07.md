# Présentation des actions de sorts — 7 octobre 2026

## Statut

Variante locale **0.4.16.5**, protocole **43**. Suite du [bilan des ressources](rtwp-offline-progress-2026-10-07.md).
Implémentation hors moteur, prototype de présentation toujours désactivé par défaut.
Aucun jeu lancé, aucune modification du mod stable, des sauvegardes ou de la production des voix.
La porte A réelle n'est pas franchie ; cet incrément ne valide pas un combat complet.

## Ce qui change

- Nouveau contrat `AbilityPresentation` dans le flux fiable `NotifyRealTimeAuthoritativeEvent`.
- Paramètres transmis : blueprint du sort, type/style d'animation, comportement de précast, temps de lancement, vitesse, arme, distance, monture et transition de sortie.
- L'hôte observe le handle créé par `UnitUseAbility`, puis émet démarrage, lancement réussi, interruption ou fin.
- Le lancement réussi est nommé `Impact` dans l'énumération commune : ici il signifie **libération du sort**, jamais collision d'un projectile ou application de dégâts.
- La cliente crée seulement un handle d'animation `CastSpell`, renseigne les paramètres et l'exécute dans le gestionnaire d'animation. Elle ne construit pas de `UnitUseAbility`, `AbilityExecutionContext` ou `RuleCastSpell` pour afficher cette action.
- Les phases terminales libèrent le handle uniquement si l'identifiant d'action correspond ; une fin ancienne ne doit pas arrêter une action plus récente.
- Attaques et sorts partagent les séquences, la file ordonnée, les copies immuables et la déduplication existantes.
- Les messages ne sont acceptés que depuis l'hôte et pour la session, le chargement et l'époque courants.
- Les enveloppes inconnues, ambiguës ou sans identité d'action/unité sont rejetées. Les paramètres invalides sont refusés avant accès aux unités ou aux assets.
- Le point d'entrée commun est renommé `TryPresentRealTimeActionAsync`, y compris dans les dummies et les tests.
- Le hook `UnitCommand.StartAnimation`/`OnEnded` reste unique et distribue attaques/sorts ; les tests empêchent sa duplication.

Le blueprint sert à renseigner le handle visuel, pas à lancer le sort. L'implémentation du gestionnaire d'animation doit encore être vérifiée dans le moteur, y compris les éventuels callbacks de ses assets.

## Garde-fou projectile

L'assembly du jeu montre que `Projectile.OnHit()` appelle successivement des handlers, des effets, `Callback` et `OnHitTrigger` via le Rulebook. Cette fonction n'est pas un lecteur d'effets visuels.

Un préfixe du prototype empêche son exécution autonome chez la cliente et signale `autonomous-client-projectile-impact` au mécanisme de pause/récupération. Le chemin stable/tour par tour reste inchangé lorsque le prototype de présentation client n'est pas actif.

**Ce garde-fou ne crée pas les projectiles visuels.** Leur représentation cliente indépendante reste à construire, puis à intégrer à l'état de référence et à la reconstruction.

L'inspection de `ProjectileView` montre un composant de présentation dont `OnEnable` prépare des objets à détruire à l'impact. Il reste nécessaire de vérifier les autres composants des prefabs avant de les utiliser sans le simulateur `Projectile`.

## Limites et preuve suivante

1. Les sorts immédiats et certaines animations rapides qui contournent `UnitCommand.StartAnimation` ne disposent pas encore d'un cycle complet propre dans ce canal.
2. Les animations custom différentes de `CastSpell` sont refusées et déclenchent la protection ; elles ne sont pas remplacées silencieusement par une animation approximative.
3. Les effets de mains, précast, impact, voix de lancement et effets persistants ne sont pas encore reproduits par ce contrat.
4. La reprise d'une animation déjà en cours depuis un checkpoint n'est pas implémentée. Un checkpoint peut couvrir un événement de démarrage ; reconstruire l'action en cours demande un état de présentation complet.
5. Les règles/dégâts/soins ne sont pas produits par ce lecteur, mais leur journal autoritaire durable reste incomplet.
6. `Rulebook.TriggerEventInternal<T>` appelle des callbacks avant `OnTrigger`. Le blocage existant sur `OnTrigger` ne prouve donc toujours pas l'absence de conséquences clientes antérieures. Ne pas déclarer l'isolation achevée.
7. Les hooks et tests de transport ne prouvent pas le rendu en jeu, le timing de pause, le nettoyage des handles lors d'un chargement ou la compatibilité vocale.

Prochain travail substantiel : projectiles purement visuels et reprise depuis état de référence ; suppression complète des callbacks mécaniques clients avant leur dispatch ; reconstruction des buffs/zones/invocations ; récupération réellement jouable. Les essais moteur demeurent différés tant que Nicolas interdit le lancement du jeu.

## Reproduction hors jeu

```powershell
.\tools\rtwp-local.ps1 -Action Build
.\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
```

La seconde commande vérifie le prototype et prépare un paquet hors du jeu. Aucune installation/activation automatique ; `runtimeValidated=false` et aucun `RTWP_RUNTIME_VALIDATED.json`.

Les tests couvrent le transport des paramètres de sorts, le rejet des paramètres invalides, les identités obligatoires, les enveloppes ambiguës, les sessions/chargements/époques, les doublons, l'ordre partagé avec les attaques et l'abandon d'une présentation devenue obsolète. Ils ne remplacent pas la rencontre à deux instances prévue par le plan.

Résultats du candidat préparé : **493 tests réussis**, aucun ignoré (33 traduction, 37 réseau, 423 principaux).
La configuration sans prototype a également été recompilée et vérifiée : les mêmes 493 tests réussissent. Cette dernière commande remplace les sorties `bin`, mais ne modifie pas le paquet prototype préparé.
Empreinte exacte : `371EF07307507CFD9555CE96FB7004B85815D24954A37E5EB49FFA8B8FBED475`.
Paquet conservé dans `local-artifacts\a0-candidates\371EF07307507CFD9555CE96FB7004B85815D24954A37E5EB49FFA8B8FBED475`.
Le slot stable installé est resté en 0.4.16 actif ; l'ancien slot expérimental 0.4.16.3 est resté désactivé.
