# Capture native de l'hôte — 7 octobre 2026

Variante 0.4.16.23 ; protocole 51 inchangé. Campagne de base sans DLC.

## État réel

Le chemin de capture du monde courant est implémenté dans le code, mais **aucun monde réel n'a été capturé** : WotR reste fermé pendant la génération des voix. Les tests et le diagnostic Mono vérifient la transaction et des documents synthétiques, pas les callbacks de préparation d'une campagne.

`MultiplayerHost.CaptureCurrentNativeCheckpointAsync` fournit une capacité interne à la future barrière de reconstruction. Elle n'est pas appelée automatiquement par la réparation actuelle et n'est pas exposée dans l'interface. Il manque encore son consommateur réseau, le rechargement client protégé et la vérification de reprise.

## Capture ajoutée

- Autorisation réservée à l'hôte actif du prototype, sous une barrière possédée par l'appelant et dans une phase non stable. Identités de session, chargement, combat, état RTwP, époque et dernière séquence doivent rester inchangées. La capacité cesse d'être valable dès qu'un de ces éléments change.
- Travail sur le thread principal, en pause effective, sans projectile ni action atomique actifs, hors croisade, dialogue, cinématique et campagne DLC. Les sauvegardes/chargements/commits natifs concurrents, créations en attente, destructions différées et scènes chargées incohérentes sont refusés.
- Lecture du brouillard courant par la coroutine GPU native, sans `SavedFogMasks.Add`, qui écrirait le masque de travail. Une requête déjà possédée par un autre chemin est refusée. Le résultat GPU doit être terminé et sans erreur avant lecture des données. L'attente possède une limite de vingt secondes et vérifie la pause et l'identité du monde. En cas d'abandon, la requête native continue séparément jusqu'à sa terminaison ; elle ne peut plus publier de capture.
- Préparation native `TurnOff`/`PreSave`, avec restaurations enregistrées **avant** chaque mutation. Ordre natif des scènes puis de l'aire conservé ; le joueur et la scène interzones sont ensuite restaurés. Les objets auparavant inactifs ne sont pas réactivés par le `SceneEntitiesState.TurnOn` global : les indicateurs de scène et les activations individuelles sont restaurés séparément.
- Préparation d'un en-tête manuel avec `FolderName` volatile et `ISaver` mémoire déjà attaché. La branche native de création de slot/`ZipSaver` n'est pas utilisée. Pas de `SaveRoutine`, screenshot, publication dans la liste de sauvegardes, compteur de sauvegardes ou commit cloud.
- Sérialisation native, en mémoire, de l'en-tête, du joueur, de la scène interzones, de l'aire courante et des scènes additionnelles chargées. Les données des aires/scènes déchargées sont lues dans le stash courant, et non dans une ancienne sauvegarde joueur.
- Historique et textures additionnelles conservés ; fichiers de brouillard de chaque aire lus explicitement, puis masques de travail et masque GPU courant superposés pour l'aire active. Le fichier `lock`, les fichiers temporaires arbitraires et les chemins externes ne sont pas des entrées du checkpoint.
- Noms natifs reproduits : GUID suivi directement du nom de scène tronqué à 62 caractères pour le JSON ; GUID, point, nom de scène et `.fog` pour le brouillard. Collisions, noms dangereux et ambiguïtés du lecteur natif de brouillard sont refusés.
- Lecture des fichiers et sérialisation bornées ; les quotas de la source mémoire restent actifs. `history` est lu via l'accès natif, qui ferme/rouvre son propriétaire de journal de travail pour assurer le flush. Aucun fichier de sauvegarde joueur n'est écrit.
- Vérification après restauration : mêmes membres de scène, mêmes indicateurs d'activation, champs de localisation sauvegardée remis à leur valeur initiale, absence de nouvelle création en attente, même temps logique et même monde ; comparaison de l'empreinte de combat et des temps répliqués. Le véritable décodeur natif d'en-tête précontrôle ensuite la source avant publication.

Le checkpoint produit `tbm.json` vide, comme une sauvegarde native hors combat tour par tour. Cette capacité est RTwP uniquement ; elle ne prétend pas capturer un contrôleur TB actif.

## Gestion des échecs

`RealTimeNativeCaptureTransaction` exécute toutes les restaurations, même si plusieurs échouent, conserve les causes de préparation/sérialisation et de restauration, puis détruit toute source non publiée. Une source n'est scellée et retournée qu'après vérification.

Si le chemin natif de préparation/capture échoue, le monde hôte concerné reste en pause et reçoit une obligation persistante de reconstruction mécanique. Cette protection est volontairement conservatrice : une vérification partielle ne doit pas autoriser silencieusement la reprise ou l'écriture d'une sauvegarde. **Cette pause protégée n'est pas une récupération réussie.**

Les callbacks `PreSave` natifs peuvent journaliser une exception sans la propager. Les contrôles d'activation et d'empreinte réduisent ce risque, mais ne prouvent pas l'intégrité de toutes les parties natives. L'essai moteur reste indispensable.

## Vérifications réalisées

Commandes exécutées, sans lancer WotR :

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint AD3B57E2B520BD49CCEA212F3A9A7C990EC29F14F4442B45FD4829F15FACB53E
& .\tools\check-package-protocol.ps1 -CandidateFingerprint AD3B57E2B520BD49CCEA212F3A9A7C990EC29F14F4442B45FD4829F15FACB53E
```

Les deux compilations passent chacune **884 tests** : 33 traduction, 41 réseau et 810 principaux. Les 23 nouveaux tests couvrent l'ordre de préparation/restauration, les mutations partielles suivies d'erreurs, les échecs multiples de restauration, la destruction des sources refusées, les vérifications finales, les noms natifs, les fichiers tronqués/grossissants/surdimensionnés et le parcours synthétique jusqu'au véritable décodeur natif d'en-tête.

Les 233 clés françaises sont vérifiées. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Une incohérence d'empaquetage a également été corrigée : le générateur et le contrôle de préparation A0 contenaient encore la valeur fixe 48. Le protocole est désormais lu dans la constante **de la DLL du paquet**, par lecture des métadonnées PE uniquement, puis comparé strictement au manifeste. La préparation refuse aussi un dossier candidat existant dont le contenu ne correspond plus à l'empreinte attendue. Le contrôle est intégré aux compilations locales et vérifie les protocoles absents, incorrects ou de mauvais type, sans charger le mod ou le jeu. Les anciens manifestes restent des traces historiques ; ils ne sont pas rétroactivement validés.

Le contrôle final du candidat passe sept vérifications et confirme `protocolVersion=51` dans son manifeste. Ces contrôles PowerShell s'ajoutent aux 884 tests .NET ; ils ne sont pas des tests moteur.

Empreinte de la compilation par défaut : `228533AD1D6009316B6DEFAD2902D577FC29F10E1B9F7AE897179884546D0091`.

Candidat prototype : `AD3B57E2B520BD49CCEA212F3A9A7C990EC29F14F4442B45FD4829F15FACB53E`. DLL SHA-256 : `4C03D81CD1DDE8669D529578E2F5FB8A4B02D3C86CE24D74EBFA220966835B92`.

Diagnostic Mono du candidat exact réussi :

```text
native-host-capture-transaction: synthetic-restoration=True; native-world-not-invoked=True
native-memory-checkpoint: actual-header-decoder=True; game-load-not-invoked=True
```

Preuves dans `local-artifacts/a0-candidates/AD3B57E2B520BD49CCEA212F3A9A7C990EC29F14F4442B45FD4829F15FACB53E/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`.

Le diagnostic Mono n'appelle pas `RealTimeNativeHostCapture`, `Game.Instance`, `PreSave` sur un joueur réel, la lecture GPU, un accès au stash natif, `LoadRoutine` ou Unity. Il ne démontre ni les animations ni la restauration réelle des activations ; les détails du chemin natif ont été audités dans les assemblies du jeu 2.7.0x et compilés seulement.

## Prochaine preuve et travail restant

1. Relier cette capacité à une barrière de reconstruction native dédiée, avec identité de checkpoint, session, chargement, époque, dernière séquence incluse, empreinte du paquet et archivage des diagnostics. Ne pas confondre cette capture complète avec l'état visuel du protocole de réparation existant.
2. Ajouter le transfert fiable borné et vérifié du checkpoint, et le refus des fragments anciens, manquants, dupliqués ou appartenant à une autre session.
3. Fournir la capacité d'écriture du **stash client de travail seulement** au chargeur natif ; contourner les hooks de chargement ordinaires uniquement dans cette barrière, sans perdre la session ou charger un chemin de sauvegarde joueur.
4. Reconstruire le monde client, restaurer les contrôleurs, reprendre au premier événement non inclus et vérifier le monde avant ACK/Commit. Les ordres actifs et états réseau ne sont pas automatiquement restaurés par les seuls fichiers natifs.
5. Lever les obligations mécaniques uniquement après reconstruction vérifiée, puis démontrer reprise ou repli jouable et sauvegarde/rechargement cohérents. Une erreur de restauration hôte doit également offrir une sortie explicite vers un état sûr, pas une pause définitive.
6. Terminer invocations/renforts et cas de campagne ; tester le paquet exact à deux instances et vérifier `WOTR_AIVO` quand Nicolas autorisera le lancement du jeu.

Aucun marqueur `RTWP_RUNTIME_VALIDATED.json`, aucune activation/installation de cette variante. Mod stable 0.4.16, sauvegardes originales et production des voix inchangés. Le chantier reste incomplet.
