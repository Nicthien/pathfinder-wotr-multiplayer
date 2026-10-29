# Changement de mode et fin de combat en solo — 0.4.16.48

Protocole 66 inchangé ; installer néanmoins le même ZIP .48 sur les deux PC.

## Incident vérifié

Journal hôte du 10 octobre : demande T vers le tour par tour à 18:09:40.862, demande supplémentaire refusée pendant la transition à 18:09:49.111, validation du mode tour par tour à 18:10:09.425. À 18:10:09.477, la capture de fin échoue avec `combat-end-requires-peers`, puis les déplacements et sauvegardes restent bloqués. Le retour au tour par tour et la validation de fin sont deux chemins distincts : le second a rendu le premier inutilisable.

## Corrections

- Une fin de combat commencée sans cliente vérifie l'état final local puis termine sans construire de barrière exigeant un correspondant. Ce chemin ne retire jamais un joueur déjà requis par la barrière ; une connexion survenue pendant la capture reste protégée.
- Pour une transition commencée seul, sans reconnexion en cours, l'hôte utilise le changement de mode du moteur sans attendre que toutes les attaques et tous les projectiles soient terminés. Pause manuelle conservée, référence de transition et contrôle d'application moteur conservés. Le contrôle d'absence de cliente est renouvelé au moment de la pause.
- T fonctionne dans les deux sens. Une nouvelle pression pendant une synchronisation, une fin de combat ou une pause forcée retient le dernier mode demandé, au lieu de l'abandonner. Une nouvelle pression inverse cette demande ; elle est appliquée lorsque la barrière actuelle est terminée. Notification française et traces `[CombatMode]`.
- Une demande ancienne ne traverse ni changement de session ni chargement de sauvegarde. Après la fin du combat, la demande retenue devient la préférence du prochain combat.
- La préférence choisie hors combat est également prise en compte au prochain combat lorsque l'hôte est seul.

## Limite importante à deux

Le raccourci peut être demandé pendant une action ou une synchronisation. Il ne force jamais un changement local qui laisserait la cliente dans l'autre mode. **À deux, la barrière existante continue d'attendre les actions/projectiles et les confirmations réseau.** L'application instantanée dans tous les cas, notamment pendant une action figée par une pause, n'est pas garantie par ce correctif ; une récupération définitivement arrêtée reste protégée et ne peut pas être ignorée en appuyant sur T. La demande retenue n'est pas une promesse de récupération d'un état moteur endommagé.

Les rejets `UnknownUnit` observés dans le même journal ne sont pas supprimés ici ; ce correctif cible le changement de mode et l'échec solo précis, pas toutes les anomalies RTwP.

## Vérification

Compilation Release et structure/empreintes du ZIP uniquement. Aucun lancement du jeu, aucune suite de tests ni installation. Aucune modification du mod stable, des sauvegardes originales ou du doublage. La vulnérabilité connue AutoMapper 10.1.1 reste signalée par la compilation.

Paquet : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.48-UMM.zip`.

Empreinte du candidat : `CED3A484327B6C83B4417C3BF2C906BDF6BC119EC60BFACA03BC5ACFFC044B9A`.

SHA256 ZIP : `42895B84E70BD47B0187313DE89AFA1A264FC3A8F445EC4F7A8FD59ED7A2534B`.

À confirmer manuellement : hôte seul, T dans les deux sens en combat et en pause, plusieurs pressions pendant une transition, fin de combat puis déplacements/sauvegarde ; ensuite mêmes scénarios à deux, avec doublage actif. Ne pas présenter cette version comme validée en jeu.
