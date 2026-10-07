# Demande réseau de reconstruction native — 7 octobre 2026

Variante 0.4.16.26, protocole 53, campagne de base sans DLC.

## Comportement ajouté

Le parcours interne `MultiplayerHost.ReconstructCurrentNativeCheckpointAsync` enchaîne la préparation en pause, la capture hôte, le transfert vérifié, puis une demande `Load` liée au manifeste exact. Il attend désormais un accusé `Loaded` distinct du reçu d'octets `Received`.

La cliente accepte `Load` uniquement depuis l'hôte, pour le checkpoint déjà reçu et vérifié dans la session/sauvegarde/époque/phase courantes. Le handler appelle le chargeur protégé ajouté précédemment. Une opération unique appartient à l'identité du checkpoint : un doublon pendant le chargement n'en programme pas un second ; après succès, il renvoie le même accusé. Une opération échouée ne peut pas être relancée par une répétition du paquet. Une révocation pendant le chargement invalide le reçu sans prétendre annuler les workers natifs.

L'accusé `Loaded` nécessite un reçu contenant le monde et son obligation de réparation. L'hôte ne l'accepte qu'après les octets reçus et une demande de chargement explicite, depuis le pair attendu, pour la sauvegarde et le checkpoint exacts, avec un numéro de fragment nul.

Les retransmissions `Load` restent bornées par une fenêtre de 150 secondes. Le premier accusé ouvre une fenêtre de vérification de 60 secondes ; les doublons ne la prolongent pas. La propriété du transfert reste conservée après réception des octets, au lieu d'être supprimée avant le chargement. Reset/Abort terminent les attentes en échec et révoquent le contexte. Les délais de l'ancienne réparation par snapshot ne doivent pas entrer en concurrence avec ceux d'une barrière native encore possédée.

## Limite essentielle

**Ce parcours interne n'est pas encore appelé automatiquement par l'ancienne réparation.** Son entrée et son handler réseau sont raccordés au chargeur, mais le coordinateur de vérification/Commit doit encore consommer son résultat. Les tests n'appellent jamais ce chargeur sur un monde réel.

`Loaded` ne change ni la phase du combat, ni la dernière séquence intégrée, ni les protections de sauvegarde. Il ne supprime aucune obligation mécanique. Une empreinte de combat visible ne suffit pas à autoriser le tour par tour : les composants mécaniques, caches, références et contrôleurs doivent encore être vérifiés/restaurés. Brancher immédiatement ce parcours sur Resume laisserait cette exigence non satisfaite.

## Vérifications hors jeu

- Build Release par défaut et préparation du candidat prototype : **983 tests réussis dans chacun**, soit 33 traduction, 80 réseau et 870 principaux. Les 233 clés françaises restent vérifiées.
- Vingt tests ajoutés : contrat `Load`/`Loaded` et sérialisation, requêtes invalides, chargement unique, erreurs, révocation, reçu incomplet, demande avant réception des octets, doublons avec reçu synthétique, droits et préconditions de l'accusé hôte, absence de Commit/reprise.
- Les essais d'opération utilisent des tâches et reçus synthétiques. L'essai de répétition du handler cliente injecte une opération déjà terminée synthétiquement ; il ne lance pas le jeu ni le chargeur natif.
- Diagnostic Mono réussi sur le candidat exact : interception des règles et champs natifs sur objets synthétiques, transfert, transaction de capture, décodeur d'en-tête et propriété du chargement. Il ne constitue pas un essai de reconstruction d'un monde Unity.
- Sept contrôles du protocole 53 réussis sur la DLL du candidat exact. Avertissement AutoMapper `NU1903` préexistant toujours présent.

Compilation par défaut : `3E45FBA59BBB8D3002AAED929D58DCA49E2C6F0E2DB26824B99779A68DC3E2D7`.

Candidat prototype : `73A3C1BDBC70F7FCB60C7B3BA16B7EA50569772B233014095111D782818A17EE`.

DLL principale SHA-256 : `C55F19ADD166F89B6623FDB1F2ABA88100F92CC1ABA7892ECDCE2D14DFC74977`.

Commandes vérifiées :

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 73A3C1BDBC70F7FCB60C7B3BA16B7EA50569772B233014095111D782818A17EE
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 73A3C1BDBC70F7FCB60C7B3BA16B7EA50569772B233014095111D782818A17EE
```

## Prochaine intégration nécessaire

1. Consommer le reçu dans une étape de vérification explicite : réappliquer l'état de référence confirmé, ses caches et les ordres à la bonne séquence, sans recréer des effets mécaniques concurrents.
2. Vérifier le graphe mécanique natif et les contrôleurs avant de lever uniquement les obligations appartenant à cette reconstruction. Puis collecter les accusés de vérification et exécuter Commit/Abort, avant de brancher ce parcours à la réparation automatique.
3. Traiter l'échec après chargement : reconstruction/repli réellement jouable ou sortie explicite vers une sauvegarde sûre. Un délai écoulé ne prouve pas la fin des workers natifs.
4. Valider ensuite le parcours avec deux véritables instances et les voix françaises, lorsque Nicolas autorisera le lancement du jeu.

Le mod stable installé 0.4.16 et le slot expérimental désactivé sont inchangés. Aucun jeu lancé, aucune installation/activation, aucun marqueur de validation moteur, aucune modification des sauvegardes originales ou de la production des voix. Le candidat est préparé hors jeu, pas validé pour jouer une campagne.
