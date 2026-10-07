# Chargeur natif client protégé — 7 octobre 2026

Variante 0.4.16.25 ; protocole 52 inchangé ; campagne de base sans DLC.

## État réel

Un point d'entrée interne de chargement du checkpoint reçu est ajouté, avec propriété exclusive du chargement, capacité limitée d'écriture du stash de travail, isolation des notifications réseau et lecture des champs sérialisés sans autoriser leurs recalculs. **Le chemin de chargement d'un monde réel est compilé, mais n'a jamais été appelé.** Aucun jeu lancé pendant la génération des voix.

`MultiplayerClient.LoadTransferredNativeCheckpointAsync` consomme uniquement la source scellée du manifeste exact encore valide. Il reste réservé à la future barrière native : ni la réception `Received` ni la réparation actuelle ne l'appellent automatiquement. Aucun nouveau message de chargement ou Commit n'est introduit dans cet incrément.

Le résultat `RealTimeNativeWorldLoadResult` signifie seulement que le chargement natif a terminé dans le contexte attendu. Il contient le monde et son obligation de réparation, **pas une empreinte commune validée, une autorisation de reprise ou une permission de sauvegarder**.

## Chemin implémenté

### Admission et fichiers de travail

- Prototype et cliente actifs, barrière possédée par l'appelant et source exacte encore présente. Session, chargement, époque, phase, UUID, manifeste et contexte de réception sont revérifiés.
- Travail sur le thread principal, pause effective, même campagne et aire courante, en-tête manuel sans campagne DLC, absence de sauvegarde/commit/chargement concurrents. Tous les précontrôles JSON de la source native sont exécutés avant de programmer le remplacement du monde.
- Validation du stash natif comme **le dossier `Areas_<pid>` de ce processus**, directement sous le dossier persistant du jeu. Refus des liens/reparse points dans les chemins et fichiers existants, et des sous-dossiers qui rendraient le nettoyage natif récursif imprévu. Aucun chemin de sauvegarde joueur n'est une destination autorisée.
- Une source mémoire scellée reçoit une capacité d'écriture attachée au propriétaire du chargement. Ses clones natifs conservent cette capacité ; la source originale et une source étrangère ne l'obtiennent pas. La capacité ne peut écrire que les fichiers déjà présents dans le checkpoint. Le fichier natif `lock` est interdit.
- Chaque écriture revérifie l'autorisation, l'identité du stash et la destination immédiate du fichier. L'accessor natif conserve sa gestion du fichier d'historique. Les appels `ISaver.Save` restent interdits : aucun slot, ZIP de sauvegarde ou commit cloud n'est créé.

### Préservation de la session et des responsabilités

- Propriétaire de chargement exclusif ; un second chargement ne remplace pas le premier. Seul le premier `LoadArea` portant le `SaveInfo` exact est admis. Les chargements utilisateur et entrées concurrentes sont refusés pendant cette propriété.
- Appel direct du `LoadArea` natif avec `AutoSaveMode.None`, évitant `ForceLoadGame`/`TransferSaveGame`, qui réinitialiseraient la connexion et changeraient la graine de chargement. L'appel natif à `SoundState.ResetBeforeUnloading`, identifié dans les métadonnées de `Game.LoadGame`, est conservé avant cet appel. Cela ne constitue pas une vérification audio de `WOTR_AIVO`.
- Nettoyage préalable des animations, interpolations et projectiles de présentation, avant la destruction des objets de l'ancien monde. Le tick de présentation client est suspendu pendant le chargement possédé.
- Notifications du mod de fin de chargement, changement de composition du groupe, début/fin de combat et début de round neutralisées pour cet acteur seulement. Les changements de mode nécessaires au chargeur natif ne sont pas diffusés comme des changements multijoueurs ordinaires. Les identités et attributions réseau ne sont donc pas volontairement réinitialisées par ce chemin.
- Une déconnexion révoque la lecture autorisée des champs et les écritures de stash, mais **ne réactive pas les règles/IA clientes tant que du travail natif de chargement reste possédé**.

### Désérialisation, erreurs et protection mécanique

`ThreadedGameLoader.DoLoad` reçoit un contexte de lecture limité au saver possédé ; il suit ses tâches natives via `AsyncLocal`. La lecture des aires est limitée aux GUID présents dans le checkpoint. Le contexte est retiré dans les finalizers, y compris après exception, et cesse d'autoriser les écritures dès sa révocation.

Seuls les setters des champs `Damage` et `BaseValue` nécessaires à cette lecture sont autorisés dans ce contexte. `UpdateValue` reste bloqué : le setter natif de `BaseValue` peut écrire son champ, mais ne déclenche pas le recalcul et ses callbacks. Les règles, jets, consommations, IA et autres mutations ne reçoivent pas de dérogation globale. La première mutation inattendue est retenue comme cause d'échec et journalisée.

Le chargeur surveille le processus natif, conserve la pause et les obligations mécaniques sur les mondes concernés, puis exige fin du travail natif, callback final, joueur remplacé, campagne/aire cohérentes et gestionnaire de sauvegarde au repos. Une limite de deux minutes signale un échec, mais n'est pas interprétée comme la fin des workers : la propriété est conservée jusqu'à observation de leur terminaison. Une observation défaillante ne déclenche pas un nouveau chargement.

Les obligations mécaniques ne sont jamais levées par le chargeur. En particulier, les caches de caractéristiques, ordres et états réseau doivent encore être réappliqués/vérifiés par la barrière avant reprise. **Une erreur qui laisse le jeu en pause protégée n'est pas une récupération réussie.**

## Preuves hors jeu

Commandes exécutées sur le code final de cet incrément :

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 101DFBE5E635337D74DBC57CBC5C8AC629F23CD1B673178A6D485333BC124A65
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 101DFBE5E635337D74DBC57CBC5C8AC629F23CD1B673178A6D485333BC124A65
```

Les deux compilations réussissent chacune **954 tests** : 33 traduction, 75 réseau et 846 principaux. Les 23 nouveaux essais couvrent admission unique, propriété exclusive, capabilities/clones, verrou natif, révocation, contexte de tâches, lecture des champs sans recalcul, exceptions, notifications de cycle de vie, sauvegarde protégée, dossiers synthétiques et refus des anciens manifestes/barrières révoquées avant accès au monde natif. Les tests de chargement utilisent exclusivement des sources et objets synthétiques ; ils ne vérifient pas le watchdog sur un vrai chargeur Unity.

Les 233 clés françaises restent vérifiées. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Compilation par défaut : `B1CFAF74D163E3A39E15E65A2453AE359CE0E094153E3B7502089BAF487FD9A1`.

Candidat prototype exact : `101DFBE5E635337D74DBC57CBC5C8AC629F23CD1B673178A6D485333BC124A65`. DLL principale SHA-256 : `2814C9E85F1B1AC46671743AB4808722362C72BFF190AAE73DF695FFF8A73AAE`.

Diagnostic Mono du candidat exact réussi, avec les vrais setters du moteur appliqués à des objets synthétiques :

```text
native-client-load-lease: field-hydration-without-recalculation=True; worker-context-and-revocation=True; native-load-not-invoked=True
```

Le diagnostic installe également le hook de `ThreadedGameLoader.DoLoad` sans appeler cette méthode. Il n'installe pas le hook d'`AreaDataStash` dans ce processus hors jeu : l'initialiseur statique du stash aurait des effets de fichiers indésirables. Il n'appelle ni le chargeur runtime ajouté, ni `LoadRoutine`, `LoadArea`, `Game.Instance`, le son ou les fonctions Unity. Les écritures de la capacité sont remplacées par un callback mémoire synthétique.

Preuves : `local-artifacts/a0-candidates/101DFBE5E635337D74DBC57CBC5C8AC629F23CD1B673178A6D485333BC124A65/offline-rule-dispatch/`, `success=true`, `gameStarted=false`, `runtimeValidated=false`. Le manifeste et sa DLL passent sept contrôles de protocole 52.

## Travail restant avant une récupération jouable

1. Relier capture, transfert, chargement et état de référence réseau à une barrière native explicite, avec adaptation des délais, messages de chargement, accusés de reconstruction et Commit/Abort. Aucun branchement automatique n'est présent ici.
2. Compléter l'admission de point sûr autour des états d'interface particuliers, notamment création/respecialisation de personnage, et vérifier le nettoyage natif réel avant d'autoriser ce chemin en jeu.
3. Réappliquer les caches confirmés de caractéristiques et autres données de référence, restaurer les références/services et contrôleurs requis, reprendre les ordres au bon point et comparer l'empreinte commune. Certains callbacks natifs `PostLoad` peuvent encore être refusés ; ils ne doivent pas être autorisés globalement pour obtenir artificiellement un chargement réussi.
4. Après échec, fournir reconstruction/repli vérifié ou sortie explicite vers une sauvegarde sûre. Vérifier le rétablissement du gestionnaire natif après erreur, sans supprimer un état de chargement alors que ses workers sont encore actifs.
5. Démontrer reprise, tour par tour jouable et sauvegarde/rechargement, puis couvrir invocations, renforts et campagne. Vérifier réellement les voix `WOTR_AIVO` sur le paquet exact lorsque Nicolas autorisera le jeu.

Mod stable installé 0.4.16 et slot expérimental désactivé inchangés ; aucune installation ou activation, aucun marqueur `RTWP_RUNTIME_VALIDATED.json`, aucune modification des sauvegardes originales ou de la production de voix. Le chantier et la validation moteur restent incomplets.
