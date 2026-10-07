# Buffs : séparation des valeurs et des callbacks — 7 octobre 2026

Variante 0.4.16.10, protocole 47. Travail hors jeu ; aucune installation ni activation.

## Constat dans l'assembly installée

`Buff.SetRank` désactive puis réactive le fait actif. `BuffCollection.AddBuff` passe par `RuleCanApplyBuff`, le contexte mécanique, les règles d'empilement et les événements de faits. `OnFactWillDetach` appelle `Buff.OnRemove`, qui peut supprimer des modificateurs, d'autres faits et appeler des composants. Ces opérations ne sont pas des écritures de présentation.

L'inspection IL de l'assembly installée confirme en revanche que `set_Rank`, `set_EndTime`, `set_NextTickTime` et `set_NextResourceSpendingTime` ne font que des écritures de champs (avec construction d'un `TimeSpan?` pour les deux échéances optionnelles). Aucun appel d'activation n'y figure.

L'ancien chemin de réparation utilisait `SetRank` et pouvait confondre deux instances du même blueprint. Il ne convient donc pas à une cliente qui ne doit pas rejouer les règles.

## Modification implémentée

Le prototype applique les valeurs des buffs déjà présents par un chemin explicite `UpdateUnitBuffsPresentation`. Le chemin tour par tour ordinaire garde `UpdateUnitBuffs` inchangé.

- Toutes les unités sont contrôlées avant l'application du checkpoint : identifiants uniques, blueprint et casters concordants, absence d'instance manquante ou supplémentaire, valeurs valides.
- L'identifiant du buff fait autorité ; le même blueprint ne remplace pas une autre instance. Deux buffs empilés restent distincts, même si leur ordre d'énumération change.
- Rang, expiration, prochain tick et prochaine dépense sont écrits par leurs champs/setters simples, sans `SetRank`, `Activate`, `Deactivate`, création ou retrait de fait.
- Les échéances relatives négatives sont acceptées : un événement peut être capturé en retard sur l'hôte avant son traitement. Elles ne doivent pas être confondues avec une durée restante invalide.
- La structure des niveaux négatifs doit correspondre avant modification. Les corrections de temporisation n'enlèvent pas de `UnitPart` et n'utilisent pas `SetDuration` dans ce chemin.

Le recalcul du prochain événement de la collection demeure une recherche dans ses faits, pas leur exécution. La protection du contrôleur client existant ne couvre pas les unités qui viennent de sortir du combat. Trois protections supplémentaires interceptent donc directement `BuffCollection.Tick`, `Postpone` et `UpdateOnDeadOwner` pendant le combat RTwP client, y compris si l'unité n'est plus marquée en combat. L'hôte et le tour par tour ne sont pas filtrés par ces préfixes.

## Ce que cet incrément ne termine pas

Ce chemin ne crée pas encore un buff absent et ne retire pas encore un buff supplémentaire. Il refuse ces écarts avant de modifier les unités au lieu de réutiliser le chemin mécanique historique. La création autoritaire doit transmettre un contexte suffisant, enregistrer une représentation sans effets mécaniques, fournir les valeurs dérivées nécessaires à l'interface, puis permettre restauration et rollback vérifiés. Le retour au tour par tour devra restaurer les composants mécaniques cohérents, pas simplement réactiver une collection visuelle.

Les zones, invocations, références croisées, mutations de contexte/visibilité et le cycle complet de reconstruction restent à couvrir. Les tests d'objets synthétiques ne démontrent pas l'affichage des icônes, particules ou statistiques dans le moteur.

## Vérification

Les tests couvrent les empilements, identifiants insensibles à la casse, absence, surplus, doublons, blueprint/casters divergents, rang/durée invalides, échéance dépassée, permanence et structures des niveaux négatifs. L'application native est testée sur un `Buff` synthétique sans monde, propriétaire ni blueprint ; un appel mécanique à `SetRank` ne pourrait pas fonctionner sur cet objet.

Le diagnostic Mono contrôle également cette écriture sur le paquet exact, sans `Game.Instance` ni unité réelle. Les empreintes et résultats du candidat sont conservés sous `local-artifacts/a0-candidates`. Aucun marqueur de validation moteur n'est produit.

Paquet prototype préparé et vérifié : `045F37AEF128947E96C2CDD64F44605CE470BEE82E0955A55DFE431DF4F42921`. SHA-256 du mod : `DA847901A9C0F0804CD0F2F61B51BF84223487D0CD4F9F3FC3D847EF2C109473`. Compilation Release réussie, 612 tests réussis (33 de localisation, 40 réseau, 539 principaux).

La compilation Release sans prototype réussit également les 612 tests. Dans cette configuration les protections nouvelles ne sont pas installées et le chemin de mise à jour historique est conservé ; cela ne remplace pas une vérification en jeu du tour par tour.

Le diagnostic Mono a effectivement installé les trois préfixes de collection puis appelé les méthodes natives sur une collection synthétique sans propriétaire : `guarded-targets=3; no-world-access=True`. L'écriture de rang et d'échéances a également réussi : `applied-without-world-owner-blueprint=True`. Les vérifications antérieures des règles et des états vitaux restent réussies. Ces résultats ne valident pas encore les particules, l'interface ou le cycle de reconstruction d'un buff absent.
