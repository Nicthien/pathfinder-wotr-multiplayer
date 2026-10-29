# Source mémoire de reconstruction native — 7 octobre 2026

Variante 0.4.16.22 ; protocole réseau 51 inchangé. Le nouveau format interne d'archive `RTWNCP01` n'est pas encore un message réseau négocié.

## Pourquoi ce chemin

Les représentations clientes ne contiennent volontairement pas tous les composants mécaniques des buffs, zones et unités. Réactiver le tour par tour sur ces représentations ne suffit pas ; certaines parties natives conservent également des références aux effets retirés.

La lecture de `SaveManager.LoadRoutine` dans l'assembly 2.7.0x confirme que le moteur accepte un `SaveInfo` associé à une interface `ISaver`. Il lit l'en-tête, clone la source puis utilise le chargeur natif pour reconstruire le monde et ses composants. La source ne doit donc pas obligatoirement être un fichier joueur.

Ce chemin implique toutefois un véritable rechargement : `LoadRoutine` dispose l'ancien monde et efface le stash de travail avant de lire certaines données. Le précontrôle et la barrière réseau sont indispensables. Une archive syntaxiquement valide ne prouve pas que toutes les références et ressources natives pourront être chargées.

## Ce qui est implémenté

`RealTimeMemorySaver` implémente réellement `ISaver` : lecture des documents natifs et octets, liste de fichiers, copie isolée d'une source existante, clonage et destruction indépendants. Les données sont en RAM. `Save()` refuse toujours l'écriture d'une sauvegarde joueur. Une source scellée refuse les modifications.

Les clones scellés partagent uniquement des buffers privés immuables, sans exposer leurs tableaux. Cela évite de dupliquer toute l'archive pour chaque thread du chargeur natif. Les lectures et les appels de copie renvoient leurs propres tableaux ; détruire une source ne corrompt pas ses clones.

L'accès au stash n'est jamais implicite : il faut fournir une capacité de lecture ou d'écriture. L'écriture est impossible avant scellement. Aucun adaptateur filesystem de production n'est encore fourni. Les noms de fichiers sont contrôlés : pas de traversée, chemin absolu, flux NTFS, alias de casse, périphérique Windows ou caractère de contrôle. Les quotas limitent fichiers, taille unitaire et taille totale.

`RealTimeNativeCheckpointCodec` encode une archive interne déterministe, bornée et non compressée. Le décodage possède ses octets avant de vérifier SHA-256, refuse les noms dangereux, les doublons, les tailles forgées, les données tronquées et le contenu final surnuméraire. Le checksum protège l'intégrité ; il ne remplace pas l'authentification de l'hôte ni la liaison à la session et à l'époque.

`RealTimeNativeCheckpointLoadSource.Prepare` précontrôle la syntaxe JSON, les propriétés dupliquées et les documents mécaniques obligatoires, puis vérifie l'identité de campagne avant d'utiliser le véritable décodeur natif d'en-tête. Il prépare un `SaveInfo` avec source mémoire clonée et identifiant volatile, sans appeler `Game.LoadGame` ou `LoadRoutine`. Les documents facultatifs et le `tbm.json` vide émis par une sauvegarde native hors tour par tour sont conservés correctement.

## Ce qui n'est pas encore implémenté

- La capture native cohérente de l'état **courant** de l'hôte en pause, avec préparation mécanique, toutes les scènes/stash, historique, brouillard, données de tour par tour et contrôles avant/après capture. Copier une ancienne sauvegarde ne constitue pas cette capture.
- Le transfert fiable lié à la session, au chargement, à l'époque, au point de reprise et à l'identité du paquet. Le format interne seul n'est pas un nouveau protocole de réparation.
- L'adaptateur de stash de travail et le chargement réel sous une barrière protégée, avec audit des callbacks de reconstruction.
- Le contournement contrôlé des hooks de chargement ordinaires : `GamePatches.Game_LoadGame_Prefix` appelle actuellement `ForceLoadGame`, qui réinitialise la session et cherche un fichier. Il ne faut pas lui passer directement l'identité volatile.
- La vérification du monde reconstruit, des contrôleurs, des empreintes et de la jouabilité avant reprise, repli ou réautorisation des sauvegardes. Les obligations existantes ne sont pas levées par la préparation de cette source.
- L'essai moteur, les invocations/renforts de présentation manquants et la validation de campagne avec `WOTR_AIVO`.

La source mémoire est donc une brique pour réutiliser le chargement natif, pas une reconstruction automatique déjà fonctionnelle. Aucune récupération jouable supplémentaire n'est annoncée.

## Vérification

Les tests couvrent la propriété des buffers, le contrat natif `ISaver`, les clones, l'interdiction d'écrire une sauvegarde, les capacités de stash, les chemins Windows, les quotas forgés, les archives corrompues/tronquées, les documents obligatoires, les erreurs JSON avant décodage natif et les identités de campagne. Un test utilise le véritable décodeur natif de `SaveInfo`, sans monde ni Unity.

Le diagnostic Mono est étendu au parcours archive → source native → véritable en-tête → clones, sur des documents synthétiques. Il n'appelle jamais le chargement du monde.

Les compilations par défaut et prototype passent chacune 861 tests : 33 traduction, 41 réseau et 787 principaux, dont 59 essais de la nouvelle source mémoire. Les 233 clés françaises et leurs paramètres sont vérifiés. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Compilation par défaut : empreinte `304009BF161F7592D00711BC681B495A20F9FF66269DF2942A36537BBA551D49`.

Candidat prototype exact : `8425FF2C8556E52870421082C3EB16FE9AFF7867AB368239B56B965AAABA92C3`. DLL SHA-256 : `D7F820146B9EF570D191E0005807CF9C8B61C967C9AE4C051D8A94575D5EF0E5`.

Diagnostic Mono du candidat réussi : `native-memory-checkpoint: actual-header-decoder=True; game-load-not-invoked=True`, ainsi que les contrôles précédents. Preuves archivées dans `local-artifacts/a0-candidates/8425FF2C8556E52870421082C3EB16FE9AFF7867AB368239B56B965AAABA92C3/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`.

Les documents du diagnostic sont synthétiques, pas une capture de campagne. Aucun `LoadRoutine`, `Game.LoadGame`, `DisposeState`, effacement de stash ou chargement de personnage réel n'est exécuté. Cette preuve ne valide donc pas la reconstruction mécanique complète ni une reprise jouable.

Aucun jeu lancé, aucune installation/activation expérimentale, aucun marqueur de validation moteur. Mod stable, sauvegardes originales et production des voix inchangés.
