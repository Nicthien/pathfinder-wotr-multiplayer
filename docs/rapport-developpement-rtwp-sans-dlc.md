# Rapport de développement — Multijoueur temps réel avec pause, sans DLC

> Incrément du 7 octobre 2026 : fabrique de contextes de présentation détachés ajoutée à la variante 0.4.16.12, protocole 48. Compilation prototype, 644 tests et diagnostic Mono du candidat exact réussis, sans lancement du jeu. Voir le [bilan de reconstruction des contextes](rtwp-buff-context-factory-2026-10-07.md). Le branchement transactionnel à la réparation réseau reste à faire ; aucun combat réel n'est validé.

> Incrément du 7 octobre 2026 : variante 0.4.16.12, protocole 48, empreinte schéma 14. Capture et transport des valeurs de contexte des buffs, vérification de leur chaîne et application sans recalcul aux contextes existants. Voir le [bilan des contextes](rtwp-buff-context-2026-10-07.md). Les créations de buffs absents ne sont pas encore branchées à la restauration transactionnelle ; aucune validation en combat réel.

> Incrément du 7 octobre 2026 : variante 0.4.16.11, protocole 47. Brique de création/enregistrement/annulation de représentations natives de buffs sans composants mécaniques, vérifiée hors jeu par tests et sous Mono. Elle n'est pas encore branchée à la réparation réseau : la restauration des buffs manquants reste à terminer. Voir le [bilan du registre de buffs](rtwp-buff-replica-store-2026-10-07.md). Aucun jeu ou outil de production des voix n'a été lancé.

> Incrément du 7 octobre 2026 : variante 0.4.16.10, protocole 47. Mise à jour des valeurs de buffs existants sans réactivation mécanique, identification des instances empilées, contrôle préalable des niveaux négatifs et protections au niveau de la collection de buffs. Voir le [bilan des valeurs de buffs](rtwp-buff-values-2026-10-07.md). La création/reconstruction des buffs manquants n'est pas encore implémentée ; aucun essai en jeu n'a été lancé.

> Incrément du 7 octobre 2026 : variante 0.4.16.9, protocole 46. Fermeture autoritaire en trois étapes avec état final vérifié, conservation de la dernière mort, retransmission et sauvegarde protégée. Prototype compilé, 594 tests réussis et diagnostic Mono hors jeu réussi pour le paquet exact. Voir le [bilan de fermeture du combat](rtwp-combat-end-2026-10-07.md). Ce travail remplace le nettoyage immédiat décrit dans les bilans précédents ; il ne prouve ni la récupération complète ni un combat réel. Le jeu reste fermé pendant la génération des voix.

> Dernier incrément du 7 octobre 2026 : variante 0.4.16.8, protocole 45. Transmission ordonnée des états vitaux, protection des écritures clientes, suppression de l'ancien chemin de mise à mort dans le prototype et conservation des participants sortis du combat. Voir le [bilan des résultats durables](rtwp-unit-results-2026-10-07.md). Les preuves restent hors jeu ; la campagne, les reconstructions d'entités et la reprise complète ne sont pas validées. Aucun jeu ne doit être lancé pendant la génération des voix.

> Mise à jour du 7 octobre 2026 : variante locale 0.4.16.7, protocole 44. Ressources et disponibilités de sorts répliquées, animations de sorts, représentation autoritaire des projectiles et nouvelle interception des callbacks de règles vérifiée hors jeu sous Mono. Aucune validation de combat dans le moteur. Voir le [bilan des callbacks](rtwp-rule-dispatch-2026-10-07.md), le [bilan des projectiles](rtwp-projectile-presentation-2026-10-07.md), le [bilan des ressources](rtwp-offline-progress-2026-10-07.md) et le [bilan des actions de sorts](rtwp-action-presentation-2026-10-07.md) pour les preuves et les limites. Aucun jeu ne doit être lancé pendant la génération des voix.

> Mise à jour du 4 octobre 2026 : dépôt retrouvé dans `D:\Codex\PathFinder\WotR-Multiplayer-RTwP`. Le prototype de présentation utilise désormais le protocole 41 et la version locale 0.4.16.3. Il reste désactivé par défaut et non validé en combat. Voir le [bilan de l'étape A](a0-presentation-prototype-2026-10-04.md). Les résultats et le protocole 38 décrits ci-dessous constituent la référence historique, pas le statut courant du prototype.

Date : 1er octobre 2026, Europe/Paris.  
Projet : `C:\AI\WotR-Multiplayer-RTwP`.  
Révision de référence : `9bbcb61361fa9a393bfc761e7552c264d973167d`.  
Protocole réseau : **38**. Variante locale : **0.4.16.1**, issue du mod stable **0.4.16**.

Décision d'architecture ajoutée le 1er octobre 2026 : **simulation du combat sur l'hôte uniquement, client chargé des ordres et de la présentation**. Cette décision décrit la cible de développement ; elle n'est pas encore implémentée dans le code de référence ci-dessus.

## 1. Objet et conclusion

Ce document sert de référence de reprise pour développer, vérifier et préparer une version utilisable du multijoueur temps réel avec pause de Pathfinder: Wrath of the Righteous. Il décrit les faits observés dans le dépôt, les limites connues et les travaux proposés. Les travaux proposés ne doivent pas être confondus avec des fonctionnalités déjà livrées.

**Périmètre décidé par Nicolas : campagne de base uniquement, sans DLC.** Les campagnes additionnelles, rencontres et mécaniques propres aux DLC sont exclues du développement et des critères de livraison. Les mécanismes de vérification du contenu installé restent utiles pour constater que les deux joueurs utilisent un environnement compatible ; cette décision ne demande pas de supprimer ou désinstaller des contenus sur leurs PC.

La variante possède une infrastructure réseau, des protections et des tests significatifs. Elle ne satisfait pas encore le critère d'un combat temps réel complet et fiable à deux joueurs. Les principaux manques sont l'autorité complète de l'hôte, le cycle de vie des actions et projectiles, et la reconstruction des entités et effets divergents. Une réparation qui finit en pause permanente protège les sauvegardes, mais ne constitue pas un repli jouable.

La prochaine preuve décisive est une rencontre simple exécutée par deux véritables instances du jeu, avec observation des deux états et récupération après une divergence volontaire. Le développement ne doit plus être piloté seulement par le nombre de tests ou de petits correctifs.

### 1.1 Architecture retenue par Nicolas

1. **Le PC de Nicolas calcule le combat.** Il valide les ordres, décide de l'IA et de leur enchaînement, effectue les jets, applique les dégâts, soins, ressources, buffs, morts, invocations, zones et impacts de projectiles.
2. **Le PC de sa nièce transmet ses ordres.** Il envoie les intentions des personnages qui lui sont attribués : déplacement, attaque, sort, arrêt/remplacement d'action et demande de pause. L'hôte les accepte ou les refuse avec une réponse explicite.
3. **Le PC de sa nièce reçoit les résultats et les affiche, avec les animations correspondantes.** Il maintient une représentation locale du monde à partir des données de l'hôte, anime les personnages, effets et projectiles, actualise l'interface et joue les sons et voix nécessaires.

Le client conserve la caméra, la sélection, l'interface, le rendu et les interactions utilisateur. Il ne calcule aucun résultat de gameplay faisant autorité pendant le RTwP. Une animation d'attaque, un contact visuel de projectile ou la fin apparente d'un buff ne doit jamais produire localement des dégâts, un jet, une dépense de ressource ou une décision d'IA.

| Responsabilité | Hôte : PC de Nicolas | Client : PC de sa nièce |
| --- | --- | --- |
| Commandes | Contrôle propriétaire, cible, ressources et validité ; exécute ou refuse | Transmet l'intention et affiche son acceptation, son attente ou son refus |
| IA et règles | Calcule toutes les décisions et conséquences du combat | Applique les résultats reçus ; aucun calcul concurrent |
| Mouvement | Décide trajectoire et position logique | Affiche et interpole les positions confirmées |
| Animations et projectiles | Émet début, progression utile, interruption et résultat d'impact | Anime la représentation ; les callbacks visuels ne modifient pas le gameplay |
| Entités et effets | Attribue les identités et pilote création, modification et suppression | Crée/met à jour/supprime les représentations sans rejouer les effets mécaniques |
| Pause et temps | Applique la politique, fixe l'état de pause et le temps logique du combat | Demande la pause/reprise et suit l'état confirmé |
| Récupération | Fournit l'état de référence et le point de reprise des événements | Reconstruit sa représentation puis accuse réception de l'état vérifié |

Cette séparation vise d'abord le combat RTwP. Le chemin tour par tour existant reste conservé. Son activation après un repli exige une restauration cohérente des données et contrôleurs nécessaires sur le client ; il ne suffit pas de réactiver sa simulation sur une représentation visuelle incomplète.

### 1.2 Contrat de réplication à construire

Les points suivants sont des exigences proposées pour implémenter l'architecture, pas des messages déjà disponibles dans le protocole 38 :

- Associer chaque intention à la session, à l'époque du combat et à un identifiant de commande ; renvoyer une acceptation ou un refus motivé, sans exécuter deux fois une intention répétée.
- Émettre les événements de gameplay validés par l'hôte avec une séquence, les identités d'entités concernées et un repère de temps logique. Corréler les événements d'animation avec l'action ou le projectile correspondant.
- Donner à chaque état complet son point de référence dans le flux d'événements. Après une restauration, ignorer les événements déjà inclus et reprendre les suivants sans omission ni double application.
- Distinguer les changements durables (PV, ressources, création, suppression, impact) des mises à jour fréquentes de présentation (position/orientation). Une optimisation du transport ne doit pas supprimer un événement durable.
- Détecter les trous dans le flux et demander les événements manquants ou un état complet. En attendant, conserver un état contrôlé ; ne jamais remplacer un résultat absent par un calcul de combat autonome du client.
- Lisser uniquement la présentation. Pour le premier prototype, aucune prédiction locale produisant des conséquences de gameplay ; les choix de fréquence et de tampon d'affichage seront fondés sur des mesures.
- Encadrer l'application des données reçues pour empêcher les callbacks Unity de rejouer des règles ou de retransmettre les mêmes ordres. Les animations doivent pouvoir démarrer, s'interrompre et se terminer indépendamment de la simulation des dégâts.

La faisabilité décisive est l'isolation de cette présentation dans WotR compilé : identifier les contrôleurs et callbacks à désactiver ou adapter chez le client, tout en préservant les vues, l'interface et le son. C'est l'objet du premier lot de travail.

### 1.3 Bibliothèque réseau et éléments réutilisables

Réutiliser initialement le transport et les services existants du mod : connexion IP, lobby, attributions, séquences, diagnostics, traduction et paquet local. La décision porte sur la responsabilité du calcul, pas sur un remplacement imposé de bibliothèque réseau.

Un outil tel que Unity Netcode ou une solution similaire ne sera introduit que si une étude ciblée démontre un gain concret et sa compatibilité avec le jeu compilé. Il ne remplace pas l'adaptation des règles et du cycle de vie de WotR. Ne pas lancer une migration générale de transport avant la preuve du client de présentation.

## 2. Usage prévu et contraintes

- Deux joueurs Windows : Nicolas hôte, sa nièce cliente, connexion par IP directe.
- Personnages attribués aux joueurs par le lobby existant.
- Temps réel avec pause, avec conservation du tour par tour et changement de mode coordonné.
- Interface multijoueur française et coexistence avec le doublage `WOTR_AIVO`.
- Travail et distribution entre ces deux PC strictement locaux ; aucune publication publique prévue. Le dossier du projet signale l'absence de licence amont ; toute redistribution publique serait un chantier séparé.
- La disponibilité de la nièce est limitée : réaliser d'abord le développement et les vérifications locales possibles, préparer une session finale courte et reproductible.
- Le débit fibre n'est pas un critère suffisant : mesurer délai, régularité des échanges et fidélité de la représentation cliente à l'état de l'hôte. Aucune nouvelle mesure WAN n'a été réalisée pour ce rapport.
- Ne jamais modifier `C:\AI\WotR-FR-Voice`, ses WAV, sa base, son casting, son gestionnaire ou ses banques pour ce chantier.
- Préserver le mod stable installé et les sauvegardes originales. Tout futur essai de jeu doit employer un environnement et des copies de sauvegardes isolés selon l'autorisation applicable à ce moment-là.
- Aucun dialogue JavaScript natif ; confirmations et notifications intégrées à l'interface concernée.

## 3. État de référence et portée des preuves

| Élément | État constaté | Limite de la preuve |
| --- | --- | --- |
| Dépôt | Propre à la révision de référence avant ajout de ce rapport | Recontrôler avant une reprise |
| Compilation et tests | Dernière exécution de développement réussie : 33 tests de traduction, 34 de réseau, 340 du projet principal, soit 407 | Total global, pas 407 combats ni 407 scénarios RTwP |
| Traduction multijoueur | Contrôle précédent : 217 clés françaises sur 217, paramètres conformes | Ne prouve pas la qualité de chaque écran en jeu |
| Mod stable | Slot `00_WOTRMultiplayer`, 0.4.16, actif | Aucune régression du tour par tour ne peut être exclue sans essai du code expérimental |
| Variante | Préparée hors du dossier `Mods`, désactivée | Aucun déploiement de campagne validé |
| Paquet local | Empreinte `11C89AA7877DAD0C085440B28A517DD73EEE20D85702CD825046EB42C7DF93F2` | Une modification du contenu exige une nouvelle empreinte |
| Validation de livraison | `local-artifacts/RTWP_RUNTIME_VALIDATED.json` absent | Ne pas fabriquer ce fichier pour contourner le verrou |
| Démarrage avec doublage | Essai antérieur du protocole 38 : deux mods chargés, menus FR initialisés et banques vocales chargées | Cet essai concernait le paquet précédent, pas l'empreinte actuelle ; aucun combat à deux ni écoute validée |
| Réseau réel | Test TCP local de sérialisation présent et précédemment réussi | Un échange TCP entre services n'est pas deux moteurs Unity en combat |
| Campagne | Aucun parcours complet validé pour le RTwP | Aucun acte ou chemin mythique ne doit être annoncé compatible par déduction |

Le lancement commun avec le doublage est documenté dans [rtwp-voice-compatibility.md](rtwp-voice-compatibility.md). Le journal [rtwp-local-development.md](rtwp-local-development.md) contient des étapes historiques : certaines décrivent d'anciens protocoles et ne doivent pas être lues comme un état actuel uniforme.

Un avertissement de dépendance `NU1903` concernant AutoMapper 10.1.1 apparaissait lors de la dernière compilation. Son traitement doit être évalué avec les contraintes du jeu et du framework ; une mise à niveau aveugle ne constitue pas une correction vérifiée.

## 4. Comparaison avec le plan initial

| Exigence | Avancement réel | Reste à démontrer ou développer |
| --- | --- | --- |
| Deux modes de combat | Modèles, préférence et changement de mode codés | Passage fiable dans un véritable combat |
| Autorité de l'hôte | Ordres validés, choix d'IA, certains jets, identifiants et morts synchronisés | Migration vers une simulation uniquement hôte et un client de présentation, selon §1.1 |
| Droits de contrôle | Validation du propriétaire et protections contre les messages incohérents | Contrôle réel des compagnons, invocations et montures concernés |
| Séquences et époques | Présentes avec rejet des messages anciens et tests | Cohérence de tous les événements après transition, reconnexion et interruption |
| Trois politiques de pause | Codées et traduites | Pause simultanée, votes, déconnexion et reprise dans Unity |
| Barrière de changement de mode | Préparation, accusés, validation, abandon, retransmissions et fin coordonnée | Fin réelle des actions atomiques et effets scriptés |
| Empreintes | Nombreux champs d'unités, buffs, zones et historique d'ordres | Vérifier l'état répliqué et le point atteint dans les événements ; distinguer les données internes à l'hôte |
| Réparation automatique | Détection, pause, application d'état partiel, vérification et repli codés | Reconstruction complète et reprise jouable après entité manquante |
| Sauvegardes sûres | Blocage pendant barrières et combat RTwP actif, protections après déconnexion | Sauvegarde/chargement après réparation ou repli effectivement vérifié |
| Interface française | Catalogue et contrôles présents | Lisibilité, qualité des messages, aucune action silencieusement perdue |
| Compatibilité de connexion | Jeu, protocole, empreinte et vérifications de contenu existantes | Essai réel avec les installations exactes des deux joueurs, sans DLC |
| Doublage français | Audit statique et démarrage commun | Écoute des dialogues et voix de combat pendant une session multijoueur |
| Paquet local et retour stable | Outils, séparation des slots et verrou de validation présents | Répétition de l'installation/retour sur le PC client |
| Campagne complète | Non validée | Matrice de rencontres du jeu de base uniquement |

## 5. Lacunes techniques prioritaires

### 5.1 Remplacer l'architecture hybride par un client de présentation

Le client continue à exécuter des règles locales. Certains jets sont remplacés par les résultats de l'hôte, mais cela ne garantit pas l'égalité des conséquences : immunités, cibles disponibles, ordre des événements, dégâts périodiques, ressources consommées et effets déclenchés peuvent différer.

Dans `MultiplayerClient.ResolveRealTimeDiceRoll`, le générateur local avance, puis le client attend le résultat de l'hôte jusqu'à 2 500 ms. Une différence demande un repli protégé ; un résultat absent utilise encore le résultat local pour terminer le calcul déjà engagé, puis signale le défaut. Ce comportement appartient à l'architecture hybride actuelle et doit disparaître du chemin RTwP cible : le client reçoit les conséquences des règles et les informations de présentation nécessaires, sans lancer son propre calcul de combat ni attendre un jet au milieu d'une règle locale. Préserver le comportement tour par tour tant que sa migration n'est pas demandée.

À faire :

1. Inventorier les événements qui modifient réellement le combat, avec leur producteur et leurs consommateurs.
2. Pour chaque catégorie, identifier le point d'exécution sur l'hôte, les données de résultat à transmettre, le point d'application client et les callbacks mécaniques à neutraliser. L'exécution déterministe concurrente du combat sur le client n'est plus une option cible.
3. Couvrir dégâts, soins, ressources, morts, buffs, zones, invocations, réactions et décisions d'IA sans double application.
4. Corréler les résultats à des identifiants d'événements stables, pas seulement à l'ordre supposé des appels locaux.
5. Vérifier qu'une réparation ne peut pas valider des PV corrects en laissant des ressources ou effets secondaires incohérents.

Critère : la conséquence est calculée une seule fois par l'hôte et appliquée une seule fois à l'état répliqué du client. L'instrumentation confirme l'absence de jets, choix d'IA et effets mécaniques autonomes chez le client pendant le RTwP ; une application impossible conduit à une récupération explicite et vérifiable.

### 5.2 Invocations, zones et buffs : reconstruction manquante

`CombatInteractionService.ApplyRealTimeCombatState` refuse les unités ou zones absentes, celles en trop et plusieurs incohérences d'origine avant de modifier l'état. C'est une protection utile ; aucune reconstruction générale n'est fournie par cette vérification.

`BuffInteractionService` contient une liste très limitée de buffs recréables : deux entrées principales, avec un effet associé. La plupart des buffs absents ne peuvent donc pas être réparés par le service actuel.

Le document [rtwp-entity-reconstruction.md](rtwp-entity-reconstruction.md) établit les difficultés du cycle de vie du jeu : sérialiser une entité par les procédures de sauvegarde peut déclencher des mutations ; recréer une zone à partir du seul blueprint peut rejouer ses effets. Il faut préserver le contexte mécanique, les références, l'identité, la durée et les liens entre entités.

À faire : capturer les informations de création sur l'hôte, construire une restauration contrôlée en pause, résoudre les références, empêcher les effets en double, vérifier l'état final et annuler proprement les créations ratées. Prévoir également les entités présentes seulement chez le client.

Dans la nouvelle architecture, la création et la suppression des représentations clientes sont nécessaires au fonctionnement normal, pas seulement à la réparation. Définir les données minimales pour afficher/sélectionner une entité et celles nécessaires à une reprise en tour par tour. Ne pas utiliser un spawn qui rejoue localement l'invocation, l'aura ou les dégâts déjà calculés par l'hôte.

Critère : une invocation, une zone et un buff manquants sont rétablis avec leurs propriétés effectives. Un échec laisse un état diagnostiquable et permet un chemin de récupération documenté. Une pause infinie ne satisfait pas le critère campagne.

### 5.3 Actions actives, interruptions et projectiles

`RealTimeAcceptedOrderLedger` conserve les 32 derniers ordres acceptés. Il décrit des messages reçus, pas les commandes actuellement exécutées par Unity. Un ordre identique peut avoir été terminé, interrompu ou remplacé différemment.

`ProjectilesPatches` synchronise notamment le point de chute d'un projectile raté. Il ne constitue pas une réplication complète du cycle départ/vol/impact/annulation et de ses effets.

Le chemin client intercepte certaines commandes et bloque les commandes non prises en charge. Il faut établir une liste explicite des actions réellement disponibles et vérifier les commandes usuelles : arrêt, remplacement, lancement de sort, objets utilisables, attaques et mouvement. Ne pas conclure qu'une classe bloquée implique nécessairement qu'une fonction entière est absente : certaines actions passent par d'autres hooks.

Critère : ordres simultanés, interruption, mort de la cible avant impact et changement de mode ne laissent aucune action fantôme, aucun impact double et aucune ressource consommée sur un seul PC.

La nouvelle voie d'exécution cliente doit appliquer les résultats et déclencher la présentation correspondante. Rejouer directement une commande de combat complète avec `ExecuteRealTimeCombatCommand` ne suffit pas si cette commande déclenche à nouveau des règles. L'impact visuel d'un projectile doit suivre le résultat reçu de l'hôte, y compris si celui-ci arrive avant ou après l'animation prévue.

### 5.4 Empreintes et horloges

La classe `RealTimeCombatSnapshot` possède un champ `RandomState`, mais les constructions de snapshots observées dans les acteurs ne renseignent pas l'état réel du générateur. La présence de ce champ n'est donc pas une preuve de contrôle des graines ou du flux aléatoire.

L'empreinte structurelle exclut volontairement plusieurs horloges ; un contrôle temporel séparé existe avec tolérance. Il faut distinguer le décalage normal lié au transport d'une véritable divergence, et comparer les états à un instant logique défini.

À faire : définir les données nécessaires à la continuité du combat, leur mode de comparaison et leur procédure de réparation. Documenter explicitement les champs exclus. Vérifier une divergence de ressource ou de commande, et pas seulement position/PV/buff.

Avec une simulation uniquement hôte, le client n'a pas à reproduire le générateur aléatoire de combat. Ne pas consacrer la nouvelle architecture à rendre deux graines identiques : vérifier l'état logique répliqué et les événements appliqués au même point de référence. Conserver les données internes de hasard côté hôte si elles sont nécessaires à sa continuité ou à ses diagnostics. Les positions interpolées et horloges purement visuelles doivent être séparées des données canoniques comparées.

### 5.5 Barrières, pause et repli réellement jouable

Les échanges de barrière sont déjà bien couverts par les tests de protocole. Le point restant concerne surtout leur raccordement au jeu : les projectiles et commandes non liées au mouvement sont attendus avant certains changements de mode, mais cela ne couvre pas automatiquement tous les effets scriptés ni tous les événements de fin d'action.

À faire : vérifier la pause à un point cohérent, empêcher de nouveaux événements incompatibles pendant la transition, borner les attentes et afficher leur cause. Une perte ou duplication de message doit conserver un seul mode logique. Le repli doit repartir sur un état réparé ; basculer seulement le réglage tour par tour ne suffit pas.

Le passage RTwP → tour par tour doit aussi restaurer les contrôleurs et données mécaniques du client avant leur réactivation. Le passage inverse doit empêcher toute simulation cliente concurrente avant la reprise du flux RTwP. Tester explicitement ces changements de responsabilité.

### 5.6 Sauvegarde, chargement et reconnexion

La politique actuelle bloque les sauvegardes durant tout combat RTwP actif, même si le dernier contrôle correspond. Conserver cette restriction tant que l'état complet n'est pas maîtrisé.

À vérifier : refus effectif d'une sauvegarde pendant divergence, sauvegarde synchronisée après repli validé, chargement cohérent, déconnexion en combat et reconnexion à l'état courant de l'hôte. Une reconnexion doit établir une nouvelle session logique et écarter les commandes de la précédente.

### 5.7 Jeu de base, UI et voix

Inclure les mécaniques du jeu de base présentes dans la campagne des joueurs : compagnons, capacités mythiques concernées, montures, invocations, renforts, pièges, métamorphoses, dialogues et transitions. Les combats de croisade doivent conserver leur fonctionnement tour par tour ; il n'est pas nécessaire de transformer leur gameplay en RTwP.

Un TODO subsiste notamment sur la synchronisation de dégâts de pièges dans `TrapCastSpellPatches`. Cela justifie un scénario ciblé du jeu de base, sans déduire de ce seul commentaire l'échec de tous les pièges.

Pour la voix, tester la sélection, les dialogues, les répliques de combat, la pause et la reprise. `WOTR_AIVO` signalait l'absence de son propre catalogue UI français lors du démarrage observé ; c'est distinct de la traduction multijoueur et du chargement des voix. Consigner ce point sans modifier le projet de doublage.

La télémétrie actuelle rapporte des délais de réponses applicatives et des réponses de snapshots manquantes. Ne pas la présenter comme une mesure exacte de perte de paquets TCP.

## 6. Ordre de travail proposé et portes de validation

| Lot | Livrable concret | Condition pour avancer |
| --- | --- | --- |
| A0 — Faisabilité du client de présentation | Carte des contrôleurs/callbacks et prototype séparant une attaque de son affichage | IA et règles clientes neutralisées pour ce cas, animations/UI conservées et preuve instrumentée |
| A — Combat minimal sur l'hôte | Deux instances réelles : intentions clientes, validation hôte, résultats répliqués et animations | Déplacement, attaque, PV, mort et pause ; aucun résultat de combat calculé indépendamment par le client |
| B — Continuité et présentation | Sort ciblé, ressources, interruptions et projectiles pilotés par les événements hôte | Combat simple complet ; affichage cohérent malgré délai et événements tardifs |
| C — Entités et reconstruction | Création/suppression normales et restauration d'une invocation, zone et buff | Aucun effet local rejoué ; divergences injectées réparées ou récupération jouable démontrée |
| D — Transition et reprise | Changement de mode en action, repli, sauvegarde après récupération, reconnexion | Deux états cohérents et poursuite de partie après chaque scénario |
| E — Campagne de base | Matrice de rencontres et mécaniques, correction des incompatibilités prioritaires | Aucune situation testée ne laisse une sauvegarde incohérente ou un blocage sans issue documentée |
| F — Paquet candidat | Paquet identique pour les deux PC, notice française, procédure de retour, preuves associées | Session réelle avec Nicolas et sa nièce, doublage actif et bilan enregistré |

Les lots peuvent nécessiter des retours ciblés, mais chacun doit produire une preuve observable. Ne pas engager une reconstruction générale de toutes les mécaniques avant d'avoir vérifié la faisabilité du combat minimal.

Les correctifs de l'ancienne architecture restent des protections transitoires. Ne pas multiplier les contournements de simulation déterministe cliente au détriment des lots A0/A. Le protocole 38 et son paquet décrivent l'état de départ ; une modification incompatible des messages ou de leur sens exigera un protocole révisé et un contrôle de compatibilité. Ne pas réutiliser une preuve de validation de l'ancienne architecture pour certifier la nouvelle.

Pour le lot A, étudier d'abord deux instances locales isolées. Vérifier que le jeu, son lanceur, les ports et les répertoires utilisateurs le permettent. Ne pas supposer qu'un second processus suffit : profils, sauvegardes, paramètres et journaux doivent rester distincts. Si cela n'est pas praticable sur le poste, documenter précisément la limitation et préparer l'essai sur un second PC. Cette possibilité n'a pas été validée dans le présent rapport.

## 7. Matrice de tests à exécuter

Légende : « présent » signifie qu'un test de code/protocole existe, pas qu'un essai moteur a réussi. Tous les scénarios moteur ci-dessous restent à attester pour un paquet candidat.

| ID | Scénario | État des preuves locales | Résultat attendu en jeu |
| --- | --- | --- | --- |
| T01 | Connexion IP, mêmes versions et paquet | Comparaison de contenu et test TCP présents | Lobby utilisable et personnages attribués |
| T02 | Version ou empreinte différente | Tests de compatibilité présents | Refus expliqué avant transfert de sauvegarde |
| T03 | Deux déplacements simultanés puis attaque | Test avec acteurs simulés présent | Ordres et positions cohérents, aucune commande perdue |
| T04 | Attaque, dégâts, soins, mort | Tests de messages et états partiels | Résultats et conséquences identiques |
| T05 | Sort ciblé et consommation de ressource | Validation moteur manquante | Même effet et même ressource restante |
| T06 | Projectile retardé, cible morte ou déplacée | Couverture moteur manquante | Impact ou annulation unique et cohérente |
| T07 | Zone persistante, entrée/sortie, dégâts périodiques | Contrôles d'état présents | Occupants, ticks et expiration cohérents |
| T08 | Invocation, disparition et invocation manquante | Détection présente, reconstruction manquante | Restauration correcte ou récupération documentée |
| T09 | Buff absent, buff en trop, durée et rang divergents | Détection/réparation partielle | Aucun double effet ; état effectif rétabli |
| T10 | Interruption, attaque d'opportunité, monture | Empreintes partielles, moteur non validé | Actions et ressources cohérentes |
| T11 | Trois politiques de pause, demandes simultanées | Tests de coordination présents | Reprise autorisée uniquement selon la politique |
| T12 | Changement de mode pendant action et projectile | Tests de barrière présents | Aucun mélange des modes ; attente bornée |
| T13 | Messages retardés, dupliqués et réordonnés | Simulation de transport présente | Pas de double application ni ancien état réappliqué |
| T14 | Divergence position/PV/buff/ressource ou événement manquant injectée | Tests partiels et réparation simulée | Reconstruction depuis l'hôte au point de référence correct ; aucun calcul client de remplacement |
| T15 | Entité irréparable | Test du maintien en pause présent | Issue de récupération explicite ; pas de fausse réussite |
| T16 | Sauvegarde pendant défaut, puis après réparation | Gardes de code présentes | Refus pendant défaut, sauvegarde cohérente ensuite |
| T17 | Déconnexion/reconnexion pendant combat ou barrière | Protections partielles présentes | Pas d'ancienne commande ; reprise depuis l'hôte |
| T18 | Renforts, pièges, cinématique et transition d'acte | Non validé | Événements uniques, session récupérable |
| T19 | Combat de croisade et retour à l'exploration | Réutilisation du chemin existant | Aucune régression provoquée par la variante |
| T20 | Doublage : sélection, dialogue, combat, pause | Audit et démarrage commun seulement | Voix audibles, aucun dialogue bloqué |
| T21 | Installation identique et retour stable | Outil local existant | Retour reproductible sur les deux PC |
| T22 | Instrumentation des règles et de l'IA clientes en RTwP | Nouveau critère, non implémenté | Zéro décision, jet ou conséquence de gameplay autonome ; application des résultats hôte traçable |
| T23 | Animation d'attaque et impact visuel sans événement de résultat | Nouveau critère, non implémenté | Animation possible, aucun dégât ni ressource consommée localement |
| T24 | Ordre client refusé, répété ou devenu invalide | Validation partielle existante | Réponse explicite ; une seule exécution hôte, UI cliente cohérente |
| T25 | Résultat tardif et interruption pendant animation | Nouveau critère, non implémenté | Présentation corrigée, pas de résultat inventé par le client |
| T26 | État complet puis événements déjà inclus ou manquants | Nouveau critère, non implémenté | Reprise exacte au point annoncé, sans effet doublé ni omis |
| T27 | Passage client de présentation ↔ simulation tour par tour | Barrières existantes, nouvelle transition non validée | Réactivation des bons contrôleurs sur un état complet, aucune simulation concurrente |

Tester les perturbations réseau à plusieurs niveaux : tests déterministes du protocole, TCP réel, puis vrai moteur. TCP conserve normalement l'ordre du flux ; les duplications et réordonnancements injectés dans les tests applicatifs vérifient surtout la robustesse des handlers. Ne pas assimiler ces simulations à une validation WAN.

La matrice campagne doit couvrir les actes effectivement parcourus, les rencontres scénarisées et les capacités des personnages choisis. Le prologue/labyrinthe est un candidat déjà utilisé pour des essais solo, pas une preuve de combat RTwP. Ne pas prétendre couvrir tous les chemins mythiques à partir d'une seule partie. Aucun DLC n'entre dans cette matrice.

### Fiche de résultat à remplir pour chaque scénario

```text
ID du scénario :
Date, lieu/rencontre et acte du jeu de base :
Révision, protocole et empreinte du paquet :
Version du jeu et liste des mods sur les deux instances :
Configuration : deux instances locales / deux PC / réseau utilisé :
État initial et copie de sauvegarde de test identifiée :
Personnages, capacités, montures et invocations concernés :
Actions exécutées et perturbation éventuellement injectée :
Résultat observé sur l'hôte :
Résultat observé sur le client :
Preuve de calcul uniquement hôte et compteurs de règles clientes :
Séquence/temps logique de référence et événements appliqués :
Animations, impacts visuels, sons et éventuels retards :
Empreintes avant/après et différences détaillées :
Réparation, repli et poursuite de partie :
Sauvegarde/chargement après récupération :
Journaux et captures associés :
Verdict : réussi / échoué / non exécuté / non applicable :
Limites et correctif requis :
```

## 8. Carte du code pour la reprise

Tous les chemins suivants sont relatifs à la racine du projet.

| Responsabilité | Fichiers principaux |
| --- | --- |
| Orchestration hôte/client | `src/WOTRMultiplayer/Services/MultiplayerHost.cs`, `MultiplayerClient.cs`, `Multiplayer.cs` |
| État, modes, séquences et pause | `src/WOTRMultiplayer/Entities/Combat/RealTime/`, `Services/RealTimeCombatCoordinator.cs`, `RealTimePauseCoordinator.cs` |
| Exécution et application dans Unity | `src/WOTRMultiplayer/Services/GameInteraction/CombatInteractionService.cs` |
| Buffs et niveaux négatifs | `src/WOTRMultiplayer/Services/GameInteraction/BuffInteractionService.cs` |
| Préflight, empreintes et diagnostic | `Services/RealTimeCombatEntityPreflight.cs`, `RealTimeCombatHasher.cs`, `RealTimeCombatTemporalDiff.cs`, `RealTimeCombatDiff.cs` sous `src/WOTRMultiplayer/` |
| Ordres et permissions | `Services/RealTimeCommandGate.cs`, `RealTimeCombatCommandValidator.cs`, `RealTimeAcceptedOrderLedger.cs`, `HarmonyPatches/Combat/UnitCommandsPatches.cs` sous `src/WOTRMultiplayer/` |
| Règles et résultats aléatoires | `Services/MultiplayerRollProcessor.cs`, `RealTimeRuleRollMailbox.cs`, `RealTimeVector2Authority.cs`, `RealTimeVector3Authority.cs`, `HarmonyPatches/Rolls/` sous `src/WOTRMultiplayer/` |
| IA, projectiles et zones | `src/WOTRMultiplayer/HarmonyPatches/Combat/AiBrainControllerPatches.cs`, `ProjectilesPatches.cs`, `AreaEffectEntityPatches.cs` |
| Protection des sauvegardes | `src/WOTRMultiplayer/HarmonyPatches/Combat/SaveManagerRealTimePatches.cs` et propriétés des acteurs |
| Interface | `src/WOTRMultiplayer/UI/Controllers/LobbyWindowController.cs`, `localization/frFR.json` |
| Compatibilité et empreinte de paquet | `src/WOTRMultiplayer/Services/RealTimeContentCompatibility.cs`, `RealTimeBuildFingerprint.cs` et contrôles de contenu des acteurs |
| Tests du protocole simulé | `src/WOTRMultiplayer.UnitTests/Services/RealTimeProtocolIntegrationTests.cs` |
| Transport TCP réel | `src/WOTRMultiplayer.Networking.UnitTests/RealTimeTcpLoopbackTests.cs` |
| Compilation, paquet, diagnostic et slots | `tools/rtwp-local.ps1`, `tools/check-localization.ps1`, `tools/rtwp-fingerprint-probe.ps1` |

## 9. Vérifications et commandes locales

Depuis `C:\AI\WotR-Multiplayer-RTwP` :

```powershell
# État des modifications et contrôle des espaces.
git status --short
git diff --check

# Compilation, tests de solution et contrôle du français, sans activation.
.\tools\rtwp-local.ps1 -Action Build

# Consultation des slots et de l'empreinte du paquet.
.\tools\rtwp-local.ps1 -Action Status

# Préparation d'un paquet de test hors du dossier Mods, jeu fermé.
.\tools\rtwp-local.ps1 -Action StageTest -AllowUnvalidatedTest

# Collecte locale des journaux ; aucune transmission extérieure.
.\tools\rtwp-local.ps1 -Action CollectDiagnostics
```

Pour un test ciblé, renseigner le chemin des assemblies du jeu dans le processus courant :

```powershell
$env:WOTR_PATH = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure'
dotnet test src\WOTRMultiplayer.UnitTests\WOTRMultiplayer.UnitTests.csproj --configuration Release --filter 'FullyQualifiedName~RealTime'
```

L'absence de `WOTR_PATH` peut provoquer une erreur de référence `Newtonsoft.Json` dans le générateur de traduction. Le script `Build` renseigne cette variable.

`StageTest` prépare un paquet et conserve l'ancien paquet expérimental ; il ne valide pas son fonctionnement en jeu. Les actions normales d'installation et d'activation restent soumises à la validation du paquet exact. Les possibilités techniques de contournement pour essais ne constituent pas une autorisation d'activation automatique.

Exécuter les tests ciblés après une modification pertinente, puis la suite complète et les contrôles requis avant le paquet candidat. Ne pas répéter une suite inchangée sans raison. Ne pas mettre à jour le nombre de tests annoncé sans consulter le résultat effectif.

## 10. Critères de livraison et décisions ouvertes

### Candidat pour une session à deux

- [ ] Rencontre ordinaire complète avec les deux instances réelles, déplacement, attaque, sort, mort et pause.
- [ ] Autorité des résultats et consommation des ressources vérifiées pour les actions proposées.
- [ ] Client de présentation vérifié : aucun calcul autonome de règles/IA en RTwP, animations et interface fonctionnelles.
- [ ] Intention refusée expliquée à la cliente ; résultats tardifs appliqués sans effet double et sans résultat local de remplacement.
- [ ] Divergence volontaire effectivement réparée ou repli jouable démontré.
- [ ] Aucun silence prolongé : l'interface indique attente, erreur et issue possible.
- [ ] Sauvegarde empêchée pendant l'état divergent ; chargement après récupération vérifié.
- [ ] Paquet reproductible, identité des deux installations contrôlée et retour stable documenté.
- [ ] Français vérifié sur les écrans utilisés et coexistence du doublage testée au niveau approprié.

### Version habituelle pour la campagne de base

- [ ] Session complète à deux PC avec le paquet exact et le doublage actif.
- [ ] Scénarios obligatoires et matrice des mécaniques pertinentes passés, avec preuves conservées.
- [ ] Aucune divergence irrécupérable ni sauvegarde incohérente durant cette validation.
- [ ] Déconnexion, reconnexion et poursuite après repli vérifiées.
- [ ] Incompatibilités connues documentées avec leur comportement de récupération.
- [ ] Portée de la validation annoncée précisément : rencontres et configurations testées, sans garantie universelle « sans bug ».
- [ ] Marqueur de validation associé à l'empreinte réellement testée ; toute modification ultérieure réévalue cette validité.

La répartition des rôles est décidée : l'hôte calcule, le client transmet les ordres et présente les résultats. Restent à instruire les moyens techniques d'isoler les animations des règles dans WotR, la faisabilité de deux instances locales isolées, le format des événements et états répliqués, la reconstruction et le retour au tour par tour, puis les capacités et chemins mythiques utilisés dans cette campagne. Ces inconnues n'empêchent pas l'analyse et l'instrumentation locales ; elles empêchent une date ou un pourcentage de livraison crédible à ce stade.

## 11. Consignes de reprise pour le prochain développeur

1. Lire ce rapport puis contrôler la révision, les modifications présentes, le protocole et le paquet ; préserver tout travail plus récent.
2. Ne pas annoncer une fonctionnalité terminée d'après une propriété, un message réseau ou un test simulé seulement.
3. Appliquer l'architecture décidée en §1.1 ; reprendre par A0 puis A. Prouver que le client peut afficher le combat sans le recalculer avant d'étendre la couverture.
4. Pour chaque changement, conserver la distinction entre code implémenté, test automatisé et preuve dans le moteur réel.
5. Actualiser la matrice avec un résultat et ses preuves ; une hypothèse ou un essai non exécuté reste explicitement signalé.
6. Tenir le périmètre **sans DLC**, préserver le doublage et le stable, et ne publier aucun paquet à l'extérieur.
7. Donner à Nicolas des bilans sur les étapes jouables franchies et les blocages concrets. Un nouveau test ou une protection supplémentaire ne constitue pas à lui seul un jalon de livraison.

Ce rapport est un état de référence et un guide de développement. Il ne certifie pas la variante comme prête pour une campagne.
