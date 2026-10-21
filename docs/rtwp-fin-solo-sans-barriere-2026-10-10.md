# Fin de combat solo sans barrière — 0.4.16.49

Protocole 66 inchangé. Correctif ciblé après le nouvel essai de Nicolas avec la .48.

## Preuve du journal

Le journal charge bien la .48. Les clics de déplacement sont refusés par la barrière à partir de 18:36:30.215 et encore à 18:36:48.163. T est conservé à 18:36:47.312, phase Stable, pendant la validation de fin. La fin est finalement confirmée localement à 18:36:49.949, après quoi des ordres de déplacement sont envoyés ; ce journal ne montre donc pas un blocage permanent. L'erreur `combat-end-requires-peers` n'est plus présente.

La .48 avait corrigé la validation sans cliente seulement **après** l'attente du point sûr. Elle créait encore `CombatEndPending` et bloquait les entrées pendant le drainage des actions/projectiles, même sans replica à synchroniser. Le contrôleur inspecte tous les projectiles de la scène ; ce journal ne permet pas de désigner celui ou la commande qui a prolongé l'attente.

## Correction

Quand la notification moteur de fin arrive et que l'hôte est seul, sans reconnexion en cours et sans barrière déjà active, le mod suit directement son chemin normal de fin de combat. Il ne crée plus de barrière réseau ni d'attente de projectile pour un correspondant absent. Les commandes et sauvegardes suivent les conditions habituelles de fin ; aucune fin artificielle n'est provoquée simplement parce qu'un ennemi semble mort.

La vérification à deux reste inchangée. Un joueur déjà requis ne disparaît jamais de la barrière au motif qu'il s'est déconnecté. Le code solo tardif de la .48 est conservé pour les états déjà engagés sans correspondant. Les transitions T de la .48 restent incluses, avec leur limite d'attente sûre à deux.

## Vérification et limites

Compilation Release et contrôle du ZIP uniquement, aucun jeu lancé ni test exécuté. Aucun changement du stable, des sauvegardes originales ou du doublage. Avertissement de compilation AutoMapper 10.1.1 connu et inchangé.

Paquet : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.49-UMM.zip`.

Empreinte : `3C498A1A982447C175568652DF07F861EAE702E9274E77D23EB50E3FF2947D53`.

SHA256 ZIP : `09AE331DACB7C7536B5611E9526057A6226488078FB9FD816EBD6F1B3B562E3C`.

À vérifier en jeu : fin de combat solo suivie immédiatement d'un déplacement, sauvegarde hors combat, T dans les deux sens, puis fin de combat à deux. Ce correctif n'affirme pas que tous les blocages RTwP ont disparu.
