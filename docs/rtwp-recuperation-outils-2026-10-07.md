# 0.4.16.38 — récupération, entités, français et outils locaux

Date : 7 octobre 2026. Protocole : 60. Campagne de base, sans DLC.

## Livraison et preuves

- Compilation Release avec `HostPresentationPrototype=true` réussie, zéro erreur.
- Aucun test unitaire, scénario synthétique ou essai en jeu exécuté pour cet incrément, conformément à la demande de Nicolas.
- Aucune installation/activation, aucun lancement de jeu, aucune modification des sauvegardes, du mod stable ou du doublage.
- Empreinte du candidat exact : `144586FDB27ABDA1848B6F5C80A91A3B4748207A147A4FC9F313997BF92420F8`.
- `runtimeValidated=false`. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` créé.
- Avertissements de compilation existants : AutoMapper 10.1.1 / NU1903 et méthode asynchrone sans `await` / CS1998. Non corrigés dans ce lot.

## 1. Réparation et reconnexion

Le coordinateur appelle désormais le transfert/chargement/restauration natifs lors d'une divergence du prototype ou d'un résultat mécanique non fiable. Un nouveau Commit réseau confirme un monde chargé et vérifié avant l'ancien Resume. Le contrôle relit l'état, la topologie, les objets natifs, les obligations de reconstruction et la fin des tâches de chargement. Il ne lève que l'obligation appartenant au chargement courant et refuse tout autre propriétaire en attente.

Les messages de réparation et leurs accusés de réception portent la session et l'identité du chargement, en plus de l'époque. Un arrêt explicite empêche un ancien Resume de débloquer une réparation échouée. Les délais réseau existants restent bornés ; les annulations attendent les travailleurs natifs au lieu de prétendre les avoir arrêtés.

Une déconnexion suspend le combat côté hôte. Quand un autre joueur redevient synchronisé par le chemin existant, une nouvelle époque de réparation est demandée et une reconstruction native est requise. Ce raccordement **ne prouve pas** que toutes les étapes de retour au lobby/chargement fonctionnent en jeu, ni que les attributions de personnages survivent à tout scénario.

## 2. Actions et entités

La reconstruction utilise le chargeur natif existant pour recréer unités, buffs et zones, puis applique la référence autoritaire sur les objets du nouveau monde. Le Commit refuse les répliques sans mécanique et les incohérences connues. Les commandes natives sont aussi inspectées : propriétaire/exécuteur et cibles doivent référencer les objets du monde courant.

L'hôte conserve les présentations Start actives. Après une réparation ordinaire, seules celles encore identifiées comme actives par les hooks sont renvoyées ; aucun ordre mécanique n'est réexécuté. Le temps restant d'incantation est ajusté et son origine temporelle réinitialisée pour ne pas soustraire deux fois la même durée lors de réparations successives.

Limites : une animation peut recommencer plutôt que reprendre à son image exacte. La capture native exige un point sans projectile ni action atomique ; elle refuse les autres situations et ne force pas un repli non vérifié. L'ensemble des invocations, scripts, références de composants et variantes de campagne n'a pas été démontré.

## 3. Interface française

Huit textes français/anglais ajoutés pour pause de récupération, reconstruction, chargement, vérification, Commit, reconnexion, arrêt et code de diagnostic. La fenêtre multijoueur suit ces changements et indique comment revenir à une copie sûre lorsque la récupération échoue. Aucun dialogue JavaScript natif ajouté.

## 4. Installation et diagnostics

Le paquet contient `ACTIVER-ESSAI.cmd`, `RETOUR-STABLE.cmd`, `COLLECTER-JOURNAUX.cmd` et une notice française. L'installateur refuse les paquets incohérents, une version différente du jeu, le jeu ouvert, les variantes simultanées et l'écrasement d'un stable conservé. Une ancienne version expérimentale doit d'abord revenir au stable avant remplacement.

Un chemin de jeu personnalisé est mémorisé dans le dossier local décompressé. La collecte produit un ZIP privé de journaux disponibles, versions des mods, identité du paquet et erreurs de copie. Elle ne copie ni sauvegardes ni voix et n'envoie rien sur le réseau. Les noms et IP éventuellement présents dans les journaux ne sont pas anonymisés.

## Ce qui n'est pas acquis

Ce lot n'est ni une validation de combat à deux instances, ni une validation WOTR_AIVO, ni une certification de sauvegarde/rechargement ou de repli jouable. Le prochain essai moteur doit utiliser exactement le même paquet sur les deux PC et vérifier ces chemins. Aucune stabilité « sans bug » n'est revendiquée.
