# Fabrique de données de zones détachées — 7 octobre 2026

Variante 0.4.16.17 ; protocole 51 et empreinte schéma 16 inchangés.

## Comportement ajouté

`RealTimeAreaReplicaFactory.CreateDetached` crée une entité native de présentation à partir d'un blueprint, d'un contexte déjà reconstruit et des données de l'hôte. Elle emploie le constructeur JSON inerte, jamais le constructeur mécanique ordinaire.

La fabrique vérifie identité, contexte, origine, transformation, cible et références ; elle affecte les champs privés/readonly et initialise les gestionnaires vides `Parts` et `Facts`. Les données autoritaires sont appliquées par `RealTimeAreaReplication`. Aucune activation, inscription à des événements, création de vue, inscription dans une scène, recalcul, test sélectif ou déclencheur de dégâts.

Les zones attachées utilisent la position confirmée reçue, sans consulter la position ou la vue de l'unité cible. La cible et les occupants doivent déjà avoir un proxy natif : sans lui, le constructeur natif `EntityRef` accède à `TemporaryEntityService`. Ce chemin est maintenant refusé avant conversion des paramètres ou mutation. Les unités artificielles des tests doivent donc avoir un descripteur et un proxy inertes ; le descripteur est aussi nécessaire à la comparaison native `unit == null`.

La topologie des contextes est vérifiée avant le getter récursif `SourceAbility`, pour refuser un cycle sans récursion incontrôlée.

## Durée de vie et protections

Les représentations sont marquées par identité d'objet. Douze méthodes natives sont protégées pour ces seules représentations, même hors session : six chemins de simulation, les callbacks de postchargement/correction/destruction/changement d'état/déchargement, et la création automatique de vue. Le futur gestionnaire d'affichage doit donc créer et nettoyer les vues explicitement.

Les contrôles de sauvegarde, fermeture finale du combat et retour au tour par tour tiennent maintenant compte des zones de présentation présentes dans l'état du jeu. Une représentation visuelle ne compte pas comme une reconstruction mécanique valide.

## Portée des preuves

Vingt tests couvrent la création sans monde, les champs et valeurs calculées, les sources invalides, cycles de contextes, cibles ambiguës/absentes/non finies, cibles attachées sans vue, références sans proxy, protections persistantes et nécessité de reconstruction mécanique. Le diagnostic Mono est étendu à la fabrique et aux douze méthodes réellement interceptées après désactivation du rôle client.

Prototype : 712 tests réussis (33 traduction, 41 réseau, 638 principaux), 233 clés françaises vérifiées. Paquet exact : `924D25CDAC7F087FD8A8FDFD1DBA00D839EBD27397A3ADFBA7F9138D2557AC0D`. DLL SHA-256 : `2CE9C17C2BAA2D27AC13FC4BDB9D75043A5F2866298222484D2F1BBA855E6BF7`.

Compilation par défaut réussie également : 712 tests, empreinte `60D6EC6EC7FCC9453A435B7355415C7C184B5F5AACBCA4E9DCA7E9DAC9C1552F`.

Diagnostic Mono du paquet exact réussi : `detached-factory=True`, `disconnected-lifecycle-targets=12`, `raw-state-without-world=True`. Le diagnostic crée aussi une cible attachée avec proxy inerte sans consulter sa vue. Preuves sous `local-artifacts/a0-candidates/924D25CDAC7F087FD8A8FDFD1DBA00D839EBD27397A3ADFBA7F9138D2557AC0D/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`.

Les essais restent hors jeu : aucune observation des FX ou formes Unity, aucun combat, aucune sauvegarde de partie. L'avertissement AutoMapper `NU1903` préexistant reste ouvert.

Un premier test de cible synthétique sans proxy a révélé l'accès natif à `TemporaryEntityService` et a échoué avant tout lancement de jeu. La vérification explicite du proxy a été ajoutée, puis les fixtures ont été corrigées et réexécutées. Les erreurs initiales d'accès aux membres privés dans les tests ont été corrigées par réflexion sur l'assembly natif non publicisé.

## Suite obligatoire

Cette fabrique n'est pas encore branchée à la réparation réseau : les zones absentes sont toujours refusées par le contrôle préalable. Restent le rendu contrôlé des formes/FX, l'enregistrement et le retrait transactionnels, la vérification de l'état relu, l'annulation après erreur et la reconstruction mécanique avant repli. Ne pas appeler simplement `CreateEntityData` ou le constructeur natif ordinaire pour contourner ces étapes.

Le jeu, la variante installée, les sauvegardes et la production des voix n'ont pas été lancés ni modifiés. Aucun marqueur de validation moteur ne doit être créé pour cet incrément.
