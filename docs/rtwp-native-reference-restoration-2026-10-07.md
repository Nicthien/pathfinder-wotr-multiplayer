# Restauration des données de référence après chargement natif

7 octobre 2026 — variante 0.4.16.27, protocole 54, empreinte de combat schéma 17. Campagne de base sans DLC.

## Problème concret corrigé

`RealTimeEncounterParticipants` conserve les unités mortes ou sorties du combat afin que leurs résultats restent synchronisés. Un chargement natif remplace leurs objets sans changer l'identité réseau du combat. Le registre conservait donc les anciens objets ; un échantillonnage du nouveau monde pouvait rencontrer `conflicting-encounter-participant` ou conserver une référence obsolète.

Le registre dispose désormais d'une réaffectation atomique depuis l'ensemble autoritaire des identifiants. Toutes les unités sont résolues avant publication, y compris mortes ou retirées. Identifiants vides/dupliqués, collisions de casse, unités absentes, révocation, dépassement de limite et modification concurrente sont refusés. La transaction de réaffectation peut être annulée, sans écraser un propriétaire ultérieur.

Dans le chemin de restauration après chargement, la nouvelle table complète est conservée dès sa publication : un échec ultérieur d'application des données ne doit pas réintroduire les objets détruits de l'ancien monde. L'obligation mécanique et la pause restent en place.

## Entrées ajoutées

- `MultiplayerClient.RestoreLoadedNativeReferenceAsync` exige le reçu `Loaded` de l'opération actuelle, le manifeste exact et une référence dont l'empreinte correspond à celle de la capture. Il prend une copie indépendante du snapshot, protège la restauration contre les appels concurrents et revérifie le contexte après l'attente.
- `ICombatInteractionService.TryRestoreNativeReferenceStateAsync` exige le monde courant et son propriétaire d'obligation exact, la fin du chargement natif, une pause sûre, une interface personnage inactive et l'absence de contexte de croisade. Il rejette les répliques de buffs et de zones avant la restauration.
- La restauration réutilise l'application des valeurs confirmées : caractéristiques/caches, PV, ressources, emplacements de sorts, données des buffs existants, positions, engagements et zones. Son précontrôle strict interdit de remplacer une entité mécanique absente par une représentation de présentation. La référence est rééchantillonnée, comparée par empreinte et différences temporelles.

**Cette entrée n'est pas encore appelée par un message réseau de vérification ni par la réparation automatique.** Aucun marqueur de graphe mécanique n'est levé, aucune séquence n'est validée, aucun ordre n'est rejoué et aucune reprise n'est autorisée par son résultat. L'empreinte comparée ici porte sur les données de combat, pas encore sur le journal complet des ordres acceptés.

## Cinétiste

L'application de la brûlure acceptée envoyait des notifications mécaniques, également sur le client de présentation. Elle applique maintenant la valeur sans ces notifications lorsque `IsClientActive` est vrai ; le chemin ordinaire conserve ses notifications. Les métadonnées du jeu installé montrent que le setter d'`AcceptedBurn` délègue à `UnitPartKineticistBurnHolder.CurrentBurn`, dont le setter est une écriture de backing field. Un holder absent provoque une erreur explicite.

La brûlure et la présence du composant n'étaient pas incluses dans l'empreinte du combat. Elles le sont désormais : une brûlure différente, ou un composant absent au lieu d'une valeur nulle, ne peuvent plus passer cette comparaison. D'où le schéma 17 et le protocole 54.

## Preuves hors jeu

Les deux variantes compilées passent chacune **1005 tests** : 33 traduction, 80 réseau, 892 principaux. Vingt-deux tests supplémentaires couvrent la réaffectation, les références anciennes/incomplètes, la révocation pendant restauration, le refus d'une zone mécanique absente, la conservation des protections, les notifications du cinétiste et sa divergence d'empreinte.

Les tests de registre utilisent des objets synthétiques. Les essais du setter de brûlure utilisent des objets natifs non initialisés, un holder synthétique et des callbacks de test ; aucune unité du jeu n'est créée. Les essais de l'entrée cliente utilisent un service de combat simulé. Les refus précoces du service réel sont testés avant tout accès au monde. **Aucun essai ne démontre encore l'application complète sur un monde Unity rechargé.**

Le premier essai de zone absente a été corrigé pour que le faux service retourne réellement `null`, au lieu d'un objet factice automatique. Les deux essais de brûlure ont été corrigés pour fournir leur holder synthétique, après vérification du setter natif. Les validations finales ci-dessous sont postérieures à ces corrections.

- Build par défaut : `8028034174CA7F0A882E2382B8F83A045285CCB5B1BCF9393C87179A32A7C146`.
- Candidat prototype : `7A3DD3080AEE4E8EAAC6A438983FACA8096AD91E28E99DA862E2322F7DEB3212`.
- DLL principale SHA-256 : `48565B18D4F00291F747A763C96A9791942F73E126DAAF4CD49670F6D9B2A753`.
- Diagnostic Mono du candidat exact réussi sur les objets synthétiques des protections existantes ; aucune reconstruction de monde invoquée.
- Sept contrôles de protocole 54 réussis sur le candidat exact. Les 233 clés françaises restent vérifiées. Avertissement AutoMapper `NU1903` préexistant non résolu.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 7A3DD3080AEE4E8EAAC6A438983FACA8096AD91E28E99DA862E2322F7DEB3212
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 7A3DD3080AEE4E8EAAC6A438983FACA8096AD91E28E99DA862E2322F7DEB3212
```

## Suite obligatoire avant reprise

1. Ajouter le message de référence/vérification lié au checkpoint exact et raccorder ce résultat aux accusés hôte/client. Empêcher les anciens messages/accusés de réparation de faire avancer l'autre barrière pendant une reconstruction native, pas seulement suspendre leurs délais.
2. Auditer et vérifier les composants, références et contrôleurs mécaniques reconstruits, ainsi que les callbacks natifs de chargement. Une simple égalité des valeurs visibles ne suffit pas à lever l'obligation mécanique ou à autoriser le tour par tour.
3. Restaurer le journal des ordres et les séquences incluses sans exécution mécanique cliente, puis exécuter la barrière Commit/Abort. Ne brancher la réparation automatique qu'avec ces garanties.
4. Traiter échec/reconnexion/repli et sortie sûre sans assimiler une pause permanente à une récupération ; valider ensuite dans deux véritables instances lorsque le jeu sera autorisé.

Aucun jeu lancé, aucune installation/activation, aucune sauvegarde originale ou production de voix modifiée. Stable 0.4.16 conservé, ancien slot expérimental toujours désactivé. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json`. Le chantier et la validation moteur restent incomplets.
