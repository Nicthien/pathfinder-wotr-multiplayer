# Montée de niveau : écran réservé au propriétaire

Version 0.4.16.46, protocole 65 inchangé. Modification locale uniquement ; stable, voix et sauvegardes non modifiés.

## Fonctionnement ajouté

- Le propriétaire actuellement attribué au personnage voit la fenêtre. Un familier utilise l'attribution de son maître. Hors du groupe, l'historique d'attribution reste disponible. Un propriétaire absent du lobby laisse l'accès à l'hôte.
- Le contrôle d'affichage est indépendant des accusés de réception des pages : attendre l'autre PC ne doit pas masquer la fenêtre du propriétaire.
- Une demande manuelle d'ouverture par un joueur non propriétaire est refusée ; l'hôte vérifie également les demandes reçues de la cliente.
- Les choix, changements de page, validations et annulations restent synchronisés. L'autre PC garde son contrôleur et ses vues liés en arrière-plan, invisibles et non cliquables. Ses callbacks de confirmation des pages restent exécutés.
- L'ouverture et la fermeture de la fenêtre cachée n'exécutent pas le fondu ni le son de fenêtre. Les données sont toujours appliquées via le service de montée de niveau existant.
- Les réglages de visibilité précédents sont restaurés à la fermeture ou avant l'affichage d'une future montée de niveau locale. Si la propriété revient à l'hôte pendant la fenêtre, l'animation d'ouverture normale est relancée.
- Logs `[LevelingOwnerUI]` pour demandes refusées, réplication cachée et restauration de visibilité.

## Pourquoi garder des vues en arrière-plan

Le service existant sélectionne les classes, talents et sorts à travers `SelectedDetailView` et les modèles de pages. Le protocole attend des confirmations envoyées par les callbacks des pages. Supprimer OpenUI ou désactiver l'objet pendant cette opération casserait ce mécanisme. La correction masque donc uniquement le rendu et les clics : groupe de transparence racine, groupes descendants et canvases propres à cette fenêtre. Elle n'appelle pas Close/Complete pour masquer et ne supprime aucun message réseau.

La nouvelle visibilité utilise le propriétaire courant avant l'historique, et non la disponibilité des pages ou le joueur qui a cliqué en premier. Création de mercenaire et personnages encore sans attribution conservent le décideur hôte de l'ancien chemin.

## Validation et limites

Compilation Release réussie : zéro erreur, deux avertissements préexistants NU1903 AutoMapper. Aucun jeu, suite de tests ou installation lancé. Pas de preuve moteur ajoutée.

Empreinte du candidat .46 : `2E8B8367494C934EC684F6C6491FEB881EC5A2B7E865ACB4AF9279A6FAB6976F`. Le manifeste conserve `runtimeValidated: false` ; les contrôles de parité des empreintes du mod et de l'outil ont réussi.

ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.46-UMM.zip`. Structure et empreinte de chaque entrée vérifiées. SHA-256 : `04344D808B166EA2944317CDA4CA6F1C8203B561AC0975B2E28F29B4941B5FE9`.

La pause et le mode de jeu de la montée de niveau restent ceux du mod/jeu existants. L'autre joueur n'obtient pas pour autant une liberté de combat ou de déplacement pendant la progression. Le mod conserve une seule montée de niveau réseau à la fois. Les flux de création/reconversion utilisent encore leur contrôleur existant.

Essai manuel sur une sauvegarde saine avec le même paquet sur les deux PC :

1. Monter un personnage de l'hôte : fenêtre chez Nicolas seulement ; validation et niveau identiques sur les deux PC.
2. Monter un personnage de la cliente : fenêtre chez elle seulement ; compétences, talent et sort choisi présents chez l'hôte après validation.
3. Annuler puis ouvrir un personnage de l'autre propriétaire : vérifier que son écran normal n'est pas resté transparent et que l'annulation ne consomme rien.
4. Monter un familier et un niveau mythique : même règle de propriétaire, choix synchronisés.
5. Vérifier le refus d'ouverture d'un personnage de l'autre joueur. Après déconnexion de la cliente, vérifier que l'hôte peut ouvrir sa progression. La reprise d'une fenêtre déjà ouverte après déconnexion reste à confirmer en jeu.

Un changement visuel compilé ne prouve pas encore ces comportements dans Unity. Les contrôles d'autorisation et le masquage sont implémentés, mais le rendu exact et les états de progression doivent être confirmés durant votre essai.
