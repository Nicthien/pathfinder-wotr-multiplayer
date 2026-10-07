# Restauration transactionnelle des buffs de présentation — 7 octobre 2026

Variante locale **0.4.16.13**, protocole **49**, empreinte canonique schéma 14. Aucun jeu ni outil de voix lancé, aucune installation ou activation.

## Branchement au réseau

L'application d'un état RTwP prépare désormais les buffs absents côté cliente après le contrôle des unités et zones existantes, puis avant l'application des valeurs des unités. La préparation utilise la fabrique de contextes détachés et le registre natif sans composants mécaniques des incréments précédents.

Pour chaque unité, les dépendances de tous les nouveaux buffs sont résolues avant leur enregistrement : blueprints, lanceurs, propriétaires, cibles, aptitudes et objets source. Les objets sont recherchés par identifiant exact dans les inventaires des unités référencées et de l'unité affectée ; aucun objet équivalent n'est substitué et aucun inventaire n'est créé. Une référence absente refuse la restauration.

Les instances sont identifiées par leur identifiant, jamais uniquement par leur blueprint. Le contrôle préalable refuse notamment doublons, incohérences de lanceur, effets locaux supplémentaires non répliqués, contextes manquants et structures de niveaux négatifs différentes.

Les représentations créées précédemment dont l'hôte ne dispose plus sont retirées sans `Remove`, `OnRemove`, désactivation ou callback de composants. Ce retrait est lui aussi transactionnel. Les buffs mécaniques ordinaires supplémentaires restent refusés : leur retrait sûr et la restitution de leurs états dérivés sont encore à traiter.

## Vérification et annulation

Lorsqu'une transaction ajoute ou retire une représentation, le service relit l'état complet dans le même passage du thread principal. La transaction n'est confirmée que si l'empreinte canonique et les temporisations correspondent à l'état reçu.

Sur exception, empreinte différente ou durée incorrecte, les nouveaux enregistrements sont retirés, les retraits non confirmés sont annulés, puis les références des planificateurs de buffs sont rafraîchies. Les tentatives de rafraîchissement continuent si l'une échoue ; une erreur agrégée signale alors l'échec.

**Ce n'est pas une transaction de tout le monde de jeu.** Les modifications déjà apportées à des états préexistants ne sont pas toutes annulées. Une application qui échoue doit donc rester dans le chemin protégé de réparation ; elle n'autorise ni reprise supposée sûre ni sauvegarde.

## Barrière mécanique explicite

Une représentation de buff sans composants n'est pas un buff mécanique reconstruit. Tant qu'une telle représentation reste enregistrée dans les unités chargées :

- ses callbacks de mécanique, suppression, retrait de rang et suppression différée sont bloqués, même après déconnexion ;
- le planificateur de sa collection reste protégé ;
- la sauvegarde, la réactivation du tour par tour et la fermeture finale du combat sont refusées tant que la reconstruction mécanique n'est pas faite.

Cette barrière est une limitation temporaire explicite, pas un repli réussi. Un buff permanent reconstruit pour la présentation peut donc encore empêcher une sortie jouable du RTwP. Il faut remplacer cette barrière par une reconstruction mécanique fidèle avant livraison. Si toutes les représentations ont été retirées conformément à l'état de l'hôte, elles ne bloquent plus à elles seules la sortie.

## Preuves

Le prototype compile et les **661 tests** réussissent : 33 de localisation, 41 réseau, 587 principaux. Les 17 nouveaux cas couvrent conservation après vérification, empreinte/durée divergentes, échec sur une autre unité, planificateur défaillant, refus des opérations après fermeture, identité des instances, références invalides, retrait confirmé ou annulé et remplacement annulé. Les tests n'exécutent pas une partie.

La compilation Release sans prototype réussit également les 661 tests. Empreinte du build par défaut : `54F5D6E845A8279D72C3498C75B1B7BE40CBB7D423CE39763F247CB40BD080FE`. Le slot stable installé reste en 0.4.16, et la variante n'est pas activée.

Candidat exact : `05D69FD05F5DE981614E0A08B83B18F36BC39862FAF176132EB7C52AF49A5651`. SHA-256 du DLL : `D512BB0D56394A5DF6198F6250DCC6E3AC880FB56E2E78D765B8DCADB06885EA`.

Le diagnostic Mono du même candidat réussit sur des objets natifs synthétiques : cinq callbacks de représentation bloqués, annulation après empreinte différente, retrait annulé puis retrait confirmé. La reconstruction de contexte et les protections précédentes restent vérifiées. Aucun accès à un monde de jeu ni chargement de sauvegarde n'est effectué.

La résolution réelle des références, le rendu des icônes/effets, l'application d'un état réseau dans Unity et la poursuite du combat ne sont **pas prouvés** par ce diagnostic. `runtimeValidated` reste faux ; aucun marqueur moteur n'est créé.

## Suite critique

1. Reconstruire les états mécaniques et contrôleurs nécessaires au repli, notamment pour les buffs permanents et les états dérivés.
2. Répliquer zones et invocations absentes et retirer les effets mécaniques ordinaires sans callbacks concurrents.
3. Vérifier reconnexion, fermeture jouable, sauvegarde après récupération et campagne de base.
4. Après autorisation distincte, effectuer le combat à deux instances et la vérification du doublage avec le paquet exact.
