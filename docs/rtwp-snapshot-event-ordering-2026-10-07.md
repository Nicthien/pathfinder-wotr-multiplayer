# Rattrapage des états sans abandonner les événements autoritaires

7 octobre 2026 — variante 0.4.16.33, protocole 57 inchangé, empreinte de valeurs schéma 17. Campagne de base sans DLC.

## Défaut corrigé dans le chemin existant

Un état périodique pouvait annoncer une séquence d'événements supérieure à celle réellement appliquée par la cliente. Le chemin précédent appliquait cet état, appelait `TickRealTimePresentation(false)` puis avançait directement l'inbox jusqu'à la séquence annoncée. Un événement de début d'action pouvait ainsi être abandonné et les handles d'animation déjà actifs libérés. Un résultat durable livré après son état de référence pouvait aussi être écarté ou, selon l'ordre des messages, écraser un état plus récent.

Ces appels de rattrapage aveugle sont supprimés du chemin des snapshots ordinaires. Une référence complète restaurée et un snapshot périodique ne sont pas considérés comme des opérations interchangeables.

## Parcours désormais raccordé

- Le snapshot est lié à la session, au chargement et à l'époque de sa voie d'événements.
- Sa frontière `IncludedEventSequence` doit correspondre à la séquence **effectivement appliquée**, pas seulement reçue ou mise en file.
- S'il arrive avant ses événements, une seule copie indépendante du snapshot le plus récent est conservée. Aucun état de jeu, hash de réception ou curseur n'est modifié pendant cette attente.
- Quand les événements réussissent et sont committés dans l'inbox, le snapshot prêt repasse par le chemin normal de validation après libération de la porte asynchrone. Sa frontière est vérifiée une nouvelle fois, notamment si un autre événement le dépasse entre-temps.
- Un snapshot dont la frontière précède un résultat déjà appliqué est ignoré ; il ne peut pas rétablir des valeurs anciennes.
- Doublon contradictoire et régression de frontière dans un snapshot plus récent sont refusés. Les collections reçues sont bornées en nombre avant copie ; ce garde-fou n'est pas présenté comme une limite complète de taille de tous les champs.
- Une attente sans événement pendant plus de cinq secondes utilise le signalement de défaut existant et la pause protégée. Les snapshots ou doublons suivants ne prolongent pas ce délai. Une pause protégée reste une protection, pas une récupération réussie.
- Changement de session/chargement/époque ou réinitialisation du client abandonne l'ancienne attente.

Les snapshots ne peuvent plus être appliqués ni acquittés pendant transition, réparation ou clôture du combat. Le contrôle est présent à l'entrée, dans l'autorisation du dispatch, après son résultat et dans le chemin d'exception. Les données d'une ancienne opération ne doivent pas interférer avec la reconstruction native protégée.

Aucune nouvelle catégorie de message ni aucun nouveau champ réseau : protocole 57 conservé. La variante et son empreinte changent ; les deux joueurs doivent toujours utiliser le paquet exact identique. Le chemin de présentation reste une option de compilation. Les contrôles de phase protègent aussi la compilation standard.

## Preuves hors jeu

Les deux compilations réussissent **1 183 tests chacune** : 33 traduction, 139 réseau et 1 011 principaux ; 233 clés françaises vérifiées.

31 nouveaux cas principaux :

- 18 cas du garde-fou : frontière exacte, attente, propriété de la copie, remplacement par le dernier snapshot, doublons, régression, délai non renouvelable, états étrangers ou invalides ;
- 13 cas d'acteurs : snapshot avant début d'action/résultat durable, ordre réel événement puis snapshot, absence d'arrêt d'animation et de commande locale, état ancien après résultat plus récent, événement refusé, perte sans événement ultérieur, changement d'époque, dispatch révoqué par trois phases de barrière avec/sans exception et événement asynchrone non encore terminé.

Les acteurs emploient un service de combat simulé. Ils prouvent l'ordre des appels et les protections, pas le rendu d'une animation Unity ni les PV d'une partie réelle.

Le diagnostic Mono du candidat exact réussit également le nouveau scénario DTO/queue : attente jusqu'à la bonne frontière, copie isolée, expiration et rejet d'un état ancien. Aucun handle d'animation natif, commande, règle ou monde n'y est exécuté. Les limites de configuration autonome et de résolution des dépendances du [bilan précédent](rtwp-accepted-order-reference-2026-10-07.md) restent applicables.

Sept contrôles du protocole du candidat exact réussissent. `git diff --check` ne signale pas d'erreur de contenu.

## Paquet exact et reproduction

- Build standard : `4DF05B4EFC2EFDA4E057A162E2A59C8133C6B3C14734D1B16A0D77C5F1E6D7BF`.
- Candidat prototype : `9E9AE0E932E061C571156D54BC92729595A18978C96321579385C23D9284DB5C`.
- DLL principale SHA-256 : `A1936A6DB9CDB69416BF0B0589E30D79D3EE95F21BAFE56F194D88A0ED313313`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 9E9AE0E932E061C571156D54BC92729595A18978C96321579385C23D9284DB5C
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 9E9AE0E932E061C571156D54BC92729595A18978C96321579385C23D9284DB5C
```

## Audit d'animation et suite

La lecture du code natif a confirmé que `AnimationActionHandle.StartInternal` réinitialise son temps, que `AnimationManager.Execute` peut démarrer immédiatement ou mettre en file un handle et que la présentation d'un sort change de clip entre précast et lancement. Il ne faut donc pas reconstruire une animation en rejouant simplement son ancien événement Start. Le temps du handle, la phase/clip, les indicateurs d'impact et les callbacks associés exigent un traitement explicite ; aucune méthode d'animation n'a été invoquée pendant cet audit.

Cette correction préserve l'ordre du flux courant et supprime un arrêt artificiel identifiable dans le code. **Elle ne restaure pas les actions encore actives après un rechargement natif**, ne certifie pas les contrôleurs et ne termine ni Commit/Abort, ni reconnexion, ni réparation automatique, ni repli jouable. Les traces d'acceptation de l'incrément précédent ne remplacent toujours pas ces actions actives. L'interaction entre arrivées tardives d'acceptations de commandes et snapshots reste à auditer séparément.

Un événement manquant ou inapplicable déclenche la protection existante ; en particulier, les créations d'entités et leurs dépendances doivent encore être couvertes pour la campagne complète. Des snapshots anciens ignorés ne reçoivent pas de faux accusé de réussite et peuvent contribuer au diagnostic de pertes existant.

Aucun jeu lancé ; aucune installation ou activation ; aucune sauvegarde originale, production de voix ou dossier WOTR_AIVO modifié. Stable 0.4.16 et ancien slot expérimental désactivé préservés. `runtimeValidated=false`, aucun `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper NU1903 préexistant non résolu. Le chantier reste incomplet.
