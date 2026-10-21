# Sauvegarde de combat TB — état réel au 18 octobre 2026

## Statut

### Délai de chargement multijoueur corrigé et reprise vérifiée — 0.4.16.99

- Le nouvel échec observé avec la 0.4.16.98 venait d'un délai trop court côté hôte, pas de l'archive. L'hôte avait fini sa reconstruction vers 21 h 55 min 20 s, puis annulait la restauration à 21 h 56 min 16 s après 60 secondes. Sur la seconde instance de la même machine, le chargeur natif n'atteignait `OnAreaLoaded` et `OnGameLoaded` qu'à 21 h 56 min 28 s, soit environ douze secondes après cette annulation. Le message « le combat sauvegardé n'a pas pu être restauré sur les deux PC » était donc cohérent avec les traces.
- La 0.4.16.99 porte le délai de la seule phase de chargement/restauration à trois minutes. Les barrières `RestorePrepare`, `RestoreCommit` et `RestoreComplete`, les contrôles de session, d'époque, de séquence et de digest restent obligatoires ; aucune divergence n'est transformée en réussite. Un test de régression vérifie que ce délai dépasse celui admis pour le chargeur natif.
- Le candidat exact `65A8BD24C21A8C32E3BF48B325598149B51D81B2FA84B38A6DC26549613C8922` a été copié dans les deux installations isolées, sans cache de DLL résiduel. DLL SHA-256 : `B2C398F6CB8A13C2CF56B5594C56DDD3A6AD88BC85C310CF2C2C731632E5BF22`.
- Essai réel à deux instances le 18 octobre, sur la même machine : l'hôte a ouvert `Multi - Combat - 2026-10-18 21-06-26 - 2eaa06e2b3ac42d7b1ebaa6774c5a3e0.zks`, le client s'est connecté à `127.0.0.1:1024`, puis les deux mondes ont restauré le personnage actif `e948bc11-116e-4821-98f4-3fd887973261`, le round 1 et le combat actif. L'hôte a reçu les trois confirmations du client. Les deux journaux ont ensuite écrit `Both worlds verified; restoration barrier released and combat resumed` à 22 h 08 min 50,369 s et 22 h 08 min 50,590 s.
- Les deux fenêtres affichaient le même combat repris, sans modale d'échec. Une surveillance supplémentaire n'a relevé ni `RestoreFailed`, ni `Restoration stopped`, ni exception, ni désynchronisation. Les deux processus ont ensuite été fermés proprement. Cet essai confirme donc le chargement hôte-client local ; il ne constitue toujours pas une validation sur deux PC physiques.
- Vérifications Release du build exact : **1 213 tests principaux, 160 réseau, 33 localisation et 270 localisation française réussis**, plus les six contrôles du protocole 74. L'unique avertissement restant est l'avis de vulnérabilité AutoMapper déjà connu.
- Paquet d'essai : `local-artifacts/livraisons/WotR-RTwP-Essai-0.4.16.99-65A8BD24C21A.zip`, SHA-256 `7B8D3A455FAE770EA874D39EED50D222723C6482A1E55C9A1AA236AC78B43F44`.
- La 0.4.16.99 est installée dans le seul dossier expérimental de l'installation habituelle, jeu fermé. Les **184 fichiers** du candidat correspondent exactement ; `Settings.xml` et les journaux ont été préservés, et aucun ancien cache de DLL n'a été repris. La 0.4.16.96 complète est récupérable sous `local-artifacts/backups/20261018-221246-normal-install-0.4.16.96`. Aucun slot stable ou doublon expérimental n'est actif.
- Aucun fichier `local-artifacts/RTWP_RUNTIME_VALIDATED.json` n'a été créé : la preuve actuelle couvre un hôte et un client réels dans deux installations et deux profils isolés sur une même machine, pas deux machines physiques ni toute la matrice de combat.

### Sauvegarde, chargement et reprise synchronisée sur deux instances — 0.4.16.96

- Essai réel effectué le 18 octobre avec deux installations et deux profils isolés sur la même machine : une instance hôte et une instance cliente réellement connectées. Il ne s'agit pas de deux PC physiques.
- Le candidat exact `6CC97D808D54FA135AE5715C5CC7DE4FA2EAD62BBBBFAC0E6E5C2051781BA138` a d'abord chargé sur les deux instances l'archive de combat 0.4.16.93 connue. La migration de cette archive est explicitement bornée à son empreinte ; les contrôles de protocole, version du jeu, schéma, intégrité et digest restent obligatoires.
- Une nouvelle sauvegarde a ensuite été créée en plein combat : `Multi - Combat - 2026-10-18 19-54-49 - 4a8fd9ac30fd4313876cf62a94e79b1b.zks`, 1 441 040 octets. L'opération `30d3edf298a6455f85b8c13eff168ae9` a obtenu la préparation du client et la même empreinte mécanique `9319DC7AA2638AA4753B2AF6C0A129721427C8C8EF6D3BC78E96F0F7EE26ACBB`, puis les deux journaux ont confirmé `Result=saved`.
- Cette nouvelle archive 0.4.16.96 a été chargée depuis le menu de l'hôte. L'hôte et le client ont tous deux restauré le personnage actif `CBU_1461302556_1`, le round 1 et le combat actif. L'hôte a reçu les confirmations `RestorePrepare`, `RestoreCommit` et `RestoreComplete`, puis les deux côtés ont journalisé `Both worlds verified; restoration barrier released and combat resumed` à 19 h 56 min 33,999 s et 19 h 56 min 34,157 s.
- La reprise n'a pas seulement été observée dans les journaux : après le chargement, l'hôte a déplacé l'unité active Ember. À 19 h 59 min 58,944 s, il a envoyé `NotifyUnitMovedTo` vers `(-62.982, 40.103, -42.349)` avec une limite de cinq pieds ; le client a reçu exactement cette commande à 19 h 59 min 58,969 s et l'a exécutée. L'état affiché et la position du groupe concordaient sur les deux instances.
- Deux défauts découverts pendant cet essai ont été corrigés sans relâcher les protections. La capture de l'hôte calcule maintenant son digest après le gel confirmé du client, et la vérification mécanique native en mode TB emploie la même projection sûre des contextes de buffs que la sauvegarde, tout en conservant la capture stricte en RTwP.
- Vérifications Release finales : **1 208 tests principaux, 160 réseau, 33 localisation et 270 localisation française réussis**, plus les six contrôles de paquet. L'unique avertissement restant est l'avis de vulnérabilité AutoMapper déjà connu. DLL SHA-256 : `13778C04A06C691E9142426A971FF211EF3531DAED9C1E3FEF498E194F5B24AE`.
- ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.96-UMM.zip`, SHA-256 `ADE9962B19D16503FA25D682B64FD929080D6496BF725F66911EB48E379F210D`.
- La 0.4.16.96 est installée dans le seul slot expérimental de l'installation habituelle, jeu fermé. Les 184 fichiers du candidat correspondent exactement ; `Settings.xml` et les journaux locaux ont été préservés. La 0.4.16.93 complète est récupérable sous `local-artifacts/backups/20261018-200200-normal-install-0.4.16.93`. Le slot stable reste absent.
- Aucun fichier `local-artifacts/RTWP_RUNTIME_VALIDATED.json` n'a été créé : cette preuve couvre la sauvegarde, le chargement et une commande synchronisée sur deux instances locales isolées, pas une validation sur deux machines physiques ni toute la matrice RTwP.

### Reprise jouable confirmée sur l'hôte — 0.4.16.93

- Essai réel effectué le 18 octobre sur l'installation hôte isolée avec le candidat exact `2EB06FB0790AFBB70FCC423886651C2DD3D03E1F75B588F9E101E5F90206E0EA`. La sauvegarde `Multi - Combat - 2026-10-18 13-12-08 - a50f16b7cbab41859a33506553c7e2cf.zks` a été créée en plein combat au tour par tour, puis chargée depuis le menu du jeu.
- Le chargement a restauré le tour natif, attendu la fin réelle des déplacements déclenchés par le chargeur, réappliqué les champs de combat que le moteur efface après `OnGameLoaded`, vérifié les deux représentations du monde puis libéré automatiquement la barrière de restauration. Aucun message d'échec n'est apparu et aucune reprise manuelle n'a été nécessaire.
- La jouabilité a été contrôlée après le chargement : Ember s'est déplacée, la fin de son tour a déclenché le tour de l'ours sanguinaire, puis l'ours s'est déplacé avec sa ressource de mouvement passant de 80/80 à 72/80. Les journaux de cette séquence ne contiennent ni `NullReferenceException`, ni échec de `StartTurn`, ni arrêt de restauration.
- Les divergences finales provenaient de données que le chargeur reconstruit différemment sans changer l'état mécanique : horodatage des zones permanentes, précision centimétrique des positions, puis champs d'IA effacés après `OnGameLoaded`. La projection de restauration ignore désormais l'horodatage non mécanique et emploie la même troncature décimale que l'archive ; le supplément natif n'est réappliqué qu'après stabilisation complète du chargeur.
- Vérifications Release complètes avec `HostPresentationPrototype=true` : **1 204 tests principaux, 160 réseau et 33 localisation réussis** ; localisation française **270/270**, protocole 74 et six contrôles de paquet réussis. L'unique avertissement restant est l'avis de vulnérabilité déjà connu sur AutoMapper.
- DLL SHA-256 : `A4E9D6F3F93976A113069DA0F308169B7262BA2C7FDC66F6E0D545A947C81E42`. ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.93-UMM.zip`, SHA-256 `EAA89716CA7A8237F90B2DEB9F8356FD09093A6B1DF9A2CE4F29D0D7EE0ED9E1`.
- La 0.4.16.93 est installée dans le seul dossier expérimental de l'installation habituelle, jeu fermé. Les 184 fichiers correspondent au candidat ; `Settings.xml` et les journaux ont été préservés. La 0.4.16.82 complète est récupérable sous `local-artifacts/backups/20261018-132105-normal-install-0.4.16.82`. Le slot stable n'a pas été activé.
- Cette preuve concerne volontairement **l'hôte seul**, conformément au test demandé. Elle ne vaut pas validation réelle sur deux PC et aucun fichier `local-artifacts/RTWP_RUNTIME_VALIDATED.json` n'a été créé ou falsifié.

### Vérification différée jusqu'à la fin réelle du chargement — 0.4.16.82

- L'essai moteur 0.4.16.80 franchit le contrôle du tour restauré, puis échoue à 23 h 17 min 14,436 s dans le calcul final du digest avec `native-reference-host-not-at-safe-point`. `OnAreaLoaded` commence la restauration à 23 h 17 min 14,304 s, alors que `OnGameLoaded` n'arrive qu'à 23 h 17 min 14,462 s et que la notification de combat actif suit à 23 h 17 min 14,491 s. Le digest inspectait donc la topologie native pendant que `LoadingProcess` possédait encore le monde.
- La restauration est désormais explicitement scindée : `OnAreaLoaded` reconstruit le contrôleur, le tour, l'état réseau et les générateurs en gardant le monde gelé ; le digest et les accusés de la barrière attendent ensuite à la fois `OnGameLoaded` et le retour effectif du cycle natif en combat au tour par tour. L'hôte n'avance aucun étage de restauration avant ces deux signaux. Un client qui reçoit un message trop tôt l'ignore ; l'hôte le réémet par le protocole fiable. La garde générale de capture native n'est pas affaiblie.
- Un test de régression conduit la vraie machine d'états et vérifie qu'aucun message n'est émis avant `OnGameLoaded`, puis que `RestorePrepare` est envoyé après l'autorisation. Quatre cas couvrent séparément les deux signaux du cycle de vie. Vérifications Release avec `HostPresentationPrototype=true` : **1 198 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis.
- La 0.4.16.82 accepte explicitement l'archive connue créée par la 0.4.16.79 ; les contrôles de protocole, version du jeu, intégrité, schéma et digest restent obligatoires. Candidat exact `7459F0B00C2518785188335463DD29A96E6D8E0D2589467D34A1B4EA12C24BC9`, DLL SHA-256 `B24AF3939ADD40BFE035BF92D74AF8A3FC4BB7000E6CA136AD34AC3EEC31E3CA`.
- La 0.4.16.82 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé ; les 184 fichiers du candidat correspondent exactement, et `Settings.xml` ainsi que les journaux ont été préservés. La 0.4.16.81 est récupérable sous `local-artifacts/backups/20261017-233052-normal-install-0.4.16.81`. ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.82-UMM.zip`, SHA-256 `7A493EDDF7701A244C4FAB4C7A75DC899669F0EEF8CEC94FFA65F423C67DF194`.
- La reprise jouable du tour reste à confirmer en moteur. Aucun marqueur de validation moteur n'a été créé.

### Contrôle du tour restauré avant la notification native de combat — 0.4.16.80

- L'essai moteur 0.4.16.79 publie correctement `Multi - Combat - 2026-10-17 23-05-00 - b7ec9356fb15427ba375b964b04166cc.zks`. Le précontrôle de chargement passe ; l'archive contient le personnage `UN_-1506828567_1`, le round 1, le statut `Acting` et aucune commande en cours.
- À 23 h 06 min 46 s, la reconstruction du contrôleur atteint son contrôle final puis échoue avec `combat-save-native-turn-not-ready:status=Acting;running=False`. Le journal montre que `OnAreaLoaded` appelle la restauration à 23 h 06 min 46,606 s, tandis que la notification native qui déclare le combat actif n'arrive qu'à 23 h 06 min 46,663 s. Le contrôleur reconstruit était contrôlé avec la règle d'une sauvegarde en combat déjà totalement réactivé, trop tôt dans le cycle de chargement.
- Le contrôle final de restauration valide désormais directement le monde gelé, l'identité du personnage, son statut, sa survie, ses commandes, son mouvement et sa monture, sans exiger cette notification ultérieure. La capture d'une nouvelle sauvegarde continue d'exiger un combat natif pleinement actif. Aucun appel à `Start`, `Prepare` ou `StartTurn` n'est ajouté.
- La 0.4.16.80 accepte explicitement l'unique empreinte 0.4.16.79 `6EBC1ECAD09B3C4E555EB5FD3A32C616FBC43D1DF743A13B54D4A7248C4939B8` pour le chargement. Cette migration est liée à la version cible 0.4.16.80 ; protocole, version du jeu, intégrité des fichiers, schéma et toutes les autres validations restent obligatoires. L'archive de 23 h 05 peut donc être retestée sans être recréée, à condition que chaque PC utilise la 0.4.16.80.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 193 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis. Candidat exact `A09C41F37AA2010E9DB6015E0DAC52ABB823AB1B5BBA96B5976D1454528E8EBF`, DLL SHA-256 `CD208C63A7F69A74BCE9387E5A12A5EE2F0F572734FB167AE22150925EBE6A46`.
- La 0.4.16.80 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé ; les 184 fichiers du candidat correspondent exactement. La 0.4.16.79 est récupérable sous `local-artifacts/backups/20261017-232200-normal-install-0.4.16.79`. ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.80-UMM.zip`, SHA-256 `FF9A79369FA3744944B67699C43FB36FA3E7B5B33DD9B7FC1B820D2749F608C7`.
- Le passage complet jusqu'à la reprise jouable du tour reste à confirmer en moteur. Aucun marqueur de validation moteur n'a été créé.

### Identifiants temporaires de la barre d'actions gelés pendant la sauvegarde — 0.4.16.79

- L'essai moteur 0.4.16.78 à 22 h 47 refuse la publication malgré une empreinte mécanique strictement identique et une autorisation encore valide. Les deux checkpoints de générateurs montrent sept compteurs avançant respectivement de 200, 150, 150, 50, 150, 150 et 200, sans aucun flux aléatoire modifié.
- Ces écarts valent exactement 50 fois une série fixe `(4, 3, 3, 1, 3, 3, 4)`. La cause est le constructeur de `AbilityData` appelé plusieurs fois par rafraîchissement de l'interface pendant les quatre secondes où la sauvegarde garde le monde en pause. Ces objets sont des représentations temporaires de capacités déjà connues ; ils ne correspondent ni à une nouvelle action ni à un changement du combat.
- Pendant la seule possession du monde par une opération de sauvegarde, le constructeur réutilise désormais le dernier identifiant déjà établi pour la même capacité logique. Aucun compteur n'est avancé ni remis en arrière. Un identifiant absent avant la barrière est refusé avec `combat-save-ability-id-not-established`, afin qu'une capacité réellement nouvelle ne puisse pas être admise silencieusement.
- La portée exclut explicitement la restauration et les protections de réparation. Les unités, faits, objets, armes, effets de zone, flux aléatoires, empreintes et contrôles de publication restent inchangés.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 191 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis. Candidat exact `6EBC1ECAD09B3C4E555EB5FD3A32C616FBC43D1DF743A13B54D4A7248C4939B8`.
- La 0.4.16.79 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé. Les 184 fichiers du candidat correspondent exactement ; `Settings.xml` et le journal local ont été conservés séparément des fichiers du candidat. La 0.4.16.78 complète est récupérable sous `local-artifacts/backups/20261017-231900-normal-install-0.4.16.78`. ZIP UMM vérifié : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.79-UMM.zip`, SHA-256 `A29686B29DD409FDAC2F532F82F5CC519CA1250FD52BB144F4C05B8EBCA51958` ; DLL SHA-256 `6451DD2F2CD890EB718317FCEB27433AD02FF5D6C0008F573EAF7042350A485F`.
- Le passage hors jeu ne prouve pas encore qu'une sauvegarde 0.4.16.79 est publiée, ni que son chargement reprend le combat. Aucun marqueur de validation moteur n'a été créé.

### Historique d'inefficacité de l'IA sans désérialisation de commandes — 0.4.16.78

- Essai moteur 0.4.16.77 à 22 h 28, hôte seul dans une session multijoueur : nouvelle sauvegarde publiée, contrôle préalable de chargement réussi et ancien faux mouvement absent. La restauration atteint ensuite l'état d'IA, puis échoue parce que `CombatSaveAiState` tente de désérialiser un `UnitUseAbility` terminé ; ce type du jeu ne possède aucun constructeur JSON utilisable.
- L'audit du moteur montre que `CombatAiData.ConsequentAttacksWithoutEffect` n'est lu que par `UnitImpatienceController`. Il sert à compter les attaques réussies restées sans effet et à remettre cette série à zéro lorsqu'une attaque ultérieure produit un effet. Il n'est ni nécessaire ni sûr de recréer les objets complets de commandes, dont le contexte d'exécution n'est d'ailleurs pas présent dans le JSON observé.
- La capture conserve désormais le nombre mécanique d'attaques inefficaces résolues. Elle refuse toute entrée encore en cours, tout projectile encore en vol et toute capacité dont le processus n'est pas terminé. Au chargement, des marqueurs inertes et terminés restituent exactement le compteur ; ils ne peuvent ni lancer une capacité ni une attaque, et la prochaine attaque efficace les efface comme l'historique natif.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 189 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis. Candidat exact `EF4B35698E9B73B4832F1FE817F7C86FDDD80E46961E0CBE5158912D7A86AAD0`, DLL SHA-256 `E4356BB9A7599644E6535693E5E1DB183362A6531304428F086879EFC01C9081`.
- La 0.4.16.78 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé ; les 184 fichiers correspondent au candidat. La 0.4.16.77 est conservée sous `local-artifacts/backups/20261017-223951-normal-install-0.4.16.77`. ZIP UMM vérifié : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.78-UMM.zip`, SHA-256 `705AFC058A134EB3727B89380BA1D7537E1A054E7367C73E132E289650B77C42`.
- Le chargement complet et la reprise du combat avec le build 0.4.16.78 doivent encore être confirmés en jeu. L'archive 0.4.16.77 précédente n'est pas réutilisable avec l'empreinte du nouveau build : créer puis charger une nouvelle sauvegarde 0.4.16.78. Aucun marqueur de validation moteur n'a été créé.

### Référence de mouvement après chargement natif — 0.4.16.77

- Essai moteur 0.4.16.76 dans l'installation habituelle, hôte seul dans une session multijoueur : la sauvegarde `Multi - Combat - 2026-10-17 22-15-16 - 5181875f3aba43f8ad79a5e0935f393f.zks` est bien publiée et son contrôle préalable de chargement réussit.
- La restauration est ensuite refusée par `combat-save-turn-restore-mismatch` alors que l'unité, le round, le statut `Preparing`, l'absence de commande active, la vue et la monture sont corrects. Le seul indicateur fautif est `HasMotionThisTick` sur le personnage actif.
- Preuve dans l'archive : `party.json` conserve `Position` et `PreviousPosition` sous la forme native arrondie `2500|3500|-6551`, tandis que le supplément du mod avait enregistré la référence non arrondie `(25.0079384, 35.0027466, -65.5179749)`. Le moteur définit `HasMotionThisTick` par l'inégalité exacte entre `Position` et `PreviousPosition`; réinjecter la valeur plus précise après le chargement natif crée donc un faux mouvement.
- La capture refuse déjà toute unité réellement en mouvement. À la restauration, `PreviousPosition` est désormais recalé sur la `Position` effectivement produite par le chargeur, ce qui préserve l'état immobile sans autoriser ni effacer une vraie commande en attente. Les contrôles des files de commandes, du relevage, de la vue et du statut du tour restent inchangés.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 188 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis. Candidat exact `80464F14CBF4DB38068334E55B1924F5457E0D9988659319DFAA1DE4D5459C84`, DLL SHA-256 `1A5AAEC9C6B5A230BD330F2BD91946781FBCC3B069E7853F780CD69CF16D317B`.
- La 0.4.16.77 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé. La 0.4.16.76 est conservée sous `local-artifacts/backups/20261017-222314-normal-install-0.4.16.76`. ZIP UMM vérifié : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.77-UMM.zip`, SHA-256 `DEEDB65E021092125E2E65DA2CF9EE998DC129647046EF2CC4007BEAD4E3B06A`.
- Le chargement complet et la reprise du combat avec le build 0.4.16.77 doivent encore être confirmés en jeu. L'archive créée par la 0.4.16.76 porte l'empreinte de cet ancien build ; le prochain essai doit créer puis charger une nouvelle sauvegarde avec la 0.4.16.77. Aucun marqueur de validation moteur n'a été créé.

### Deuxième représentation polymorphe conservée — 0.4.16.76

- L'essai solo de 22 h 07 sur la 0.4.16.75 confirme la disparition des créations `KitsuneSpellLike`, `KitsuneMageLight` et du premier `Bite1d4`. Il reste exactement un identifiant `Bite1d4` créé par `AddAdditionalLimb.OnTurnOn` ; l'empreinte mécanique demeure identique avant/après (`F8ACB9828E2BED71DE8AE3AC641B6A2C719AE483691B8987A3791EB1AC811C11`) et seule la comparaison des générateurs refuse la publication.
- L'IL du jeu 2.7.0x montre qu'une arme `KeepInPolymorph` possède deux objets temporaires : le membre humain puis le membre polymorphe référencé par `m_HumanToPolymorphAdditionalLimbsMapping`. La 0.4.16.75 ne conservait que le premier. La capture mémorise maintenant les deux objets et les restitue dans l'ordre exact où `AddLimbToIndex` les réclame.
- Ce correctif ne relâche ni la comparaison des générateurs ni celle de l'état du combat. La publication reste refusée si un autre objet est créé pendant la capture.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 187 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 et six contrôles de paquet réussis. Candidat exact `23C7C030EC17F75CFC7FA032E0CDAC956E6C179A265361719E895A9740E693E9`, DLL SHA-256 `36787E139736D0E765BBFF1A722861E3182810FE797A31716CCE3975F7132A44`.
- La 0.4.16.76 est installée dans le dossier expérimental de l'installation habituelle, jeu fermé. La 0.4.16.75 est conservée sous `local-artifacts/backups/20261017-221118-normal-install-0.4.16.75`. ZIP UMM vérifié : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.76-UMM.zip`, SHA-256 `4A109373386F3BC63073D55EF1792E88F57AE66F38EE4C1E4A9FEB3774AE36AB`.
- Aucun nouvel essai moteur n'a encore prouvé que cette capture publie l'archive ni que son chargement restaure le tour. Aucun marqueur de validation moteur n'a été créé.

### Refus de publication du Kitsune corrigé — 0.4.16.75

- L'essai réel de 21 h 46 ne présentait ni action en cours ni divergence mécanique : l'empreinte restait `8EB973CDE1B153E9A6E37AC6ED85C8C0906FBCFBC1BFFF296621B40B743E3202` avant et après capture. La publication était annulée uniquement parce que les compteurs d'identifiants avançaient pendant la suspension native.
- Les piles du journal identifient deux mutations précises. La désactivation progressive des modificateurs réévaluait transitoirement `AddFeatureOnSkill` et recréait `KitsuneSpellLike` / `KitsuneMageLight`. La restauration de deux composants `AddAdditionalLimb` recréait ensuite deux armes `Bite1d4`, chemin qui n'était pas couvert par la conservation existante des armes naturelles.
- Pendant la seule portée synchrone de capture, la réévaluation transitoire `AddFeatureOnSkill.OnActivate` est maintenant ignorée. Les composants `AddAdditionalLimb` conservent et réutilisent leurs objets d'arme exacts lors du retour du monde, avec contrôle de l'index et vérification que toutes les armes mémorisées ont été consommées. Les comportements ordinaires hors capture restent inchangés.
- Vérifications Release avec `HostPresentationPrototype=true` : **1 187 tests principaux, 160 réseau et 33 localisation réussis** ; protocole 74 lu dans la DLL et six contrôles du paquet réussis. Candidat exact `550FED2DBF20A0B2EA6A0E7EEAE18BCBEDDA983EE8CF74E910CAB538DB4407B7`, DLL SHA-256 `3D1E75ED5BA8462C9D55AF9690B3396B85445D2934D92590D720E0A16D4F9ACC`.
- La 0.4.16.75 a remplacé la 0.4.16.74 dans le seul dossier expérimental de l'installation habituelle, jeu fermé. Sauvegarde récupérable : `local-artifacts/backups/20261017-215532-normal-install-0.4.16.74`. ZIP UMM vérifié pour l'autre PC : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.75-UMM.zip`, SHA-256 `31B485F9AEB165B4FCDCDD61812ECDC6956DF57C7BBA7BC485CDC5E30147FAEA`.
- Aucun nouvel essai moteur n'a encore confirmé la publication avec ce Kitsune, puis le chargement et la poursuite du même combat sur deux PC. Aucun marqueur de validation moteur n'a été créé.

### Correction du chargement du mod — 0.4.16.74

- Les journaux de l'installation habituelle à 21:38 et 21:40 montrent `Undefined target method` sur `EscMenuVM_Constructed_Postfix`, entraînant `Failed to load mod` dans Harmony.PatchAll.
- L'attribut Harmony omettait la signature du constructeur natif `EscMenuVM(Action closeAction)`. La cible indique désormais explicitement `System.Action`.
- Régression ajoutée : résolution du constructeur déclaré par l'attribut contre l'assembly installé. Les 11 contrôles menu/Harmony passent ; compilation Release. Aucun verrou de profil réintroduit. La disparition de l'erreur au prochain démarrage reste à constater dans le journal.

### Activation normale demandée explicitement — 0.4.16.73, protocole 74

- Nicolas a explicitement demandé le retrait des verrous de profil de test. `CombatSaveActivation.Enabled` ne dépend plus d'une variable d'environnement, d'un marqueur de profil ou d'une compilation conditionnelle. Cette instruction remplace la restriction aux profils isolés pour cette activation.
- Le bouton Échap → Sauvegarder reste réservé à l'hôte en combat TB. Les protections contre les actions en cours, divergences, opérations de chargement/réparation, doubles opérations et sauvegarde unique restent appliquées. Aucun contrôle de cohérence mécanique n'est contourné.
- Release compilée ; 69 tests ciblés réussis. Paquet `AA8C02988F6AFF014160CC6E2C3B5370C1327BB9988364ADE2ECBEFC17DE8739`. Aucun nouvel essai en jeu, aucune validation moteur déclarée, aucun marqueur de validation créé.

### Menu classique — 0.4.16.72, protocole 74

- Suppression du bouton de sauvegarde séparé du salon. Dans un profil de test autorisé, l'entrée native Échap → Sauvegarder de l'hôte ferme le menu puis appelle le coordinateur existant. Une nouvelle archive de combat est créée ; ce chemin n'ouvre pas le dialogue natif d'écrasement.
- Hors combat, le parcours natif reste inchangé. Client, profil non autorisé, mode RTwP, chargement protégé et mode sauvegarde unique ne gagnent aucune permission. Le coordinateur recontrôle toutes les conditions avant écriture. Les sauvegardes rapides ne sont pas débloquées.
- Copier le paquet dans l'installation habituelle ne suffit toujours pas à activer la fonctionnalité : le verrou du profil isolé reste explicite. Aucune validation moteur supplémentaire revendiquée.

### Restauration complétée côté code — 0.4.16.71, protocole 74

- Le client conserve le gel après `RestoreComplete`, même si sa confirmation est perdue. L'hôte collecte toutes les confirmations, vérifie son état puis envoie `RestoreRelease` avant les commandes de reprise. Une libération anticipée est ignorée ; une libération dupliquée ne rejoue rien. Les tests pilotent les vrais gestionnaires des acteurs.
- Le supplément natif conserve maintenant la monture, l'autorisation de relevage, l'immunité de désengagement, les engagements et cibles, les positions de retour et les protections de combat. Les champs omis de `CombatAiData` sont archivés : délais et usages d'actions, cibles, historique des attaques inefficaces, temps et état d'utilisation automatique. Les actions sont rattachées aux collections du cerveau chargé et leur blueprint est contrôlé ; aucune ancienne référence d'entité n'est réutilisée.
- Le contrôleur précédent est désabonné avant la construction du remplaçant. La propriété de relevage est rétablie avant son abonnement, sans émission initiale ; la monture reconstruite doit correspondre à celle archivée. Aucun appel à Start/Prepare pour recréer le début du tour.
- Le précontrôle lit et valide le supplément avant le changement de monde : champs obligatoires, compteurs finis, identité des collections et références. Une ancienne archive expérimentale incomplète est refusée ; elle n'est pas convertie en état supposé valide.
- Les ticks des contrôleurs d'unités et des cinématiques sont bloqués dans le seul monde lié à la restauration, en plus de l'horloge et du tour. Un delta nul ne suffisait pas à empêcher le démarrage d'effets. Le chargement avant liaison, les menus et le traitement réseau restent disponibles.
- Vérifications : **1 180 tests prototype réussis** avant les derniers ajouts de précontrôle des références et d'état automatique du cerveau ; **64 tests ciblés sauvegarde/restauration/Harmony réussis sur les sources finales** ; **160 tests réseau réussis**. Rapports `local-artifacts/diagnostics/combat-save-resume/tests-71-full.log`, `tests-71-final.log`, `network-74.log`. Aucun échec dans ces trois passes.
- **Limite de preuve : aucun nouvel essai moteur. La dernière restauration en jeu observée reste l'échec de la version 68. Les corrections ci-dessus ne prouvent pas encore la reprise jusqu'à la fin du combat sur deux instances.** Aucun marqueur de validation créé ; installation habituelle et sauvegardes originales non modifiées. La fonctionnalité reste réservée aux profils isolés.
- Compilation Release réussie ; candidat préparé et non installé : `local-artifacts/a0-candidates/B23AEB24386FAB55B097145B1A7008D9DA03DC3B3795E3C2C39E095B3A85840B/00_WOTRMultiplayerRTwP`. Journal `local-artifacts/diagnostics/combat-save-resume/build-71.log`. `git diff --check` réussi.

### Correction du déplacement de l'hôte — 0.4.16.70, protocole 73

- Cause vérifiée dans le code natif : CombatAction.UpdateCurrentStates consomme les prévisions locales. Une commande distante exécutée sans le curseur de l'hôte laisse ainsi certains états Available alors que les compteurs de déplacement sont déjà consommés. Les traces 68 montrent également des positions finales différentes de quelques centimètres.
- Ajout d'un état final envoyé par l'hôte après un déplacement de son personnage, lorsque commandes, mouvement et relevage sont terminés. Le client attend aussi son arrêt avant d'appliquer la position, l'orientation, les compteurs et l'état du tour dans le contrôleur existant, sans Start/Prepare. Monture comprise lorsqu'elle accompagne le rider.
- Message dédié non transférable entre clients ; contrôle expéditeur hôte, session, chargement, combat, époque, tour, unité, ordinal du déplacement et séquence. Réception bornée et application sur le thread du jeu. Une sauvegarde attend l'application du déplacement ; sa comparaison finale des empreintes demeure obligatoire. Une erreur d'application protège le monde.
- Portée explicite : profil isolé de sauvegarde, déplacement du personnage contrôlé par l'hôte. Aucun changement du RTwP ni des tours contrôlés par le client. Ce correctif ne constitue pas une validation de la restauration complète.
- Vérifications hors jeu : 54 tests sauvegarde réussis sur les sources finales, 160 tests réseau réussis. Compilation Release réussie. Candidat non installé : `local-artifacts/a0-candidates/736F2D1F48C176C8DDEA9B668E61E2AAF717EEA9B0222B5A281DEB1FB91CB1DE/00_WOTRMultiplayerRTwP`. Aucun nouvel essai graphique ni installation dans le jeu habituel. Confirmation moteur encore absente.

### Reprise 0.4.16.69 — essais graphiques arrêtés à la demande de Nicolas

- Preuve moteur 0.4.16.68 : première sauvegarde publiée à 19 h 46, tour d'Ellie, round 1, empreinte commune `2CA96CBCCFB41927C5482EFC6B50705F17DFE5325CCE2120EE78EB1A7E4E3605`. Archive `Multi - Combat - 2026-10-17 19-46-51 - 4faee14fc6bc403498b1807339463dc0.zks` dans le seul profil hôte isolé, visible dans le menu de chargement (vignette manquante).
- Après déplacement : divergence de position/orientation et d'états d'actions, sauvegarde refusée. Checkpoint privé créé, mais l'ancien appel ForceLoadGame ne rechargeait que le client. Correction : passer par QuickLoadGame, dont le chargement natif déclenche aussi la préparation réseau de l'hôte.
- Chargement manuel de la première archive : refus `combat-save-turn-restore-mismatch`. Diagnostic détaillé ajouté ; cause exacte du refus non confirmée. Audit natif : HasMotionThisTick compare Position à PreviousPosition. Ajout de cette dernière au supplément, restauration de la valeur enregistrée, rejet d'une archive où ces champs manquent. Aucun contournement du contrôle des commandes ou de la divergence.
- Vérification hors jeu : contrat natif de statut du tour et copie des membres persistés testés ; conservation de la position précédente et des compteurs d'actions testée. Les traces volumineuses de création d'identifiants sont limitées à la capture synchrone.
- Compilation Release et 52 tests ciblés réussis sur les sources 0.4.16.69. PreviousPosition est conservé dans l'archive mais exclu de l'empreinte inter-pairs : c'est une référence d'image locale ; les positions actuelles restent contrôlées par l'empreinte du monde. Le candidat préparé avant cette dernière modification n'est pas le livrable courant et ne doit pas être installé. Aucun déploiement de la version 69.
- **Fonction encore incomplète : convergence après déplacement, restauration complète et reprise du combat non démontrées. Aucun marqueur de validation moteur, aucune installation habituelle modifiée. Ne pas reprendre la boucle d'essais graphiques sans nouvelle instruction de Nicolas.** Les preuves précédentes sont archivées sous `local-artifacts/diagnostics/combat-save-resume/save-and-load-68/`.

### Essai moteur 19 h 31 — empreintes concordantes, armes naturelles recréées ; 0.4.16.68

- 0.4.16.67 : 1 174 tests prototype réussis. Premier tour de Wenduag : empreinte hôte/client `B54E27EAED2F936602D14AF0293BAA8289CC4C99DF64D1734FDD2A2C5A0911D5` concordante, identique après capture. Publication refusée uniquement par les compteurs d'identifiants. Les traces attribuent les créations à EmptyHandWeaponOverride.OnTurnOn (Unarmed1d6/Unarmed1d3) et AddSecondaryAttacks.OnTurnOn (AnimalFuryBite), pendant le rollback de la suspension native. Journaux archivés sous `save-publication-refusal-67/` dans le dossier de diagnostics.
- 0.4.16.68 : conserver les objets d'armes retirés par ces composants et les réutiliser pendant leur seul rééquipement synchrone de capture. Le TurnOff et le contenu natif sérialisé gardent leurs règles habituelles ; aucune remise à zéro des générateurs. Vérifier que la file d'objets à restituer est épuisée avant d'accepter la capture. Hors capture, le constructeur natif reste inchangé.
- 36 tests ciblés réussis. Candidat `79476985BD2C707E7B0CCC57BA31040D87C28415C829F0FD9DF6E7E1FDCD760F`, DLL `1EB5AB028E9F858EFB2BFCB81326BAAC0C6CBD3FF38CB31116AF4B902B736DE2`, copies isolées uniquement. Essai moteur en cours, aucune sauvegarde en combat publiée à ce stade. Les 41 sauvegardes originales sont inchangées au contrôle de 19 h 29.

### Essai moteur 19 h 20 — compteurs d'identifiants ; diagnostic 0.4.16.67

- 0.4.16.66 : 1 174 tests prototype réussis. Deux instances connectées, tour de Wenduag après l'archer. Seul écart d'empreinte relevé : TimeToNextRound 5.454545 / 5.4545455. La récupération capture un état mécanique inchangé et reste autorisée, mais trois compteurs d'identifiants avancent pendant la préparation ; le dernier contrôle refuse correctement la publication. Preuves dans `local-artifacts/diagnostics/combat-save-resume/save-publication-refusal-66/`.
- 0.4.16.67 : projection temporelle du contrôleur avec conservation du signe et des limites de rounds ; valeurs originales toujours archivées. Diagnostic des appels générant un identifiant pendant le gel, sans contourner leur contrôle. Chemin de publication corrigé vers le véritable `Saved Games` du profil (GetPersistentDataPath du service désigne le sous-dossier Multiplayer). Suppression du double Dispose du tour : le setter natif CurrentTurn dispose déjà le précédent.
- 12 tests ciblés réussis. Candidat `C67E1232142C665A5334B6BAED70CBF3930253549156D0A7E0B2E93D95E7285D`, DLL `413A0D764FF8C8E378C07719C2C4183007ED5626AB6B3B186723DDD3D87B46DC`, installé seulement dans les copies isolées. Nouvel essai en cours ; sauvegarde/rechargement/reprise toujours non validés.

### Essai moteur 19 h 05 — capture corrigée ; diagnostic 0.4.16.66

- 0.4.16.65 : 1 174 tests prototype réussis. QuickSave3 chargé sur les deux instances ; un maraudeur agit avant Wenduag. Le relevé des compteurs révèle seulement cinq écarts flottants (initiative et mouvement), de moins de 0,000007 seconde. La capture native de récupération passe sans les suppressions d'effets précédentes ; la publication est ensuite annulée par son dernier contrôle. Aucun fichier joueur publié.
- 0.4.16.66 : projection des seuls compteurs temporels à 0,0001 seconde pour l'empreinte, en conservant le signe et le côté des seuils d'actions 0/3/6. Valeurs originales de l'hôte conservées dans l'archive. Test avec les valeurs réellement observées et vérification qu'un franchissement de seuil reste distinct. Ajout du diagnostic exact du refus de publication (autorisation, empreinte, générateurs). 36 tests ciblés réussis.
- Candidat `C91C2EA5C5586BAEE1CCDE2DEAFFD91A7CC53667F64717DE560B5977BC930644`, DLL `88B4187471227274CF8D208C81E2259CF375049F813084DC0C6B7F2E96CD8EA4`, installé seulement dans les deux copies isolées. Essai moteur en cours. Journaux précédents dans `local-artifacts/diagnostics/combat-save-resume/save-publication-refusal-65/`.
- Contrôle des sauvegardes originales à 19 h 02 : 41 fichiers, aucune empreinte modifiée (`local-artifacts/combat-save-lab/original-saves-after-capture.json`). Sauvegarde/rechargement/reprise encore non validés.

### Essai moteur 18 h 50 — comparaison acceptée, capture refusée ; candidat 0.4.16.65

- Version 0.4.16.64 : 1 174 tests prototype réussis. Deux instances connectées, QuickSave3 chargé, premier tour du compagnon ours. La comparaison hôte/client aboutit et atteint WriteNewSaveAsync. La capture refuse ensuite `native-checkpoint-mechanical-state-changed` ; aucune archive publiée ni restauration validée.
- Différence exacte avant/après préparation : cinq `ImpatienceTickingBuff` supprimés et quatre orientations de zones passant du cache nul à leur transformation réelle. PreSave recopie normalement cette transformation. `AddBuffOnCombatStart.OnTurnOff` supprime AppliedBuff ; seul un nouveau jet d'initiative le remettrait. Les relevés sont conservés dans `local-artifacts/diagnostics/combat-save-resume/save-capture-refusal-64/`.
- Candidat 0.4.16.65 : conserver ces effets pendant la seule suspension synchrone de capture TB, avec portée liée au monde protégé ; comparer la transformation effective des zones lors des captures du profil de test. Les vérifications après capture restent obligatoires. 36 tests ciblés passent ; nouvel essai moteur en cours.
- Empreinte `C401C76BAF3A7AF01A1078E7CBF2EE33DD0EE469A1B1BE2743574FE2C8C031E0`, DLL `AB7322EA87BB01B7AEBAC1302EFF0ED1FE476C7A970DD681E835032BDFBDBA95`. Installation dans les deux copies isolées seulement. Aucun marqueur de validation moteur.

### Diagnostic 18 h 33 — comparaison des prévisions, version 0.4.16.64

- Version 0.4.16.62 : 1 172 tests prototype réussis. Le défaut de capture GPU ne se reproduit plus ; la vérification après capture refuse désormais `native-checkpoint-mechanical-state-changed`. La mutation exacte reste à identifier ; aucune archive publiée.
- Version 0.4.16.63 : relevés obtenus dans les deux profils avant toute action du premier tour de Wenduag. État des unités strictement identique (JSON et empreinte `11700419415729448587`). Les différences du tour sont exclusivement les prévisions de l'interface (`*Predicted`, CanUse, Type, ApproachPoint, m_IsOutOfRange). Journaux et relevés conservés sous `local-artifacts/diagnostics/combat-save-resume/save-peer-divergence-63/`.
- Correction 0.4.16.64 : comparaison des états actuels d'actions sans les prévisions de curseur. Vérification du contrat natif : UnitEntityData.CombatState est absent de la sauvegarde native. Ajout explicite des compteurs d'actions, initiative et opportunités dans le supplément, comparés et restaurés avant de reconstruire le tour. 36 tests ciblés réussis, dont contrat natif et distinction prévision/action consommée. Validation moteur et suite complète encore en cours.
- Candidat installé uniquement dans les copies isolées : `2D8DDC51A199F26B1F9F6A453137B10EC997FE87570C2F85B1EF509743E4C8A3`, DLL `4ED96620388F76C09B3537B41D88F4A7827C08DDFAC02F6B2CF64E42B2B92EFF`. Aucune validation moteur créée. Les 41 sauvegardes originales sont inchangées au contrôle de 18 h 30.

### Essai moteur 18 h 14 — divergence constatée, diagnostic 0.4.16.62

- Version 0.4.16.61 : 1 171 tests prototype réussis. Combat chargé sur deux instances, Ember round 1, déplacement consommé. La capture native passe ; le client répond, mais les empreintes divergent. Aucune sauvegarde publiée.
- La récupération du tour hôte échoue ensuite avec `native-checkpoint-fog-readback-failed`. Le pilote de coroutine ajoutait une image après la fin de la requête GPU, dont la durée de validité est limitée. Correction pour consommer le résultat dans la même image ; test de régression et 23 autres tests de capture réussis.
- Version 0.4.16.62 : relevé local borné des états comparés dans chaque profil isolé pour identifier la divergence, sans relâcher les critères. Candidat `A5E60377EF272C00157675FA7952DE4D6557B71D5DC2DE2C99848EB7E38600E7`, DLL `523051E579C3BAD55C2903B5FAB7992C979C5F9D28E7A611DCBB12EA8AEBEA3D`, installé uniquement dans les deux copies de test.
- Journaux 0.4.16.61 conservés dans `local-artifacts/diagnostics/combat-save-resume/save-peer-divergence-61/`. Sauvegarde/rechargement/reprise toujours non validés ; aucun marqueur moteur créé.

### Essai moteur 17 h 58 — sérialisation native, version 0.4.16.61 en préparation

- Version 0.4.16.60 : 1 170 tests prototype réussis ; empreinte `64047745138A69390EACEE651F0F25B4C9A9AA9FDAA968AF25F903DCC8A09335`, DLL `6DB1EA6B5A60D8CB3078FC41C866F9302F016D14AA1AADF111910B1067F3CDF9`, copies isolées uniquement.
- Nouveau combat : Ember joue en premier. Petit déplacement exécuté, puis sauvegarde. Le contrôle du tour passe ; l'empreinte échoue avec `combat-save-missing-native-field:RoundNumber`. Aucune sauvegarde publiée.
- Le sérialiseur natif utilise `OptInContractResolver`, tandis que le test de contrat utilisait à tort `DefaultContractResolver`. Le champ RoundNumber, Initialized et plusieurs champs temporels du contrôleur ne sont pas persistés. Correction : supplément explicite enregistré dans le payload TB, capturé dans l'empreinte et restauré sans appeler Initialize/Prepare. Copie des membres persistés également alignée sur OptInContractResolver pour préserver les liaisons de runtime.
- 48 tests ciblés réussis, dont contrat natif réel et aller-retour du supplément dans l'archive sans modifier la source scellée. Suite complète et nouvel essai moteur encore en cours. Journaux conservés dans `local-artifacts/diagnostics/combat-save-resume/save-native-schema-refusal/`.

### Essai moteur 16 h 12 — correction du critère de tour 0.4.16.60

- Candidat 0.4.16.59 : 1 170 tests principaux prototype réussis ; suite standard complète 1 170 + 160 réseau + 33 localisation réussis. Empreinte `6C5C33C9D859EF050E0B6B5CEC627A7AFD4E6934BD42E0241054C91EEF2F7510`, installé exclusivement dans les deux copies isolées.
- Chargement QuickSave3 puis premier tour de Wenduag synchronisés, graine `893458114`. Après déplacement, la demande atteint la capture native du tour : le refus de pause précédent est dépassé. Nouveau refus `combat-save-native-turn-not-ready` ; aucune archive publiée.
- Audit de `TurnController.Tick` et `ContinueActing` : le statut natif `Acting` persiste après une commande, même lorsque le joueur attend son prochain choix. `IsActing` ne signifie donc pas qu'une action est encore exécutée. Version 0.4.16.60 : accepter Preparing ou Acting uniquement sans commande active/en attente, déplacement ni relevage ; conserver le refus Scrolling/Ending/Ended/Delayed. Même contrôle au chargement. Cette correction reste à valider en moteur.
- Journaux conservés dans `local-artifacts/diagnostics/combat-save-resume/save-turn-refusal/`. Deux instances fermées proprement pour reconstruction. Aucune validation de sauvegarde/rechargement en combat à ce stade.

### Essai moteur — première sauvegarde refusée, correction 0.4.16.59 en cours

- Le candidat 0.4.16.58 (`7DEA4583B39E4E93E60B202236CD7314FB6D549C2379878FC6738B64BB759324`, DLL `C4D19801C1C40DC0202609F2D7FC4D0B2CF2F63B638FD0CAEEEF652A3AD9FAEA`) a passé 1 168 tests prototype. Premier tour de Wenduag effectivement démarré sur les deux instances à 15 h 49, accusé de réception et graine `-1520994207` concordants. Le blocage précédent est corrigé en moteur.
- Déplacement consommé pendant ce tour, puis bouton `Save Combat` essayé à 15 h 50 et 15 h 52. Les deux opérations échouent avant écriture avec `native-reference-host-not-at-safe-point`.
- Cause native vérifiée dans `Game.DoStartMode` : le moteur refuse le mode `Pause` pendant un combat TB. `Game.IsPaused = true` ne constitue donc pas une protection effective. La version 0.4.16.59 remplace cette demande par un gel borné au monde de l'opération : horloge suspendue et deltas nuls, ticks de l'horloge et du contrôleur de combat retenus, commandes déjà bloquées. Les contrôles de capture acceptent cette protection effective ; les autres préconditions demeurent.
- Les opérations abandonnées sans autorisation de reprise transfèrent leur gel à un verrou lié au monde. Un autre monde chargé ne l'hérite pas. Les essais moteur de cette correction restent à faire.
- Point de reprise hors combat créé dans le seul profil hôte : `Saved Games/Quick_3.zks`, 15 h 48, devant la maison. Journaux du refus conservés sous `local-artifacts/diagnostics/combat-save-resume/save-pause-refusal/`. Les deux instances sont fermées pour reconstruire.

### Essai moteur — blocage du premier tour identifié, correction 0.4.16.58

- Connexion et chargement de `Manual_103` terminés à 15 h 31. Combat contre cinq maraudeurs déclenché à 15 h 33 sur les deux instances, initiatives concordantes.
- Le premier tour restait bloqué : le client était en `Initialization` après `OnBeforeTurnStart`, tandis que le nouveau garde de réception du checkpoint exigeait `Starting`, transition propre à l'hôte. Le message reçu était donc ignoré sans accusé de réception.
- Correction : accepter l'étape client `Initialization` en conservant les contrôles de session, sauvegarde, combat, tour et expéditeur, ainsi que la déduplication. Test ajouté depuis le véritable point d'entrée `OnBeforeTurnStart`, plus contrôle de répétition du checkpoint dans cette étape.
- Journaux conservés sous `local-artifacts/diagnostics/combat-save-resume/first-combat-stall/`. Les deux jeux isolés ont été fermés pour reconstruire le candidat. Protocole inchangé (72).
- **La sauvegarde et la restauration en combat ne sont toujours pas validées.** Les chiffres et empreintes de la section suivante décrivent le candidat précédent.

### Développement poursuivi — version 0.4.16.57, protocole 72

Les points ci-dessous remplacent les anciens constats de reprise pour les sources courantes. La validation en jeu reste ouverte.

- Suite principale prototype complète : **1 166 réussites, 0 échec, 0 ignoré**, `combat-save-prototype-72-final.trx`. Les anciens scénarios de réparation exécutent maintenant le transport de reconstruction native avec doubles des seules opérations Unity ; ils exigent la convergence des deux acteurs et la confirmation finale. Les échecs natifs gardent le monde protégé.
- Barrière de restauration : l'hôte attend désormais le reçu de `RestoreComplete`. Le client conserve un reçu rejouable limité à l'opération ; test de perte du reçu puis renvoi et test de divergence. Une opération périmée ne bloque plus un autre chargement ; nettoyage des états transitoires à la déconnexion.
- Tour natif : seul `Preparing` est admissible. `Scrolling` est refusé car son prochain tick rappelle `Prepare` et rendrait des actions. Réabonnement au changement de relevage sans émission initiale, sans appel à `Start`/`Prepare`.
- Après publication, ajout de l'archive au catalogue local et événement de rafraîchissement ; aucun appel de rescan global ni d'envoi cloud.
- Divergence : la sauvegarde manuelle est refusée. Si l'état hôte est strictement inchangé depuis la demande et les pairs identiques, un checkpoint privé dans `Cache/CombatRecovery` déclenche le chargement réseau des deux mondes. Il ne devient pas un emplacement de sauvegarde. Ce chemin doit encore être éprouvé en moteur ; un échec reste protégé.
- Deux installations distinctes préparées sous `local-artifacts/combat-save-lab/{host-game,client-game}`, sans les mods habituels ; deux copies terminées sans erreur. Profils `{host-profile,client-profile}` marqués, trois copies de sauvegardes chacun. Empreintes des sauvegardes originales consignées dans `original-saves-before.json`.
- Lanceur : paramètre `-ProfilePath` pour ces profils, arguments de lancement fenêtré 1280×720 ; le moteur impose initialement son plein écran, corrigé via Alt+Entrée dans chaque copie. Chaque installation dispose de son propre `startup.json.logsDir` ; les dossiers de journaux existent avant lancement.

- Suite standard complète : **1 166 principaux + 160 réseau + 33 localisation**, tous réussis, `combat-save-standard-72-final.trx`. Contrôle FR/EN : 270 clés, paramètres concordants.
- Candidat exact : `5EEDF2DA76C44E51AEEB3BF5CA742B60989A8F787544543225A28D773357D12E`, protocole 72 vérifié dans la DLL, 7 contrôles du manifeste réussis. DLL SHA-256 `6CB6FCB60CD87DCA666D51A82857B736423CA684B731EE9CCCD0FED219E17F1E`. Copié dans les deux installations isolées, jamais dans le jeu habituel. Démarrage, redirection des profils et des journaux confirmés pour les deux instances. Connexion locale 127.0.0.1:1024, protocole et empreinte concordants. Transfert de la copie de sauvegarde Place du marché réussi ; premier chargement de zone lancé sur les deux instances à 14 h 48. La sauvegarde en combat reste à éprouver.

### Essai moteur en cours — 15 h 25

- Chargement multijoueur terminé à 14 h 51, pause de chargement levée sur les deux instances. Déplacements et utilisation d'objets transmis ; transitions vers la maison à 15 h 08 et sa cave à 15 h 15 confirmées dans les journaux des deux copies.
- Deux sauvegardes rapides hors combat créées uniquement dans `host-profile/Saved Games` pour la reprise de l'essai. Elles ne valident pas la nouvelle fonction de sauvegarde en combat.
- La rencontre du démon des ombres était déjà terminée dans la copie `Manual_113` : son `UnitPlaceholderEntityData` porte `m_IsFinallyDead=true`. Les entrées de mécanique propres à la cave, presque vides, n'étaient pas une preuve de zone inexplorée ; l'état est dans `KenabresBurning_Default_Mechanics`.
- Copie supplémentaire de `Manual_103_Place_du_march___25_Arodus__VIII__4715__09_09_58.zks` vers le seul profil hôte (SHA-256 `FE9189910170702C60E408A76B1C6690D7851DEE83755F269544A155D83D8BCE`). Redémarrage de l'hôte isolé pour actualiser le catalogue ; nouveau PID 46364. Client 24300 toujours ouvert. Journaux du premier essai conservés sous `local-artifacts/diagnostics/combat-save-resume/host-market-exploration/`.
- **Toujours aucune sauvegarde en combat ni restauration du tour validée. Aucun marqueur de validation moteur.**

### Historique : reprise dans le workspace déplacé — 17 octobre 2026

**Computer Use répond dans cette conversation** : import de `@oai/sky` via `node_repl`, puis appel réussi à `sky.list_windows()`. Le dossier courant existe (`G:\Codex\PathFinder`). Aucun lancement du jeu ni entrée graphique pendant cette vérification. Le blocage d'outils décrit plus bas concerne l'ancienne conversation.

Tests relancés sur les sources actuelles, toujours en Release :

- Suite principale standard : **1 156 réussites, 0 échec, 0 ignoré**, `combat-save-standard-resume.trx`.
- Suite principale prototype : **1 145 réussites, 11 échecs, 0 ignoré**, `combat-save-prototype-resume.trx`. Deux échecs dans `MultiplayerHostTests`, neuf dans `RealTimeProtocolIntegrationTests`, sur les scénarios de réparation/repli. Leur adaptation au chemin natif reste à faire sans retirer les critères de convergence et de récupération.
- Réseau : **160 réussites, 0 échec**, `combat-save-network-resume-fixed.trx`, après correction du contrôle Roslyn d'utilisation des messages. Il ignorait les expressions C# implicitement typées `new()` et déclarait à tort `NotifyInvalidCombatTurnStarted` inutilisé. `BaseObjectCreationExpressionSyntax` couvre maintenant les deux syntaxes ; aucun message n'est exclu du contrôle.
- Localisation : **33 réussites, 0 échec**, `combat-save-localization-resume.trx`.

Les rapports sont dans les dossiers `TestResults` des projets correspondants. Les sorties Release ont été reconstruites ; elles ne sont pas un nouveau candidat livré. Aucun code fonctionnel du mod modifié pendant cette reprise, aucun déploiement ni marqueur de validation.

Isolation inspectée, **pas encore préparée pour le jeu** :

- Installation habituelle : `D:\Perso\Steam\steamapps\common\Pathfinder Second Adventure`, inspectée en lecture seule. Le hash de `Assembly-CSharp.dll` correspond au manifeste du candidat C5927571… (`2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953`).
- Les deux profils `local-artifacts/runtime-profiles/host` et `client` existent, mais leurs dossiers `Saved Games` sont vides.
- Les mods installés sont dans `WOTRMultiplayerRTwP`, `WOTR_AIVO` et `AllowModdedAchievements`, pas dans les noms de slots attendus par le lanceur. Préparer une installation distincte ; ne pas renommer l'installation habituelle pour satisfaire le lanceur.
- `BuildModeUtility.GetLogsDir()` utilise `startup.json.logsDir` si le dossier existe, sinon le chemin Unity global. La redirection du profil par le mod ne suffit donc pas à isoler ces journaux. Prévoir une racine de jeu séparée pour chaque instance avec son propre `startup.json` et son dossier de journaux existant, sans modifier ceux du jeu habituel.
- Les méthodes de `SteamSavesReplicator` sont vides dans l'assembly inspectée. Cela ne prouve pas l'absence d'une synchronisation externe par le client Steam.
- Décompilation de diagnostic conservée sous `local-artifacts/diagnostics/combat-save-resume/`. Aucun essai moteur réalisé ; les étapes restantes ci-dessous demeurent ouvertes.

### État transmis avant cette reprise

Raccordement moteur/réseau codé, compilation Release réussie, **non validé en jeu et non livré comme utilisable**. Sources 0.4.16.56, protocole 71. Aucun changement du mod installé, des sauvegardes originales ou du doublage. Aucun nouveau ZIP ni marqueur de validation produits.

Nicolas autorise maintenant le lancement du jeu. Aucun lancement effectué pendant ce raccordement. Une installation expérimentale séparée reste à préparer : ne pas remplacer l'installation stable pour cet essai. La procédure computer-use a été lue ; le point d'entrée node_repl nécessaire au contrôle Windows est absent des outils de cette session. La recherche de plugin n'a pas fourni de solution pertinente. Ce blocage concerne l'essai interactif, pas la compilation.

Le bouton reste désactivé pour les profils ordinaires. Le chemin de développement exige la compilation HostPresentationPrototype, WOTR_RTWP_COMBAT_SAVE_TEST=1, un profil WOTR_RTWP_TEST_PROFILE marqué .rtwp-test-profile, et la redirection effective du chemin persistant du moteur vers celui-ci. Cela autorise un essai isolé, pas une livraison validée.

## Constats natifs vérifiés en lecture seule

- SaveManager.IsSaveAllowed interdit le combat, mais aussi diverses situations incompatibles : fin de partie, dialogue, repos, cinématique, rencontre de carte, transformation du compagnon double, croisade, respecialisation et interface des îles.
- Le nouveau transpiler remplace uniquement son appel Player.IsInCombat lors d'une interrogation synchrone de l'hôte, protégée par try/finally. Les autres interdictions restent exécutées. Une disposition IL inconnue reste bloquée.
- La sauvegarde native écrit le contrôleur TB dans tbm.json via OwlcatJsonConvert.
- **Le chargeur natif ignore tbm.json.** Écrire cette entrée ne suffit pas pour reprendre le tour.
- Le chargeur traite les fichiers JSON inconnus comme des zones. Les métadonnées utilisent donc multiplayer-combat.state, avec contenu JSON.
- TurnController.Start/Prepare réinitialisent des états mécaniques. Le restaurateur ne les appelle pas : il utilise le constructeur lié à l'unité et les membres sérialisés. Le comportement des liaisons et abonnements reste à vérifier en moteur.

## Raccordements réalisés

### Préparation et écriture

- Coordinateur à opération unique : identité session/chargement/époque/opération, préparation limitée à 15 secondes, confirmations authentifiées par connexion, ensemble exact de pairs, rejet des réponses anciennes/manquantes/dupliquées et comparaison des empreintes.
- Adaptateur hôte et échanges NotifyCombatSave / ClientCombatSaveAck enregistrés dans le transport existant ; protocole 71.
- Accès moteur rapatriés sur le thread principal ; pause possédée par l'opération ; blocage des commandes, changements de mode, début/fin/report de tour et sauvegardes concurrentes.
- Refus client traité comme une annulation, non comme une écriture défaillante. Une divergence ne produit aucun fichier et conserve une pause protégée.
- Capture native complète en mémoire incluant le vrai contrôleur TB, données du monde et brouillard ; contrôle avant/après callbacks. Le chemin RT distinct conserve son document TB vide.
- Métadonnées : schéma, jeu, protocole, empreinte du paquet, attributions hôte/client, état réseau, graines sémantiques, générateurs déterministes et compteurs.
- Nouveau fichier Multi - Combat - date - identifiant.zks ; écriture temporaire puis publication sans écrasement ; en-tête affiché « Multi — Combat — date et heure ». Hash des entrées, tailles/noms/JSON contrôlés et nettoyage du temporaire sur échec.

### Chargement

- Prévalidation de l'archive avant remplacement du monde : compatibilité exacte, métadonnées, générateurs, tour réseau et données natives.
- Refus d'un chargement solo de cette archive pour éviter une reprise silencieuse sans restauration du tour.
- Reconstruction du contrôleur TB sans démarrer de nouveau tour ; restitution des attributions et générateurs ; nettoyage des anciennes confirmations.
- Nouvelle époque de chargement distincte des graines sémantiques. Une sauvegarde ultérieure utilise la graine du tour courant, pas celle du premier chargement.
- Barrière RestorePrepare → RestoreCommit → RestoreComplete, empreintes vérifiées, renvoi périodique et arrêt protégé sur refus, divergence, déconnexion ou délai dépassé.
- Messages français/anglais et traces [CombatSave] pour préparation, confirmations, résultat, fichier et restauration.

## Vérifications

### Mise à jour après reprise du développement

- Configuration standard, suite entière : **1 146 réussis, 0 échec**, rapport combat-save-standard-final.trx. Ce passage précède l'ajout des 10 tests de prévalidation réseau ci-dessous ; ne pas le présenter comme un passage entier de la toute dernière révision.
- Configuration expérimentale, dernier lot sauvegarde/générateurs/graphe natif/patches : **126 réussis, 0 échec**, rapport combat-save-prototype-final.trx. Comprend les nouveaux tests de prévalidation, les collections réseau non vides et le blocage des callbacks de changement de tour.
- Défaut réel corrigé : les cases nulles du tableau natif de commandes sont des emplacements inoccupés, pas une corruption ; la validation continue à rejeter les commandes dont l'exécuteur ou la cible sont invalides.
- Défaut réel corrigé : l'ancienne bibliothèque JSON appelle les callbacks de désérialisation binaire des ConcurrentDictionary. Un résolveur local au format réseau empêche ces callbacks inadaptés ; un aller-retour avec collections non vides est vérifié.
- Les tests de transport transmettent désormais NotifyRealTimeCombatPreference au vrai gestionnaire client. Les attentes d'application ont été mises à jour vers la surcharge qui réconcilie les buffs TB ; les critères de convergence ne sont pas supprimés.
- Les messages de refus sont maintenant spécifiques et traduits. Un échec d'écriture disque ne laisse plus uniquement le client en pause protégée ; une capture réellement incertaine est signalée comme divergence aux deux côtés.
- Les callbacks de début/report de tour sont bloqués pendant restauration, sans créer le tour temporaire utilisé par la barrière ordinaire. La protection d'un monde dont le chargement a échoué ne s'applique pas à un autre monde chargé ensuite.
- Le prototype complet contient encore des tests de l'ancien protocole de réparation qui attendent Apply/Fallback, alors que le prototype utilise la reconstruction native. Leur adaptation et la couverture de bout en bout native restent à faire ; le lot ciblé vert n'est pas une suite expérimentale entière verte.

Candidat de développement préparé, **non installé** : local-artifacts/a0-candidates/C5927571963808AE1141CE53C3CF9FA2F83EA77D82B43CEAF8810B68E79773CF/00_WOTRMultiplayerRTwP. Le manifeste indique runtimeValidated=false. Le candidat intermédiaire 553DA049… est marqué NE-PAS-UTILISER.md en raison du défaut de relecture découvert après sa construction. Aucun ZIP de livraison.

Le lanceur tools/start-rtwp-a0-instance.ps1 accepte maintenant -CombatSaveTest, uniquement pour un profil marqué et une installation déjà expérimentale. Il refuse toujours la présence du stable, ne copie ni n'installe de mod, et restaure ses variables d'environnement après le lancement. Syntaxe PowerShell vérifiée ; lanceur non exécuté.

### Historique des premiers passages

Compilation/test Release avec HostPresentationPrototype=true et WOTR_PATH pointant vers l'installation du jeu.

Dernier lot ciblé CombatSave + GeneratorCheckpoint + DeterministicValueGeneratorTests : **64 réussis, 0 échec**. Rapport : src/WOTRMultiplayer.UnitTests/TestResults/combat-save-final-focused.trx. Il inclut les tests existants du générateur ; ce ne sont pas 64 scénarios Unity.

Passe générale intermédiaire : **1109 réussis, 34 échecs**, rapport combat-save-full-regression.trx. Un double attribut Harmony ajouté pendant le raccordement a ensuite été corrigé. Contrôle ciblé des patches après correction : 4 réussis, 1 échec de convention de nommage pour IngameMenuVM_Constructed et IngameMenuVM_Disposed. La suite générale n'est pas déclarée verte : restent à examiner les scénarios réseau refusant NotifyRealTimeCombatPreference, les attentes de réparation et le test de graphe natif. Aucune attribution automatique de ces échecs à un état antérieur.

git diff --check réussi avant cette mise à jour documentaire. Avertissement NU1903 sur AutoMapper 10.1.1 toujours présent.

## Ce qui reste réellement à terminer

1. Vérifier les liaisons moteur du tour reconstruit : monture, abonnements, relevage, réglage TB au chargement, interface et absence de callbacks de début de tour. Le contrat JSON vérifié hors moteur ne prouve pas ces comportements.
2. Valider l'empreinte entre PC et après chargement : initiative, temporalité, buffs, ressources, entités et ordre des collections.
3. Compléter la récupération d'une divergence pendant un tour joueur. Actuellement l'opération annule et protège ; la réparation existante ne possède pas forcément un checkpoint utilisable au milieu du tour. **Une pause protégée seule n'est pas une récupération réussie.**
4. Vérifier le nettoyage complet lors d'une déconnexion, fermeture de session ou autre chargement ; ajouter les tests des vrais acteurs pour les barrières, en plus des doubles du coordinateur.
5. Vérifier en moteur les messages de refus désormais spécifiques et le rafraîchissement de la liste native des sauvegardes après publication. Ne pas déclencher une réplication Steam Cloud sur les profils de test.
6. Adapter les scénarios de l'ancien protocole de réparation au chemin natif expérimental et valider la suite complète de ce dernier, sans masquer les critères. La suite standard a passé comme indiqué ci-dessus.
7. Préparer une installation expérimentale distincte et deux profils isolés, avec uniquement des copies de sauvegardes.
8. Tester le paquet exact sur deux instances : sauvegarde après action/déplacement partiels, mort, invocation et buff ; même tour/actions/ressources après chargement ; poursuite jusqu'à la fin du combat ; interruptions, reconnexion et refus d'archives incompatibles.

## Livraison

Code de développement protégé, pas encore fonctionnalité de campagne disponible. Ne pas créer RTWP_RUNTIME_VALIDATED.json sur la base des tests hors moteur. La livraison normale exige une reprise prouvée sur deux instances, sans actions rendues ni effets rejoués.

### Journal des essais moteur isolés — lancement du 17 octobre, 14 h 39

- Copie de chaque jeu : 23 161 fichiers, 31,872 Gio, zéro échec Robocopy. Les sorties 1 de Robocopy signifient ici une copie réussie, pas une erreur.
- Le paquet 5EEDF2DA... est installé uniquement dans les deux copies. Hash identique de WOTRMultiplayer.dll vérifié.
- Hôte : profil host-profile et logsDir host-profile/logs confirmés par les journaux, mod 0.4.16.57 actif, 538 méthodes Harmony patchées, 1/1 mod chargé.
- Premier processus fermé à l'initialisation Steam. Le code appelle SteamAPI.RestartAppIfNecessary ; le fichier de développement steam_appid.txt (1184370) a été ajouté uniquement aux copies, conformément à https://partner.steamgames.com/doc/sdk/api?language=english. Aucun patch de Steam ni changement de l'installation habituelle. Journaux initiaux préservés sous local-artifacts/diagnostics/combat-save-resume/host-first-launch.
- Nicolas a fermé lui-même la demande du pare-feu avec Annuler. Aucun réglage de sécurité modifié par l'agent.
- Le second lancement hôte atteint l'écran de chargement, fenêtre responsive. Le client isolé a été lancé ensuite. Aucun scénario de sauvegarde/restauration en combat validé à cet instant.
