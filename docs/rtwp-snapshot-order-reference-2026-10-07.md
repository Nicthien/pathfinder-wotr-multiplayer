# Journal d'ordres lié aux états périodiques

7 octobre 2026 — variante 0.4.16.34, protocole 58, schéma d'empreinte 18. Campagne de base sans DLC.

## Défaut et correction

Les confirmations d'ordres et les snapshots utilisent une séquence autoritaire commune. Une confirmation reçue après un snapshot plus récent peut être écartée correctement comme ancienne, mais laisser le journal client incomplet. Comme ce journal participe à l'empreinte canonique, deux mondes identiques pouvaient alors produire des empreintes différentes.

Le snapshot du prototype transporte maintenant sa propre référence bornée des 32 derniers ordres acceptés de l'époque. L'hôte calcule l'empreinte depuis cette même copie du journal. Il s'agit de traces d'acceptation, pas de commandes natives actives ou exécutables.

La cliente vérifie session, sauvegarde, époque, séquence et structure, puis l'empreinte des données annoncées avec ce journal avant toute application au monde. Après application, elle compare les données réellement relues et leur état temporel. Seulement si ces vérifications réussissent et si l'autorisation reste valable, elle remplace atomiquement le journal, son curseur global et son empreinte. Une réception tardive ne doit donc ni manquer dans le journal confirmé, ni déclencher une deuxième commande.

Une application du monde qui ne correspond pas à la référence produit un accusé d'échec sans valider le journal ni le curseur. Cette opération n'est pas une transaction restaurant automatiquement toutes les anciennes valeurs du monde : la protection et la récupération restent nécessaires après échec.

Le schéma d'empreinte 18 lie également l'identité de commande, la session et la sauvegarde aux traces. Le nouveau champ réseau justifie le protocole 58 ; les deux joueurs doivent utiliser le même paquet.

## Limites à ne pas confondre

- La frontière des événements effectivement appliqués introduite en 0.4.16.33 reste obligatoire ; aucun événement durable manquant n'est sauté.
- Ce commit de métadonnées concerne uniquement les snapshots ordinaires en phase stable. Il ne certifie ni le graphe mécanique, ni les actions actives, ni la permission de reprendre ou sauvegarder après une reconstruction.
- La restauration native du journal reste distincte : en phase protégée, elle ne fait avancer aucun curseur et ne remplace pas le futur Commit de reconstruction.
- La compilation standard conserve son parcours de snapshot sans ce journal obligatoire ; le schéma d'empreinte est commun aux compilations.

## Preuves hors jeu

Les compilations standard et prototype réussissent chacune **1 207 tests** : 33 traduction, 140 réseau, 1 034 principaux ; 233 clés françaises vérifiées. Parmi les 24 nouveaux cas : codec du snapshot, identité des traces dans le hash, autorisation du commit et scénarios d'acteurs avec confirmation précoce/tardive/dupliquée, journal divergent, historique borné et données invalides. Les scénarios d'acteurs utilisent un service de combat simulé, pas un monde Unity.

Le diagnostic Mono du paquet exact réussit une restauration synthétique du journal sans avancement du curseur natif, puis un commit de métadonnées séparé sur un snapshot stable. Il n'exécute aucune commande native ni aucun chargement de monde. Sa configuration de dépendances est autonome : elle ne prouve pas la résolution des dépendances par le chargeur du jeu.

Les sept contrôles du protocole du candidat exact réussissent.

- Empreinte standard : `57FB9C5AADA95EAA9AC02786EF6AFB3AECD3D4118D2D206118D107273F9538DC`.
- Empreinte du candidat : `D3A27FC254B5405EF3FE9D73791AF22110346D2A94E592F134B98CF18C6B6734`.
- SHA-256 DLL principale : `20E8F3F3DDFB7AB82F2011E3DA2DCF89548864C7C5E768F5E6A30905FF60F010`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint D3A27FC254B5405EF3FE9D73791AF22110346D2A94E592F134B98CF18C6B6734
& .\tools\check-package-protocol.ps1 -CandidateFingerprint D3A27FC254B5405EF3FE9D73791AF22110346D2A94E592F134B98CF18C6B6734
```

## Suite et sécurité

Restent les actions natives actives et leurs contrôleurs, la cohérence mécanique complète, le Commit de reconstruction, son raccordement à la récupération automatique, la reconnexion, le repli jouable et la couverture de campagne. Le rendu et WOTR_AIVO nécessiteront une preuve moteur ultérieure, uniquement sur autorisation.

Aucun jeu lancé, paquet installé ou activé. Stable 0.4.16, sauvegardes originales et production de voix préservés. `runtimeValidated=false`, aucun marqueur `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper NU1903 préexistant non résolu. Ce candidat n'est pas une version finale validée en jeu.
