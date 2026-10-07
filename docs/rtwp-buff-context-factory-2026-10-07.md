# Reconstruction des contextes de présentation — 7 octobre 2026

Cet incrément ajoute `RealTimeBuffContextFactory` à la variante 0.4.16.12 (protocole 48 inchangé). Il construit une chaîne détachée de contextes à partir des valeurs confirmées par l'hôte. Il ne constitue pas encore une réparation réseau complète.

## Comportement

- Validation de la chaîne reçue avant construction.
- Constructeurs natifs `JsonConstructorMark` pour les contextes ordinaires et d'aptitude ; aucun constructeur ordinaire recalculant les résultats.
- Résolution injectée des blueprints, unités, objets et aptitudes, avec refus d'une référence absente ou d'une identité différente.
- Restauration des références de lanceur, propriétaire, objet source et cible ; liaison des parents, puis application des tableaux et paramètres confirmés.
- Aucun enregistrement de buff, activation de composants ou accès à `Game.Instance` dans cette fabrique. Une exception laisse uniquement des objets détachés, sans mutation du monde.

## Preuves et limites

Les sept nouveaux tests couvrent une chaîne enfant/parent avec cible ponctuelle, la copie des tableaux et le refus des six catégories de référence non résolue avant conversion des paramètres. Ils ne prouvent pas la résolution des références dans une partie, ni la création effective d'un contexte d'aptitude résolu.

La compilation prototype et les 644 tests réussissent : 33 de localisation, 41 réseau, 570 principaux. Le candidat exact est `93FDA0E268CCCD09FE231B8200B6A0CF9FB28ECF99D278183263C53E4F14C523`.

La compilation Release sans prototype réussit également les 644 tests ; empreinte `82B5545F4CFBCC81D80A27BBCAB832AB548C3CCCE68EEDC2C52B85FFFBFD9BD6`. Le DLL du candidat prototype a pour SHA-256 `19B86D5C0C9EEC2663DFAD983C87AD41CD28C28A4181C7DCF76DF8A76FAC652A`.

Le diagnostic Mono de ce candidat réussit avec `installed-buff-context-factory: restored-without-world=True`. Il vérifie une reconstruction native détachée avec cible ponctuelle et paramètres simulés, sans monde de jeu. Les protections précédentes et le registre de buffs restent également vérifiés. Cela ne valide pas un combat Unity.

## Travail suivant

Brancher cette fabrique au plan de restauration des buffs manquants : résoudre toutes les dépendances, préparer les représentations, enregistrer dans une transaction, relire l'état et confirmer uniquement après vérification. Annuler les nouveaux enregistrements sur échec. Les contextes reconstruits restent des données de présentation : ils ne suffisent pas à réactiver le tour par tour mécanique.

Les zones, invocations, récupération jouable, repli mécanique et essais en jeu restent à terminer. Aucun jeu, outil de voix, installation ou activation n'a été lancé ; aucun marqueur de validation moteur n'est créé.
