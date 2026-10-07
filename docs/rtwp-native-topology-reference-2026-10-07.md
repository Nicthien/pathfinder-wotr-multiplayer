# Inventaire natif de l'hôte lié à la référence de reconstruction

7 octobre 2026 — variante 0.4.16.31, protocole 56, empreinte de valeurs de combat schéma 17. Campagne de base sans DLC.

## Problème traité

L'inspection locale précédente pouvait accepter un graphe cohérent auquel un composant manquait entièrement : aucun mauvais lien n'était alors présent à détecter. La référence de reconstruction transporte désormais l'inventaire attendu depuis l'hôte, au lieu de ne comparer que les objets localement présents.

## Parcours raccordé

`GetRealTimeNativeReferenceState` capture les valeurs de combat et l'inventaire des unités/zones de l'hôte en pause sûre. Il refuse chargement/sauvegarde, file de création non vide, interface personnage active, croisade, campagne DLC et contexte client. Cette capture particulière n'est pas utilisée par les snapshots fréquents.

L'inventaire contient :

- identité et catégorie unité/zone ;
- types des parts, avec leur ordre et leur multiplicité ;
- faits dans leur ordre natif, avec identité, type d'exécution et GUID de blueprint ;
- composants dans leur ordre natif, avec type d'exécution et nom du composant source du blueprint.

Le champ est sérialisé dans la référence bornée à 8 Mio. La SHA-256 du contenu de référence inclut donc cet inventaire et reste liée au checkpoint exact et à son reçu. Le protocole passe à 56 : les deux joueurs doivent employer le même paquet. Le hash de valeurs de combat reste au schéma 17 ; il n'est pas présenté comme une empreinte complète du graphe.

L'hôte compare également l'inventaire après capture de l'archive et avant demande de vérification. Un changement depuis la référence préparée fait abandonner la procédure protégée.

La cliente exige l'inventaire, l'inspecte et le compare avant réaffectation/application, puis après application des valeurs. Le service renvoie son inventaire effectivement recapturé ; l'acteur client le compare de nouveau avant le reçu. Un inventaire absent, mal formé ou différent ne peut pas être remplacé par un hash de PV identique. Aucun callback mécanique supplémentaire n'est lancé par la capture/comparaison.

L'ordre d'énumération des entités entre pools est normalisé pour la comparaison, sans trier les listes de l'appelant. L'ordre des faits, parts et composants reste significatif. Les identifiants, types, collections et tailles sont validés ; l'inventaire est borné à 32 768 entités et 262 144 éléments au total, en plus de la limite de référence sérialisée.

## Preuves hors jeu

- **1 108 tests réussis dans chacune des deux compilations** : 33 traduction, 112 réseau et 963 principaux ; 233 clés françaises vérifiées.
- Vingt-trois nouveaux cas réseau couvrent copie/roundtrip du contenu, ordre des pools, objets absents/supplémentaires, ordre/types/blueprints/sources différents et inventaires invalides.
- Quatre nouveaux cas principaux couvrent la capture native synthétique sans activation, une topologie différente malgré des valeurs de combat identiques, un inventaire omis et un changement de structure de l'hôte avant vérification. Les échanges d'acteurs utilisent un service de combat simulé, pas un monde Unity.
- Diagnostic Mono du candidat exact réussi : capture d'une zone détachée synthétique avec fait, composant et part, suppression complète du composant, puis différence d'inventaire détectée. Aucun chargement, PostLoad ou activation natifs n'est invoqué.
- Sept contrôles du protocole 56 réussis sur le candidat exact. Les protections hors jeu précédentes restent vérifiées.

## Paquet exact

- Build par défaut : `441DBF5CAB26EF3B4E5EA555A502FC62AE38E7652BB21566884B007C0CCC9F65`.
- Candidat prototype : `3F7E4C6C0FF3ADF9F4F7C95BA5A4FCB3DC1CFE8C3CA1FED6A7B22A67D148B877`.
- DLL principale SHA-256 : `480CEE4E2F91A0D60057A8DD0B09F472BEB68337D0395F9128881C22D6AC0444`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 3F7E4C6C0FF3ADF9F4F7C95BA5A4FCB3DC1CFE8C3CA1FED6A7B22A67D148B877
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 3F7E4C6C0FF3ADF9F4F7C95BA5A4FCB3DC1CFE8C3CA1FED6A7B22A67D148B877
```

## Limites et suite obligatoire

Cet inventaire compare la structure décrite ci-dessus, **pas tous les champs et références internes arbitraires**, abonnements, caches ou conséquences silencieuses des composants. Les autres catégories d'entités, le graphe du joueur et les états internes des contrôleurs ne sont pas certifiés. Des parts liées aux vues, légitimement différentes entre PC, peuvent entraîner un refus conservateur : leur traitement ne sera pas assoupli sans audit et preuve moteur.

Il faut encore compléter les références/caches mécaniques, restaurer et vérifier journal des commandes et contrôleurs, terminer Commit/Abort, raccorder la réparation automatique puis assurer reconnexion/repli jouable ou sortie sûre. Les scénarios de campagne, voix et sauvegarde/rechargement nécessitent toujours une validation ultérieure dans deux instances réelles autorisées.

Aucune obligation mécanique levée, aucun ordre/séquence validé, aucune reprise ou sauvegarde autorisée par ce seul inventaire. Aucun jeu ou monde Unity lancé ; aucune installation/activation, sauvegarde originale ou production de voix modifiée. Stable 0.4.16 et ancien slot expérimental désactivé préservés. Candidat hors installation, `runtimeValidated=false`, aucun `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper `NU1903` préexistant non résolu. Le chantier reste incomplet.
