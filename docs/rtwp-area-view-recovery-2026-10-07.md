# Récupération des représentations de zones — 7 octobre 2026

Variante 0.4.16.20 ; protocole 51 et empreinte schéma 16 inchangés.

## Problèmes corrigés côté code

Le pool natif masque les entités inactives ou supprimées de l'énumération. Une représentation cliente pouvait donc sembler absente alors que son identifiant, son proxy et sa scène existaient toujours. Essayer de la recréer provoquait un conflit de registre au lieu de réparer la présence.

`RealTimeAreaDiscovery` combine les zones natives actives et les représentations possédées figurant dans la liste complète du pool. Les références sont dédupliquées explicitement par identité d'objet, pas par une éventuelle égalité native. Deux objets différents portant le même identifiant sont refusés. Les zones natives ordinaires cachées ne sont pas réactivées implicitement.

Une représentation connue mais dépourvue de vue n'est plus refusée avant la préparation d'une réparation. Le service prépare sa nouvelle vue sans remplacer les données, le proxy ou les références de l'entité.

## Préparation, liaison et nettoyage

Les nouvelles vues sont préparées inactives, sans liaison aux données ni FX démarrés. Les prefabs et contextes sont résolus avant les changements de registre. `RealTimeAreaViewBinding` lie ensuite les champs natifs bruts sans `AttachView`, setter `Data`, activation des faits ou callbacks.

La liaison précédente reste disponible jusqu'à validation. Une liaison avec une vue non possédée ou appartenant à une autre entité est refusée. Après validation de l'empreinte et des temps, la transaction conserve la nouvelle vue et nettoie les anciennes représentations. Si la vérification échoue, elle remet la liaison précédente puis détruit les nouvelles vues.

Le renderer conserve également un registre faible par entité de ses représentations préparées/vivantes. Cela permet de retirer une ancienne racine même si un composant a effacé le champ `area.View` sans nettoyer son renderer. Une zone supprimée par l'hôte peut être retirée même si sa vue a déjà disparu.

Le démarrage des FX se fait après la liaison. Une racine préparée ne peut donc pas afficher ses FX avant son enregistrement. Une racine de présentation désactivée peut être réactivée lors de l'application d'un état confirmé, sans redémarrer un deuxième effet.

## Présence et autorité

`RealTimeAreaPresence` restaure `m_IsInGame=true` et `m_Suppressed=false` uniquement pour les représentations confirmées par l'hôte. Il conserve les valeurs précédentes pour l'annulation. Les setters natifs ne sont jamais appelés, car ils invoquent des callbacks de cycle de vie.

Les setters natifs `EntityDataBase.IsInGame` et `Suppressed` sont interceptés pour les représentations marquées, y compris après déconnexion. Ils restent inchangés pour les zones ordinaires et les autres entités. L'inscription aux pools et la restauration explicite utilisent les champs bruts autorisés.

La sauvegarde reste protégée par l'inspection de la liste complète du pool : rendre une représentation invisible ne la transforme pas en entité mécanique sauvegardable.

## Preuves et limites

Tests ciblés : découverte d'une représentation cachée, absence de doublon, conflit d'identité, création/remplacement/annulation de liaison native sans Unity, refus de propriété étrangère, restauration des indicateurs et annulation commune lors d'un état réseau divergent.

Le diagnostic Mono est étendu aux véritables setters natifs interceptés, à la restauration brute de présence, à une liaison de vue synthétique annulable et à la découverte d'une représentation inactive. Aucune vue réelle ni aucun objet Unity n'est créé par le diagnostic.

Les deux compilations passent 779 tests : 33 traduction, 41 réseau et 705 principaux. Les 233 clés françaises et leurs paramètres sont vérifiés. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Compilation par défaut : empreinte `0693364BA91597AE0C0EC6EA02C858EAB8015C944EC2EA48F10DB1D4A7CF4DF9`.

Candidat prototype exact : `E7827D141C774BA14FB03E74B247429249A8FBE18A6BB4611B6BA46BF695C942`. DLL SHA-256 : `5255D7E126657F971C108DAEDB0887CDB5B60FC8347D9F794E436CB54A19D9B4`.

Diagnostic Mono de ce candidat réussi : `owned-state-setters=True`, `raw-presence=True`, `view-rebinding-without-unity=True`, `hidden-discovery=True`, ainsi que les contrôles précédents. Preuves archivées dans `local-artifacts/a0-candidates/E7827D141C774BA14FB03E74B247429249A8FBE18A6BB4611B6BA46BF695C942/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`. Aucun marqueur de validation moteur ne peut être déduit de ces essais.

Restent notamment : retrait des zones natives ordinaires et de leurs abonnements, création des invocations/renforts manquants, restauration mécanique pour le repli et la sauvegarde, audit des FX de campagne et essai moteur. Les tests de liaison ne prouvent pas le rendu, les locators, la destruction différée, l'audio ou l'exécution de la réparation complète dans Unity. Une pause protégée reste un échec de récupération jouable, pas une réussite.

Aucun jeu lancé. Aucun changement au mod stable, aux sauvegardes originales ou à la production des voix.
