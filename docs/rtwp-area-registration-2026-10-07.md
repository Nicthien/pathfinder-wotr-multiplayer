# Enregistrement transactionnel des zones — 7 octobre 2026

Variante 0.4.16.18 ; protocole 51 et empreinte schéma 16 inchangés.

## Enregistrement et retrait sans callbacks

`RealTimeAreaReplicaStore` gère explicitement la liste de scène, le proxy d'entité, le dictionnaire du service d'entités et ses caches natifs. Le service et la scène sont fournis par l'appelant : aucun singleton, monde, sauvegarde ou vue Unity n'est créé par ce code.

Les méthodes natives de scène ne sont pas utilisées : `AddEntityData` déclenche `OnAdded`, tandis que `RemoveEntityData` appelle aussi `Dispose` sur l'entité et déclenche `OnRemoved`. Le magasin ne déclenche aucun de ces chemins mécaniques.

L'enregistrement est réservé aux représentations créées par la fabrique de présentation. Il vérifie la scène attendue, les identités, les collisions de casse, la disponibilité du proxy et l'absence d'enregistrement précédent. Il réutilise un proxy non résolu existant, afin de préserver les références déjà présentes.

Une inscription non validée annule les listes, caches et liens. Un proxy créé pour cette seule inscription est retiré et invalidé ; un proxy préexistant reste vivant et non résolu. Le retrait d'une représentation est réversible jusqu'à validation. L'annulation conserve le même objet proxy et la position initiale dans la liste de scène. Le retrait validé invalide seulement le proxy, jamais l'entité par ses callbacks natifs.

## Transaction commune

La transaction existante de restauration des buffs prend aussi en charge les zones. Elle compare l'empreinte canonique et les données temporelles avant validation, puis contrôle la propriété des enregistrements et retraits.

Un remplacement de même identifiant conserve le proxy et le redirige vers la nouvelle représentation. L'annulation le redirige vers l'ancienne. Les doubles inscriptions/retraits du même identifiant dans une transaction sont refusés avant mutation pour ne pas créer une chaîne de remplacements ambiguë.

La transaction ne devient pas pour autant un rollback général du monde : les valeurs d'entités préexistantes modifiées ailleurs ne sont pas toutes réversibles. Une erreur impose toujours une réparation protégée.

## Vérifications

Dix-sept tests couvrent caches natifs préexistants, absence d'événements de scène, proxies nouveaux/préexistants, annulation et validation du retrait, ordre de scène, collisions, refus des entités ordinaires, empreintes et durées divergentes, remplacement de même identifiant, opérations dupliquées et fermeture de transaction.

Le diagnostic Mono est étendu au magasin, à la transaction et au remplacement de proxy sur des instances natives synthétiques. Il ne prétend pas tester la cohérence d'un combat : les états réseau de comparaison du diagnostic sont des fixtures ; la visibilité native des enregistrements est contrôlée séparément dans le service, la scène et les caches.

Prototype : 729 tests réussis (33 traduction, 41 réseau, 655 principaux), 233 clés françaises vérifiées. Paquet exact : `CA1B2C704D5FA3AB4CCE59CB309FA119D0ACAA92475FD1E52A5D5E3348BB1FEF`. DLL SHA-256 : `A3527D855F41BCBC1168B88E5CC2BD02E5A0CBB4341909518CFEF34394A4DFDF`.

Compilation par défaut : 729 tests réussis également ; empreinte `31C53BFFE7B54C18B3AFB2F0069E8F8906F577881A6E745C1196C783CB516753`. Avertissement AutoMapper `NU1903` préexistant non résolu.

Diagnostic Mono de ce paquet réussi : `detached-factory-and-store=True`, caches de scène/service restaurés, retrait validé, remplacement conservant le proxy. Preuves dans `local-artifacts/a0-candidates/CA1B2C704D5FA3AB4CCE59CB309FA119D0ACAA92475FD1E52A5D5E3348BB1FEF/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`.

## Limites et prochaine intégration

Cette brique n'est pas encore appelée par la réparation réseau. Le contrôle préalable continue de refuser les zones absentes. Restent les vues et FX contrôlés, la préparation de toutes les représentations, leur branchement dans le service de combat, la relecture de l'état réel avant validation et le nettoyage des vues en cas d'échec. Aucun repli mécanique jouable n'est annoncé.

Le retrait d'une zone native ordinaire reste volontairement refusé par ce magasin : ses abonnements et déclencheurs préexistants nécessitent un traitement distinct, pas une conversion silencieuse en représentation inerte. Les représentations ajoutées ici n'ont jamais été inscrites aux ordonnanceurs natifs par les événements de scène ; leur affichage et leur nettoyage doivent rester sous la responsabilité du gestionnaire de présentation.

Appels sur le thread Unity uniquement lors de la future intégration. Le verrou natif protège les dictionnaires pendant les opérations, mais ne remplace pas l'arrêt de la simulation et la barrière réseau.

Aucun jeu lancé, aucune installation, aucune sauvegarde originale ni production des voix modifiée. Aucun marqueur de validation moteur.
