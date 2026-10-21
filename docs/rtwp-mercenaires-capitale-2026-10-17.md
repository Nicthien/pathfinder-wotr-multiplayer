# Mercenaires en capitale — .55 / protocole 70

Objectif : permettre au propriétaire du mercenaire de continuer à jouer dans la taverne lorsque le personnage principal appartient à l'autre joueur.

L'exception vise les unités reconnues par `UnitHelper.IsCustomCompanion`, en état de compagnon actif (InParty), uniquement en multijoueur et en mode capitale, hors croisade. Aucun test sur le nom ou le préfixe d'identifiant. Les unités cachées, placées par script, avec spawner ou protégées contre la réparation native sont exclues.

Les vérifications IsMainCharacter sont étendues localement dans AddCharacterToLists et IsDirectlyControllable, jamais globalement. Les gardes natives IsInGame et PreventDirectControl restent en amont ; les mercenaires morts, paniqués, détachés ou de faction non contrôlable restent non contrôlables. Les contrôles multijoueurs existants continuent de filtrer le propriétaire. IsControllableInParty est adapté au même périmètre.

FixPartyMember utilise sa branche native de remise en place pour ces mercenaires uniquement. Aucune création d'entité ou modification persistante de l'état du compagnon. Les vérifications natives de présence et position évitent un déplacement à chaque appel. L'identité d'un personnage est prioritaire sur son index lors de la récupération d'attribution. La fermeture d'une fenêtre ne sélectionne plus automatiquement le héros appartenant à l'autre joueur.

Vérifications hors jeu : compilation Release, éligibilité et exclusion, présence des points de patch dans les instructions du moteur installé, rejet d'un layout inattendu. Ces tests ne prouvent pas l'affichage ou les déplacements en jeu.

À vérifier avec le même paquet sur les deux PC : entrée et chargement dans la taverne avec mercenaire actif attribué à l'hôte, portrait et sélection, déplacement visible sur les deux écrans, inventaire puis fermeture, dialogue et cinématique, sortie de taverne, mercenaire mis en réserve. Aucun lancement, installation, changement de sauvegarde ou du projet de doublage par l'agent. Les correctifs .54 restent inclus ; aucun nouveau contrat réseau.
