# Corrections du démarrage de combat A0 — 31 octobre 2026

## Portée

Ces corrections répondent aux incidents A0-01 à A0-04 du [rapport à deux instances](rapport-test-a0-v0.5.0-2026-10-31.md). Elles modifient le code et ajoutent des tests d'acteurs avec services de jeu simulés. Elles ne constituent pas une nouvelle validation en jeu du scénario « Rencontre de rue ».

Le rapport et les journaux historiques sont conservés. Pendant cette phase de correction du code, aucun déploiement, lancement du jeu, changement de sauvegarde ou marqueur de validation runtime n'a été réalisé.

Un [réessai moteur ultérieur sur deux instances locales](rapport-retest-a0-corrections-2026-10-31.md) vérifie ensuite les tours TB, le RTwP et sa fermeture dans les scénarios décrits, et relève un défaut distinct de chargement ordinaire depuis RTwP actif. Il ne valide pas une campagne ni deux PC physiques.

## Diagnostic et changements

### A0-01 : conservation de la rencontre pendant le démarrage du premier tour

Les journaux confirment une sortie native de la cliente après `Running`, avant les demandes de récupération. La pile native disponible passe par `UnitCombatLeaveController.TickGroups` et `TickGroup`. La lecture de l'IL installé confirme que ce contrôleur peut faire sortir les groupes selon les ennemis connus du contrôleur tour par tour. La valeur exacte de son cache au premier incident n'est pas enregistrée ; elle reste à vérifier dans le moteur.

Un préfixe suspend ce contrôleur de sortie tant que le protocole initialise la rencontre ou attend l'initialisation du tour. Un callback répété d'entrée ne remplace plus l'identité réseau dans cette attente. Cela conserve le contexte requis par la récupération existante, sans assouplir ses contrôles de session, sauvegarde, seed et phase. Une sortie native imprévue pendant l'initialisation provoque un arrêt protégé.

### A0-02 : initialisation RTwP et arrêt protégé

Le protocole de démarrage progresse maintenant depuis la mise à jour réseau/menu, sur les deux acteurs. L'hôte peut notamment traiter `Finalizing` sans dépendre d'un tick du contrôleur tour par tour. Les contrôles de disponibilité native existants restent requis.

Lors d'une nouvelle rencontre, le mode réseau repart de `TurnBased` comme état préalable au handshake ; ce changement de champ ne bascule pas le réglage natif. Si RTwP est préféré, un verrou local `RealTimeBootstrapPending` conserve la pause et la protection jusqu'au mode RTwP stable avec époque non nulle. L'hôte utilise ainsi la transition autoritaire existante même si le moteur était déjà réglé en temps réel hors combat. L'application différée d'une préférence hors combat ne peut plus modifier le mode pendant une initialisation.

Pendant le démarrage, les ticks de commandes, mouvements et buffs, les choix d'IA et les règles mutantes interceptées sont refusés. Dans le prototype, le filtre de dispatch couvre aussi les callbacks de ces règles. La préparation d'initiative reste possible. La sauvegarde et l'indicateur de barrière couvrent désormais toutes les phases de démarrage.

Après 30 secondes, un démarrage incomplet s'arrête en pause, avec diagnostic `combat-startup-failed` et sauvegarde interdite. Un combat natif détecté sans rencontre réseau provoque la même protection. La disparition de l'objet de combat ne réinitialise pas cette protection ; une nouvelle session ou sauvegarde est nécessaire. Ce résultat est un **arrêt protégé demandant un rechargement**, pas une réparation réussie ni un repli tour par tour certifié.

### A0-03 : attente de redémarrage bornée et contrôlée

L'hôte vérifie le résultat des attentes, la session, la sauvegarde et la présence du combat avant tout accès après redémarrage. Un verrou protège aussi l'intervalle où le combat réseau est nul. Une attente expirée ou une exception produit un arrêt protégé ; elle n'accède plus à `Game.Combat.PlayersInCombat` quand le combat manque. Les fins natives attendues de cette opération ne sont pas interprétées comme une nouvelle fin autoritaire.

Les confirmations d'initialisation et d'exclusion clientes ne sont plus envoyées après remplacement du contexte attendu. Les retours asynchrones de réconciliation hôte ne modifient plus un autre objet de combat. Les confirmations hôte tardives sont filtrées selon leur phase.

### A0-04 : pause de sécurité

Les demandes de pause pendant le démarrage passent par une pause locale de sécurité, indépendamment des conditions du vote RTwP normal. Une reprise manuelle ou `ForceUnpause` ne lève pas cette protection. La demande de reprise reste refusée tant que l'état n'est pas sûr. En cas d'échec, la notification existante demande de quitter la session et recharger une copie sûre.

Le retour du chemin clavier interdit une seconde bascule native après l'application directe de la pause : retourner une permission de basculer à cet endroit aurait immédiatement dépausé le jeu.

## Vérifications et limites

Le SDK utilisé est le SDK local `9.0.318`, conformément à `global.json`. Les commandes sont exécutées en Release avec le chemin courant du jeu explicitement passé au script. Les résultats complets sont conservés sous `local-artifacts/verification-corrections/`.

Les tests ajoutés couvrent les cinq phases de démarrage, la conservation du combat, la persistance du blocage après perte de l'objet, le délai maximal, la protection du premier tour, l'attente d'une époque stable, la progression de `Finalizing` depuis la boucle réseau, la pause de sécurité et un redémarrage hôte qui ne revient jamais. Ce dernier scénario attend réellement l'expiration de 20 secondes du mécanisme historique et vérifie la protection avant et après celle-ci.

La compilation Release du prototype A0 final réussit : **1 404 tests principaux, 212 tests réseau et 33 tests de localisation**, soit **1 649 tests**, dont 12 nouvelles régressions. Les 281 clés françaises et leurs paramètres concordent. Les six contrôles du protocole binaire réussissent, avec le protocole `92`. Journal : `local-artifacts/verification-corrections/a0-startup-final-verified-prototype.log`. Empreinte du build prototype testé : `41B77DAB21B01B6BE5A5F426412C6A52280B345DCE9D05329F9262718E160A43`.

La variante sans prototype passe **1 648 tests** avant le dernier ajustement clavier (`a0-startup-verified-standard.log`). Après la dernière correction du retour clavier, ses **12 tests de régression de démarrage**, dont celui du double basculement, passent sur une recompilation Release (`a0-startup-final-standard-regressions.log`).

Le diagnostic Mono de `tools/check-rule-dispatch.ps1` a été tenté mais **n'a pas pu démarrer** : le compilateur C++ requis est absent (`vswhere` introuvable). Son journal est `local-artifacts/verification-corrections/a0-startup-dispatch.log`. Aucun résultat historique de ce diagnostic n'est utilisé comme preuve pour le nouveau build. L'avertissement NuGet existant sur AutoMapper 10.1.1 demeure ; la dépendance n'est pas modifiée dans ce correctif.

Une nouvelle vérification à deux instances reste nécessaire : premier tour TB, nouvelle rencontre directement en RTwP, positions et PV, déplacement/attaque, pause/reprise, et comportement après expiration. Vérifier aussi les combats déclenchés par une action ou un projectile déjà actif : le gel et les points sûrs peuvent conduire à un arrêt protégé si l'action ne peut plus se terminer. Aucun contournement du drainage ou des accusés n'est introduit pour masquer ce cas. La convergence des règles complexes, des zones et des effets scriptés n'est pas démontrée par les services simulés.

Le protocole sérialisé reste `92` ; les deux pairs doivent utiliser exactement le même nouveau paquet, conformément au contrôle de compatibilité par empreinte du code installé. Aucun paquet de publication A0 existant n'est déclaré corrigé rétroactivement.
