# Transfert borné du checkpoint natif — 7 octobre 2026

Variante 0.4.16.24 ; protocole 52 ; campagne de base sans DLC.

## Ce qui est démontré, et ce qui ne l'est pas

Le transfert fiable d'une archive en mémoire est implémenté et vérifié hors jeu : fragments réordonnés ou dupliqués, pertes simulées, accusés de réception, quotas et contrôle SHA-256. Un essai utilise le transport TCP réel en boucle locale. Les handlers multijoueurs sont testés avec des interactions de jeu simulées.

**Aucun monde de jeu n'a été capturé, transféré ou rechargé pendant ces essais.** Le nouveau point d'entrée hôte n'est pas encore appelé automatiquement par la réparation. Le client conserve une source mémoire scellée, mais ne la charge pas dans le moteur. Un reçu `Received` atteste les octets vérifiés, jamais une reconstruction mécanique, un Commit, une reprise ou l'autorisation de sauvegarder.

## Contrat et comportement

- Trois messages ajoutés à la fin des identifiants existants, sans renuméroter le chemin tour par tour : préparation/manifeste/abandon, fragment, accusé de réception.
- Préparation liée à session, chargement, époque, UUID de checkpoint, empreinte du paquet, dernière séquence incluse et empreinte canonique du combat. Un checkpoint concurrent ne remplace pas celui déjà accepté dans la même époque.
- Hôte autorisé uniquement dans le prototype actif, sous une barrière non stable détenue par l'appelant, en pause effective, avec exactement une cliente et une empreinte de paquet identique.
- La cliente confirme la préparation seulement après confirmation de la pause sur le thread principal. Identités étrangères ou périmées refusées ; un abandon conserve un refus jusqu'au changement de contexte.
- Archive maximale de 132 Mio ; fragments de 32 Kio ; fenêtre de huit fragments en attente ; douze tentatives maximum espacées d'une seconde. Délai global borné selon la taille, en plus du délai initial de préparation.
- Allocation seulement après validation du manifeste et de sa correspondance avec la préparation. Les deux extrémités possèdent leurs données ; les mutations d'un objet fourni par l'appelant ne modifient pas silencieusement le transfert.
- SHA-256 par fragment et pour l'archive complète. Numéros, tailles, identités et doublons conflictuels vérifiés. La perte d'un reçu final est couverte par la retransmission du manifeste et le renvoi du reçu par la cliente.
- Après réception, décodage de l'archive en fichiers mémoire uniquement : pas de désérialisation d'un monde natif, écriture du stash, appel à `Game.LoadGame` ou levée des protections mécaniques.
- Reset, nouveau chargement, changement de session/époque/phase ou abandon révoquent la source. Les échecs libèrent les buffers et ne reprennent pas le combat.

`MultiplayerHost.TransferCurrentNativeCheckpointAsync` appelle la capture hôte introduite en .23 puis publie l'archive. Son résultat est un manifeste, pas une validation de réparation. `MultiplayerClient.CloneTransferredNativeCheckpoint` fournit au futur chargeur protégé une copie scellée seulement si le contexte exact est toujours valide.

## Vérifications exécutées

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint FD8AD33C3EDE6D80BE13CA85E79B7B163871B110C129559B02325871B84F7092
& .\tools\check-package-protocol.ps1 -CandidateFingerprint FD8AD33C3EDE6D80BE13CA85E79B7B163871B110C129559B02325871B84F7092
```

Les deux compilations réussissent chacune **931 tests** : 33 traduction, 75 réseau, 823 principaux. Les 47 nouveaux essais couvrent les primitives de transfert, le TCP local et les handlers. Dans la compilation sans prototype, les handlers expérimentaux doivent rester inactifs ; les essais de leur comportement complet sont exécutés avec le prototype compilé.

Le test architectural de messages utilisés cherchait seulement dans les services multijoueurs et dans le premier type de chaque fichier. Il examine désormais aussi les helpers de transport de production et tous les types de chaque fichier, sans inclure les fixtures de tests. Il détecte ainsi la création réelle des fragments dans l'émetteur, sans masquer les messages inutilisés.

233 clés françaises et paramètres de traduction vérifiés. Avertissement préexistant AutoMapper `NU1903` non résolu.

Empreinte de compilation par défaut : `0DC3C1AC49A24A09FBCFE393C7B93DD8719A10046244D6F9F5F75C18E6143FD9`.

Candidat prototype : `FD8AD33C3EDE6D80BE13CA85E79B7B163871B110C129559B02325871B84F7092`. DLL principale SHA-256 : `A9B538C5650DDDA660D38C9ECB58B5929E558A5706A0F30DA5E94D804B4E3F52`.

Le diagnostic Mono vérifie les DLL principale **et réseau exactes** du candidat. Résultat :

```text
native-checkpoint-transfer: reordered-and-duplicated=True; archive-only=True; native-load-not-invoked=True
native-host-capture-transaction: synthetic-restoration=True; native-world-not-invoked=True
native-memory-checkpoint: actual-header-decoder=True; game-load-not-invoked=True
```

Preuves dans `local-artifacts/a0-candidates/FD8AD33C3EDE6D80BE13CA85E79B7B163871B110C129559B02325871B84F7092/offline-rule-dispatch/` : `success=true`, `gameStarted=false`, `runtimeValidated=false`. Sept contrôles PowerShell confirment aussi le protocole 52 du manifeste et de sa DLL.

## Travail critique suivant

1. Construire le chargeur client protégé : précontrôle natif, capacité d'écriture du stash client de travail seulement, contournement limité des hooks ordinaires qui réinitialisent la connexion, conservation de la session et de l'époque. Ne pas utiliser une sauvegarde joueur comme destination ou réécrire les sauvegardes originales.
2. Intégrer capture, transfert et chargement dans une barrière dédiée. **Adapter le délai de réparation existant avant de brancher l'appel automatique** : le délai actuel peut expirer avant la fin d'un transfert natif. Aucun branchement automatique n'est ajouté dans cet incrément.
3. Restaurer contrôleurs et commandes actives ; vérifier empreinte mécanique et état jouable avant ACK/Commit. L'empreinte de préparation actuelle concerne le combat capturé, pas la restauration complète du registre d'ordres réseau.
4. Prévoir reprise ou repli réellement jouable, puis sauvegarde cohérente. Un échec de restauration hôte doit également permettre une sortie explicite vers un état sûr.
5. Terminer invocations, renforts et cas de campagne ; valider ensuite le paquet exact à deux véritables instances et avec `WOTR_AIVO`, uniquement quand Nicolas autorisera le lancement du jeu.

Aucun jeu lancé, aucune installation ou activation expérimentale, aucun marqueur `RTWP_RUNTIME_VALIDATED.json`. Le mod stable installé, les sauvegardes originales et la production des voix ne sont pas modifiés. Le chantier reste incomplet.
