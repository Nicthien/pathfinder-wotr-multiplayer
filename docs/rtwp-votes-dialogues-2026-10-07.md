# Votes unanimes dans les dialogues — 0.4.16.39 / protocole 61

## Fonctionnement ajouté

- Clic gauche sur une réponse : vote du joueur, sans exécution cliente.
- Chaque joueur peut changer de réponse tant que l'accord n'est pas acquis.
- L'hôte ne valide qu'avec exactement deux participants, connectés et synchronisés, ayant vu la réplique et voté pour la même réponse.
- La validation et les jets de dialogue restent sur le chemin hôte existant ; les votes ne rejouent pas les conséquences.
- Les pastilles de proposition existantes affichent les votes reçus. Les notifications françaises expliquent l'attente.
- Chaque tour de vote reçoit un identifiant unique ; un vote ancien, étranger au dialogue ou émis par un non-participant est refusé et journalisé. La réponse validée transporte l'identifiant du tour qui l'a décidée, même si le moteur affiche déjà la réplique suivante côté hôte.
- Le simple bouton Continuer, sans choix de réponse, conserve son fonctionnement antérieur.
- Les événements de livre avec choix de personnage conservent ce choix sur l'hôte. Si la cliente complète l'accord en dernier, l'hôte est invité à recliquer pour ouvrir le sélecteur. Le passage dans ce sélecteur n'est pas encore l'exécution de la réponse.
- Le clic droit ne valide pas de vote ni de réponse.

## Déconnexion et limites

Une absence ou une déconnexion ne vaut jamais accord. Les participants sont conservés pour la réplique courante : pas de passage automatique au vote solo. Un retour avec une autre identité peut nécessiter de rouvrir le dialogue ou de recharger la copie d'essai. Ce lot ne remplace pas tout le protocole des dialogues et ne certifie pas les dialogues scriptés, les pages de livre ni leurs transitions dans le moteur réel.

## Vérification et livraison

Compilation Release du prototype uniquement, sans lancement du jeu ni suite de tests. Le stable, les sauvegardes et WOTR_AIVO ne sont ni installés ni modifiés par ce chantier. Aucune validation de comportement en jeu n'est revendiquée. Le même paquet 0.4.16.39 est nécessaire sur les deux PC ; les anciens ZIP sont conservés.

Empreinte du candidat compilé livré : `1F57101F2DC781284BA97ABB4D6F9EE5033BC4D4E21886A59AFF116589E8B92D`. Les avertissements préexistants concernant AutoMapper restent non corrigés dans ce lot. Les exports intermédiaires de relecture ne sont pas le paquet à installer.

À vérifier lors de votre essai : réponses différentes sans progression, changement de vote puis accord dans les deux ordres (hôte en premier ou en dernier), bouton Continuer, réponse de sortie, dialogue à jet, page de livre avec personnage, déconnexion pendant l'attente. Utiliser COLLECTER-JOURNAUX.cmd sur les deux PC en cas de problème. Les journaux comportent les tours, votes reçus, refus et validations de l'hôte.
