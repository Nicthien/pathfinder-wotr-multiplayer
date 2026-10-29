# Contrôle local des références natives après reconstruction

7 octobre 2026 — variante 0.4.16.30, protocole 55, schéma d'empreinte 17. Campagne de base sans DLC.

## Changement raccordé

La restauration stricte des données de référence inspecte maintenant les unités et zones natives présentes, avant réaffectation des participants et après application des valeurs confirmées. Cette inspection lit les champs et collections ; elle ne lance ni PostLoad, ni activation, ni recalcul, ni commande.

Contrôles ajoutés :

- identités d'entités présentes et uniques entre les deux collections ; objets non détruits par disposal, non en cours de disposal et ayant terminé PostLoad ;
- propriétaires et stockage des gestionnaires de faits et de parts ;
- faits non disposés, PostLoad terminé, gestionnaire correct, identité présente et unique dans leur entité ;
- composants non disposés et reliés au bon fait ; parts non disposées, PostLoad terminé et reliées à la bonne entité ;
- absence d'objets partagés ou répétés dans ces collections ;
- liens unité/descripteur/caractéristiques, propriétaires des contrôleurs de combat et de commandes, stockage de commandes et structures de combat indispensables présents.

L'autorisation est revérifiée pendant le parcours, borné à 262 144 objets. Une anomalie indique l'identité de l'entité et la catégorie du lien incorrect. L'inspection ne corrige pas silencieusement le graphe et ne remplace pas un composant manquant par un objet de présentation.

## Reçu de chargement renforcé

Le service exige désormais le bail natif exact, terminé, sans erreur et dont le drainage a été positivement observé. Le chargeur marque cette observation seulement lorsqu'il constate que le processus natif ne charge plus. Disposer du bail, constater un délai dépassé ou posséder un marqueur de reconstruction ne suffit pas.

L'état est revérifié pendant la restauration ; une erreur tardive invalide le reçu. Les protections d'autorité, de pause, d'identité du monde et d'obligation mécanique restent en place. Aucun reçu ne lève cette obligation, ne valide un ordre ou une séquence, ni n'autorise reprise ou sauvegarde.

## Preuves hors jeu

- **1 081 tests réussis dans chacune des deux compilations** : 33 traduction, 89 réseau et 959 principaux. Les 233 clés françaises restent vérifiées.
- Quarante-trois nouveaux cas du graphe synthétique : état cohérent sans activation, descripteur/statistiques/contrôleurs corrompus, gestionnaires étrangers, faits/composants/parts invalides ou partagés, identités en collision, autorisation révoquée, entité nulle et limite de taille. Un cas supplémentaire vérifie drainage, fermeture du bail et erreur tardive.
- Diagnostic Mono du candidat exact réussi. Son nouvel essai construit uniquement une zone détachée synthétique avec un fait, un composant et une part : état cohérent accepté, mauvais propriétaire de composant/part et entité disposée refusés, puis état rétabli accepté sans activation. Les contrôleurs d'unité sont couverts par les tests .NET, pas par ce nouvel essai Mono.
- Sept contrôles du protocole 55 réussis sur le candidat exact. Avertissement AutoMapper `NU1903` préexistant non résolu.

La première compilation du nouvel essai Mono a révélé des membres non publics inaccessibles depuis son assembly. La préparation synthétique a été corrigée pour utiliser des objets non initialisés et des écritures de champs par réflexion, comme les autres essais isolés ; aucun constructeur mécanique de buff n'est appelé. La validation Mono ci-dessus est postérieure à cette correction.

## Paquet exact

- Build par défaut : `25AACB97E1531A8D65B537AA3E2208E8496724356C6BB735DC407C4540F0800C`.
- Candidat prototype : `724D4ED564277432573315427C06A360A238AAD9BC502F83A5907ADD504288C1`.
- DLL principale SHA-256 : `0EBE463FFA5C0882DB5AC5476537EAF48D9AFC350E9517A59021D26FAFAF6997`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 724D4ED564277432573315427C06A360A238AAD9BC502F83A5907ADD504288C1
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 724D4ED564277432573315427C06A360A238AAD9BC502F83A5907ADD504288C1
```

## Limites et suite obligatoire

Ce contrôle vérifie la cohérence locale des objets présents, **pas l'égalité du graphe avec celui de l'hôte**. Un composant complètement absent des deux collections locales peut encore échapper au contrôle ; les références internes arbitraires des composants, leurs abonnements, caches, effets silencieux et les autres catégories d'entités ne sont pas certifiés. La limite de taille et les indicateurs natifs doivent également être confrontés aux scènes réelles de campagne.

Il reste donc à comparer la structure et les références attendues depuis l'hôte, terminer l'audit des callbacks, restaurer le journal des commandes et les contrôleurs, exécuter Commit/Abort, raccorder la réparation automatique et assurer reconnexion/repli jouable ou sortie sûre. La validation moteur et campagne sur deux instances avec les voix reste nécessaire lorsque le jeu sera autorisé.

Aucun jeu, monde Unity ou audio lancé. Aucune installation/activation, sauvegarde originale ou production de voix modifiée. Stable 0.4.16 et ancien slot expérimental désactivé préservés. Candidat hors installation, `runtimeValidated=false` ; aucun `RTWP_RUNTIME_VALIDATED.json`. Le chantier reste incomplet.
