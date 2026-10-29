# Essai local RTwP — 0.4.16.56 / protocole 70

Correctif local .120 : une montée de niveau ordinaire ou mythique distante n'ouvre plus CharGen, même masqué, et ne place plus l'autre joueur dans un écran modal. Les messages sont mis en file pendant que leur propriétaire choisit ses niveaux, puis appliqués par un contrôleur de niveau sans interface. L'autre joueur peut donc continuer à se déplacer ou ouvrir simultanément sa propre montée de niveau. La règle des réponses de dialogue reste inchangée : accord obligatoire des deux joueurs. Le bouton **Continuer** d'un dialogue scripté réémet automatiquement son accusé de lecture lorsqu'il a croisé l'initialisation de l'hôte. Les vitesses d'animation des tours joueur et non-joueur sont forcées à `3` sur les deux PC, le déplacement accéléré reste activé et le mod ne neutralise plus la touche Espace. Les corrections de remise en phase automatique du round et du personnage actif au début d'un combat sont conservées. Construction Release réussie le 28 octobre : 1 308 tests du mod, 163 tests réseau et 33 tests de localisation réussis ; protocole 89. Candidat hors jeu d'empreinte `29B03E5BFCA7BDC5FD31014F82816FE05B8B59E486CA824425C3D55B1B755810`. Même paquet obligatoire sur les deux PC. Aucune validation en jeu n'est encore revendiquée.

Correctif local .118 : une montée de niveau ordinaire ou mythique s'ouvre
uniquement chez le propriétaire. L'autre PC met désormais les actions en file
sans ouvrir la fenêtre ni attendre ses validations de phase ; si les deux
joueurs montent chacun un personnage en même temps, chaque machine rejoue ensuite
la progression distante dans une fenêtre masquée. Couverture automatisée de
l'ouverture propriétaire, du refus d'ouverture distante et de la reprise après
deux sessions simultanées. Le rendu et la réplication complète doivent encore
être confirmés sur deux PC physiques avant publication.

Version .56 : Échap → Salon multijoueur, bouton « Forcer la synchronisation » toujours affiché chez l'hôte, y compris hors combat avec indication de sa portée. Il relance les récupérations de combat existantes ; il ne termine pas le combat et ne reconstruit pas librement toute la carte hors combat. Boutons avec hauteur minimale et texte ajusté, fenêtre défilable à la molette, espace des joueurs adapté au nombre présent. Même ZIP sur les deux PC, jeu fermé. Compilation/tests hors jeu ; rendu réel à confirmer, aucune intervention sur le jeu en cours.

Version .55 : les mercenaires actifs du groupe restent dans le groupe contrôlable en capitale/taverne multijoueur. Les compagnons de scénario et les mercenaires en réserve restent exclus. Les restrictions scriptées, le contrôle par propriétaire et les gardes de sélection existantes sont conservés. Après fermeture de l'inventaire, la sélection revient à un personnage contrôlé localement, pas systématiquement au personnage principal. Même .55 sur les deux PC, installation jeu fermé. Compilation et tests hors jeu uniquement ; présence, déplacements partagés et transitions restent à confirmer en jeu.

Version .54 : dans le menu multijoueur de l'hôte, bouton **Forcer la synchronisation**, également visible pendant une barrière. En TB, relance l'initialisation manquante ou renvoie le checkpoint de début/fin de tour déjà attendu ; en RTwP, demande une réparation. Les confirmations restent obligatoires. Un clic répété est limité pendant trois secondes. Si une action est en cours ou qu'aucun checkpoint sûr n'existe, le bouton explique l'indisponibilité ; il ne garantit pas le déblocage. Il ne tue aucun ennemi, ne saute aucun tour et ne force pas la victoire. Diagnostic `[ManualSync]`. Installer la même .54 sur les deux PC, jeu fermé. Aucune validation en jeu par l'agent.

Correctif du 17 octobre : capture des buffs RT isolée du tour par tour, rapprochement des identifiants de buffs non ambigus, relance d'une initialisation de tour manquante et confirmation de chargement réémise. Reconnexion différée jusqu'à la fin du combat TB/dialogue/butin et au retour dans une zone locale, avec message d'attente. Les deux PC doivent employer cette même .53. Ne remplacer le paquet qu'une fois le jeu fermé. Aucun essai en jeu effectué par l'agent ; le repli jouable n'est pas garanti pour toutes les divergences.

Session du 14 octobre : réconciliation des buffs natifs avant bascule depuis le tour par tour ; potion pour autrui résolue depuis l'objet exact ; détection des ennemis par le groupe transmise ; retrait contrôlé des doublons locaux explicitement exclus par l'hôte ; remplacement du dispatcher Unity appelé depuis le réseau par une file dépilée sur le thread du jeu. Les diagnostics sont détaillés dans `docs/rtwp-incidents-session-2026-10-14.md`. Installer le même ZIP .52 sur les deux PC, jeu fermé. Compilation uniquement : les cinq scénarios restent à confirmer, notamment l'affichage de la nuée. Aucun jeu lancé ou paquet installé par l'agent.

Retour multi → menu → solo → menu : reconstruction des contrôleurs du menu quand leurs objets Unity ont été détruits. Correction de l'exception au clic Multi et protection contre le nettoyage tardif d'une ancienne fenêtre. Même ZIP .51 sur les deux PC ; jeu fermé pour installer. Compilation seule, parcours à confirmer en jeu.

Mode global comme en solo : T et le bouton de combat changent le mode conservé pour les combats suivants. Hors combat, le réglage réel du jeu est également appliqué aux deux PC. Le bouton est rafraîchi depuis ce réglage après application, jamais depuis une demande encore en attente. Seul l'hôte décide ; à deux, la barrière réseau reste nécessaire. Une pause manuelle n'empêche plus la bascule. Cela remplace l'ancienne distinction entre préférence et mode temporaire. Installer le même ZIP .50 sur les deux PC, jeu fermé. Compilation uniquement : affichage réel et bascule restent à confirmer en jeu.

Fin de combat solo : suppression de la barrière encore créée par la .48 avant sa vérification locale. Quand le moteur annonce la fin et qu'aucune cliente/reconnexion/barrière n'est en attente, le mod suit directement la fin normale sans drainage réseau des projectiles. À deux, validation inchangée. Tous les correctifs précédents sont inclus ; installer le même ZIP .49 sur les deux PC. Compilation seule, reste à confirmer en jeu.

Hôte seul : fin de combat sans attente d'une cliente absente ; T bascule dans les deux sens sans attendre la fin de toutes les attaques. En synchronisation, une pression sur T conserve le dernier mode demandé ; à deux, son application reste soumise à la barrière et aux confirmations, pas forcément instantanée. Installer le même ZIP UMM .48 sur les deux PC. Tous les correctifs précédents sont inclus. Compilation uniquement, reste à confirmer en jeu.

Carte globale : vérification et réparation visuelle du marqueur de rencontre, recentrage si hors écran, attente des fenêtres bloquées et reconstruction des fenêtres de collecte/arrivée manquantes. Messages anciens rejetés ; collecte confirmée protégée contre répétition. Installer le même ZIP UMM .47 sur les deux PC. Les correctifs .45 et .46 ci-dessous sont inclus. Compilation seulement, aucun jeu lancé ; noter l'heure si le problème revient, journaux `[MapEvent]`.

Montée de niveau (.46 historique) : écran visible uniquement pour le joueur propriétaire du personnage (familiers : propriétaire du maître). L'autre PC conserve la réplication en arrière-plan, sans animation ni son d'ouverture/fermeture. L'hôte récupère l'accès quand le propriétaire n'est plus connecté. Cette ancienne limitation sur les montées simultanées est remplacée par le correctif .118 décrit en tête du document.

Invocations : reproduction des sorts déjà commencés corrigée (position du lanceur, trajet vide, veto local de budget avant démarrage). Installer le même ZIP UMM .45 sur les deux PC. Compilation seulement : vérifier notamment une invocation après déplacement en tour par tour, présence des trois créatures et passage du tour. Journaux dédiés `[AbilityReplay]`. Aucun jeu lancé par l'outil.

Dialogue : le premier vote et un changement de réponse restent silencieux. Seul un nouveau clic sur la même réponse en attente affiche un rappel court de deux secondes. L'accord à deux et le vote seul après déconnexion restent conservés. Cette version inclut le ping et les correctifs précédents.

Ajout : Ctrl gauche/droit + clic gauche dans le monde 3D envoie un ping partagé, bleu pour l'hôte et orange pour la cliente, avec son intégré et disparition après trois secondes même en pause. Le clic ne donne aucun ordre. Installer le même ZIP .43 sur les deux PC. Pas de nouveau raccourci dans la carte plein écran/minimap. Affichage et son restent à vérifier en jeu.

Correctif du 9 octobre : touche T raccordée au changement réseau de l'hôte, recherche des variantes de capacités corrigée, fin de la boucle de récupération sans issue. Les invocations restent à vérifier sur les deux PC. Un arrêt protégé ne permet pas de poursuivre le combat. Pour le ZIP marqué UMM, utiliser Unity Mod Manager uniquement ; ne pas utiliser les scripts d'activation ci-dessous.

Version expérimentale : compilée, **pas validée dans le jeu**. Ce n'est pas une version finale. Réservée à vos deux PC, ne pas publier. La reconstruction automatique est maintenant raccordée : transfert, chargement, restauration des données natives, vérification, Commit puis reprise. Son comportement réel reste à vérifier. Elle refuse une capture avec projectile ou action atomique encore active ; dans ce cas, ou en cas d'échec, un arrêt protégé demande de quitter puis recharger une copie sûre. Le repli automatique jouable n'est pas garanti.

Le renvoi des actions après réparation concerne seulement leurs animations : aucun ordre mécanique n'est rejoué. Une animation peut redémarrer au lieu de reprendre exactement à son ancienne image. Le temps d'incantation transmis est réduit du temps déjà écoulé.

## Installation sur les deux PC

### Reconnexion sans quitter côté hôte

Avec ce paquet sur les deux PC dès le début de la session, la cliente peut rejoindre à nouveau l'IP de l'hôte resté en partie. Le chemin ajouté met l'hôte en pause, capture l'état actuel en mémoire, le transmet avec contrôle des blocs, charge uniquement la cliente puis compare l'état et les références natives avant reprise. Il ne relance pas la sauvegarde de l'hôte. Les anciens personnages de la cliente sont réattribués grâce à un ticket de reconnexion enregistré localement ; si ce ticket est absent, l'hôte doit réattribuer les personnages dans le menu multijoueur. Le ticket n'est pas une migration d'hôte.

Premier essai conseillé hors combat dans une zone ordinaire, hors dialogue/cinématique. La carte globale, les combats de croisade et les combats tour par tour sont refusés par ce nouveau chemin ; le RTwP exige un point sans action atomique ni projectile et passe ensuite par la reconstruction native existante. Une erreur laisse une pause protégée et des logs : ne pas présenter cette limitation comme une récupération réussie. Le comportement réel de reconnexion, chargement, restitution et reprise reste **non vérifié en jeu**.

À l'exécution seulement, la cliente conserve des checkpoints uniques et un ticket dans `RTwP-Rejoin`, sous son dossier de données du jeu, hors des sauvegardes originales. Aucun checkpoint n'a été créé ni chargé pendant le développement. Ne partagez pas les fichiers `.token` ; le collecteur de journaux ne les inclut pas. Les checkpoints de reconnexion ne sont pas des sauvegardes de campagne validées.

Les réponses de dialogue nécessitent l'accord des deux joueurs tant qu'ils sont connectés : clic gauche pour voter, nouveau clic sur une autre réponse pour changer son vote. Les pastilles colorées indiquent les votes. Si la cliente se déconnecte réellement, son vote est retiré et l'hôte peut cliquer sur une réponse pour la valider seul ; aucune réponse n'est choisie automatiquement au départ d'un joueur. Un joueur encore connecté mais en chargement reste attendu. À son retour, le joueur doit de nouveau être synchronisé et voter avant une nouvelle validation. Le simple bouton « Continuer » bénéficie de la relance technique .119 décrite en tête du document. Le clic droit ne valide aucune réponse. Pour les événements de livre demandant un personnage, l'hôte peut devoir recliquer après l'accord pour ouvrir le choix de personnage. Si l'hôte quitte, la cliente ne peut pas poursuivre seule : il n'y a pas de migration de serveur. Cette fonctionnalité n'a pas encore été essayée en jeu.

1. Décompresser le même ZIP dans un dossier local sur chaque PC. Ne pas le copier dans `Mods`.
2. Fermer le jeu. Conserver à part une copie de la sauvegarde utilisée pour l'essai. Ne pas utiliser la seule copie de votre campagne.
3. Double-cliquer `ACTIVER-ESSAI.cmd`. Le stable 0.4.16 est conservé **sans modification** dans `RTwP-RetourStable`, à côté de `Mods`. Il n'est pas supprimé.
4. Si le jeu est installé ailleurs, ouvrir PowerShell dans le dossier décompressé et exécuter :

   ```powershell
   .\Installer-essai.ps1 -Action Activer -AutoriserVersionExperimentale -GamePath 'D:\Votre dossier\Pathfinder Second Adventure'
   ```

Le script vérifie les fichiers du paquet et l'assembly du jeu avant la bascule. Il refuse le jeu ouvert, deux variantes simultanées ou l'écrasement d'un stable conservé. Il ne lance ni le jeu, ni Steam, ni une génération de voix. Il ne modifie pas les sauvegardes, le doublage ou les paramètres Unity Mod Manager.

Si une ancienne version d'essai est déjà active, commencer par `RETOUR-STABLE.cmd`, puis activer ce nouveau paquet. Ne pas superposer les dossiers. Un chemin personnalisé fourni avec `-GamePath` est mémorisé localement pour le retour et les diagnostics.

## Essai

Lancer le jeu vous-même quand la génération de voix vous le permet. Vérifier dans Unity Mod Manager que `Multiplayer RTwP (Local Experimental)` est activé ; laisser `WOTR_AIVO` inchangé.

Nicolas héberge, sa nièce rejoint par IP directe. Attribuer les personnages comme d'habitude. Dans la fenêtre multijoueur de l'hôte, cliquer sur **« Préférer le temps réel pour les prochains combats »** ; si le combat a déjà commencé, utiliser **« Passer en temps réel »**. Choisir la politique de pause voulue. Commencer par un combat ordinaire, sans monture ni invocation : déplacement, attaque simple, pause, reprise. L'hôte doit calculer les conséquences ; la cliente ne doit pas exécuter ses propres commandes mécaniques.

Ne pas commencer par toute la campagne. Les sorts complexes, entités créées et réparations complètes ne sont pas validés. Les sauvegardes sont bloquées pendant le combat RTwP et les états non vérifiés. Si une réparation reste bloquée, ne cherchez pas à forcer une sauvegarde : quitter le jeu puis revenir au stable et à la copie sûre.

## Retour immédiat

Fermer le jeu, puis double-cliquer `RETOUR-STABLE.cmd` (ou utiliser `Installer-essai.ps1 -Action RetourStable -GamePath ...`). Le stable est remis en place ; l'essai est conservé hors de `Mods`. Même si le dossier du ZIP est effacé, le stable reste dans `RTwP-RetourStable`.

## En cas de problème

Noter l'heure, le lieu, l'action, le PC concerné et le message affiché. Lancer `COLLECTER-JOURNAUX.cmd` sur les **deux** PC avant de recommencer : un ZIP local regroupe les journaux disponibles, l'identité du paquet et les versions des mods. Aucun envoi automatique, aucune copie de sauvegarde ni de voix. Les journaux peuvent contenir noms et IP : partage privé uniquement. Ne pas supprimer les journaux pour recommencer.
