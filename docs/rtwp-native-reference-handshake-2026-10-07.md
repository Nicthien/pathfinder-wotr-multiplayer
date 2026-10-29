# Vérification réseau de la référence après reconstruction native

7 octobre 2026 — variante 0.4.16.28, protocole 55, empreinte de combat schéma 17. Campagne de base sans DLC.

## Avancement concret

Le parcours interne de reconstruction attend maintenant deux reçus distincts : `Loaded` constate la fin du chargement ; `ReferenceVerified` constate que les données de combat restaurées correspondent à la référence liée au checkpoint. Aucun des deux ne constitue une autorisation de reprise ou de sauvegarde.

L'hôte conserve la référence avant capture et vérifie qu'elle n'a pas changé après capture, puis avant vérification. Le message `Verify` contient cette référence sérialisée, bornée à 8 Mio, protégée par SHA-256 et liée à l'identité de session, sauvegarde, époque, checkpoint, paquet et séquence incluse. Le décodeur refuse les formats invalides et borne les nombres d'unités, de zones et de projectiles.

La cliente n'accepte la référence qu'après réception de l'archive exacte et chargement terminé. Elle appelle la restauration stricte sur les objets natifs existants, puis répond avec l'empreinte canonique des données et celle du contenu reçu. Les doublons renvoient le reçu mémorisé sans recommencer l'application. Une référence différente pour le même checkpoint est refusée. L'hôte vérifie le pair, l'identité, les étapes et les deux empreintes ; un désaccord provoque l'abandon protégé.

Les anciens messages de réparation `Apply`, `Verify`, `Fallback` et `Resume`, ainsi que leurs accusés côté hôte, ne peuvent plus faire avancer l'autre barrière pendant la reconstruction native possédée. Cette protection ne remplace pas le futur coordinateur d'échec/reconnexion.

## Preuves hors jeu

- Les deux compilations passent chacune **1 024 tests** : 33 traduction, 89 réseau et 902 principaux. Les 233 clés françaises restent vérifiées.
- Dix scénarios d'intégration vérifient les deux acteurs réels du mod avec transport et service de combat simulés : reçu perdu puis retransmis sans double application, mauvais pair, empreintes divergentes, restauration divergente, format invalide et messages hérités bloqués. Le chargement est représenté par un reçu synthétique ; aucun monde Unity n'est construit.
- Neuf tests du codec couvrent notamment les limites, le format, les copies indépendantes et la conservation des données de brûlure du cinétiste.
- Un essai TCP local existant a été étendu pour transporter une référence de plus de 70 Ko et son reçu distinct. Il valide le transport, pas la restauration mécanique ni l'empreinte d'un combat réel.
- Le diagnostic Mono du paquet exact réussit sur les objets synthétiques existants. Il ne lance ni capture ni chargement d'un monde natif.
- Sept contrôles du protocole 55 réussissent sur le candidat exact. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

La première compilation de cet incrément a échoué sur deux noms de types ambigus dans le test TCP. Ils ont été pleinement qualifiés avant les deux validations complètes ci-dessus.

## Paquet exact

- Build par défaut : `8F5BE400E67D75B61D0C7656EE308A13860FC6F55DC8A85FB9C39584A2C094FF`.
- Candidat prototype : `EFC6B01E4DCBD46513A92918FA3341AFA984BF4BC0A526AFC5BEE2D3364A33B9`.
- DLL principale SHA-256 : `5BB6602DE0DF42F5920FBD1299D01F8140C92BA78081F94017C4C5EB70F64037`.
- Emplacement : `local-artifacts/a0-candidates/EFC6B01E4DCBD46513A92918FA3341AFA984BF4BC0A526AFC5BEE2D3364A33B9`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint EFC6B01E4DCBD46513A92918FA3341AFA984BF4BC0A526AFC5BEE2D3364A33B9
& .\tools\check-package-protocol.ps1 -CandidateFingerprint EFC6B01E4DCBD46513A92918FA3341AFA984BF4BC0A526AFC5BEE2D3364A33B9
```

## Ce qui interdit encore une reprise

1. Vérifier les composants, références, contrôleurs mécaniques et callbacks natifs de chargement. Une égalité des données répliquées ne certifie pas le graphe mécanique complet.
2. Restaurer et vérifier le journal des ordres et les séquences sans exécution mécanique cliente, puis terminer `Commit/Abort` et ses accusés. Le marqueur d'obligation mécanique reste présent.
3. Raccorder le parcours complet à la réparation automatique, puis traiter échecs, reconnexion et retour jouable au tour par tour ou sortie sûre. Une pause permanente n'est pas une récupération.
4. Valider les scénarios de campagne et le doublage sur deux véritables instances lorsque le lancement du jeu sera autorisé.

Le paquet est préparé hors installation, mais n'est pas une version finale validée. `runtimeValidated=false`. Aucun jeu lancé, aucune activation, aucune sauvegarde originale ou production de voix modifiée. Stable 0.4.16 et ancien slot expérimental désactivé conservés. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` créé.
