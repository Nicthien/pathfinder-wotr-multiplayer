# Journal d'ordres lié à la référence native de reconstruction

7 octobre 2026 — variante 0.4.16.32, protocole 57 ; empreinte de valeurs de combat toujours au schéma 17. Campagne de base sans DLC.

## Modification raccordée

La référence native transporte désormais `NetworkAcceptedOrderReference` : session, chargement, époque, dernière séquence incluse et les 32 dernières traces d'ordres acceptés de cette époque. Chaque trace contient séquence, propriétaire, unité, catégorie, identifiant de commande et SHA-256 du message accepté, sous forme Base64 canonique. Ce sont des preuves bornées d'acceptation, pas des commandes exécutables ni un inventaire d'actions encore actives.

Le contenu est inclus dans les octets et la SHA-256 de la référence existante. Il n'est pas ajouté aux snapshots fréquents, ni au hash de valeurs de combat. L'hôte capture le journal sous verrou et refuse sa modification après capture de l'archive ou avant `Verify`, même si le compteur de séquence n'a pas changé.

La cliente exige une référence d'ordres liée exactement au manifeste : session, sauvegarde chargée, époque et séquence incluse. La vérification des données et de l'inventaire natif précède le remplacement atomique de son journal. Une barrière toujours détenue, le mode RTwP et une époque identique sont nécessaires ; un curseur déjà plus avancé est refusé. Aucun ordre natif, règle, dépense ou animation n'est exécuté par ce remplacement.

Le curseur réseau **n'est pas avancé**. La phase, les obligations de réparation, l'interdiction de sauvegarder et la pause restent inchangées. Le futur Commit doit encore traiter les flux, contrôleurs et états de présentation avant toute reprise. Un échec ultérieur laisse le monde protégé ; cette opération n'est pas une récupération complète.

Les snapshots du journal retournent maintenant des enveloppes indépendantes : modifier un snapshot ne peut plus modifier indirectement le journal original. Les données reçues sont validées puis copiées et revalidées avant publication. Les collections, séquences, identités, catégories, tailles et hashes sont bornés ; doublons de séquence ou de commande et références futures sont refusés.

## Preuves hors jeu

- Compilation standard et prototype : **1 152 tests réussis dans chacune**, soit 33 traduction, 139 réseau, 980 principaux. Les 233 clés françaises restent vérifiées.
- 27 nouveaux tests réseau : codec, propriété des copies, identité de barrière et journaux mal formés.
- 13 nouveaux tests du journal : restauration des traces sans changement de curseur/phase, copies isolées, barrières invalides sans remplacement, modification du journal hôte détectée.
- Quatre nouveaux scénarios d'acteurs : référence d'ordres absente, séquence incompatible, restauration d'une trace non vide, changement du journal hôte avant Verify. Les acteurs emploient un service de combat simulé, pas Unity.
- Diagnostic Mono du candidat exact : journal synthétique capturé/restauré, curseur inchangé, copie isolée et restauration hors barrière refusée. Aucun monde, callback mécanique ou commande native invoqué.
- Sept contrôles du protocole 57 réussis pour le candidat exact.

## Incident du diagnostic et portée de sa correction

Le premier diagnostic Mono a échoué : protobuf-net demandait `System.Collections.Immutable` 7, absent du dossier du diagnostic. Le paquet candidat contenait déjà cette dépendance en version 9, comme le mod stable ; il n'a pas été modifié pour contourner le test.

Le projet du diagnostic référence désormais explicitement la DLL du paquet. Le script vérifie son SHA-256 identique, archive la configuration de l'exécutable et inscrit son empreinte ainsi que celle de la dépendance dans la preuve. La configuration autonome redirige les demandes vers la version 9 ; le diagnostic réussit avec cette configuration. **Cela ne prouve pas la résolution des dépendances par UnityModManager dans le jeu**, qui reste à vérifier ultérieurement. Aucun chargement du mod par le jeu n'est prétendu.

## Paquet et reproduction

- Build standard : `841BEBD39663C13067BDD09A8FD2A401FE870CFFFF3C281EF860E96C99BD88C2`.
- Candidat prototype : `88C9CB65217045F25B04EFF9E679D279CC122C968EE64C350235B183512CE745`.
- DLL principale SHA-256 : `540790F473156F6733C616B59AC8AC2EAC3DF044AFC9F052AC75A2D426B1CB50`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 88C9CB65217045F25B04EFF9E679D279CC122C968EE64C350235B183512CE745
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 88C9CB65217045F25B04EFF9E679D279CC122C968EE64C350235B183512CE745
```

Les preuves du diagnostic sont dans `local-artifacts/a0-candidates/<empreinte>/offline-rule-dispatch/`, hors du dossier du paquet. Aucun marqueur de validation moteur n'est créé.

## Suite indispensable

Le journal historique est limité à 32 traces de l'époque courante. `BeginRepair` change actuellement d'époque et réinitialise le curseur : cette modification ne conserve pas artificiellement les commandes de l'époque précédente et ne restaure pas leurs actions natives. Les ordres actifs/queues, contrôleurs et état de présentation nécessitent une représentation distincte ; ils ne doivent surtout pas être rejoués depuis ces hashes.

Il reste à terminer la certification mécanique, le point de reprise des flux et Commit/Abort, le branchement automatique de la reconstruction, reconnexion/repli jouable ou sortie sûre, puis les scénarios de campagne et la validation moteur à deux instances autorisées. Le contrôle de point sûr actuel exclut les projectiles et commandes non-mouvement en cours ; il ne certifie pas toutes les files et tous les contrôleurs.

Aucun jeu lancé, aucune installation/activation, aucune sauvegarde originale ou production de voix modifiée. Stable 0.4.16 et ancien slot expérimental désactivé préservés. Candidat `runtimeValidated=false`, pas de `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper NU1903 préexistant non résolu. Le chantier reste incomplet.
