# v0.5.1-rtwp — démarrage du combat, rechargement et synchronisation

Préversion expérimentale du 31 octobre 2026, protocole 92.

## Correctifs

- Démarrage du premier tour TB et initialisation RTwP protégés contre les sorties de combat natives prématurées. Une initialisation incomplète conserve la pause et interdit la sauvegarde.
- Initialisation d'une nouvelle rencontre lorsque le temps réel est déjà sélectionné, avec renouvellement de l'époque et synchronisation avant simulation.
- Protection des callbacks tardifs et des accès au combat après un redémarrage interrompu.
- Rechargement d'une sauvegarde ordinaire depuis un combat RTwP : abandon de l'ancien contexte avant déchargement, restauration des statistiques clientes et libération du verrou après le chargement natif et la pause de zone.
- Un seul snapshot en attente d'application par tous les clients, pour éviter l'accumulation et les réparations répétées quand la reconstruction cliente est lente. Un accusé doublon ne libère pas celui d'un autre client ; une absence de réponse déclenche toujours une vérification en pause après huit secondes.

## Téléchargements

- `wotr-multiplayer-v0.5.1-rtwp.zip` : variante Release standard (`HostPresentationPrototype=false`).
- `wotr-multiplayer-v0.5.1-rtwp-a0-prototype.zip` : variante Release A0 (`HostPresentationPrototype=true`), pour les essais du moteur de présentation RTwP.
- `SHA256SUMS.txt` : empreintes des deux archives.

Choisir une seule variante et installer exactement le même paquet chez tous les joueurs. Les deux archives ciblent `00_WOTRMultiplayerRTwP`. Un seul des slots stable et expérimental doit être actif ; conserver le slot inactif hors de `Mods`. Les bibliothèques propriétaires du jeu, configurations locales, sauvegardes et journaux privés ne sont pas inclus.

## Vérifications et portée

Les deux archives 0.5.1 sont compilées et testées en Release avec le SDK 9.0.318 et la version d'assembly 0.5.1.0 : **1 657 tests C# par variante** (1 412 principaux, 212 réseau et 33 localisation), six contrôles de protocole et contrôle des 281 clés de localisation. Les preuves de compilation proviennent des suites locales ; aucune réussite de CI GitHub n'est revendiquée.

Les correctifs ont été exercés sur **deux instances réelles du même PC**, avec profils isolés et copie de sauvegarde : démarrage TB, passage RTwP, pause/reprise, rechargement ordinaire depuis RTwP, nouvelle rencontre, déplacement client et fermeture avec checkpoint final confirmé. Après rechargement et après fin de combat, les sept membres avaient les mêmes PV, PV maximum et positions ; écart maximal de position : **0 m**.

**Ces essais en jeu portent sur le candidat 0.5.0 corrigé `3374C8680B52A65F6C49E2B027E5EFEDE205EBFB676758C8FDB3F90FA36F7CB8`. Les archives 0.5.1 sont recompilées avec la nouvelle identité de version et n'ont pas été relancées en jeu.** Le code des correctifs est identique ; les empreintes des nouveaux binaires diffèrent. Le manifeste conserve `runtimeValidated=false` et aucun marqueur de validation runtime n'est fourni.

Voir le [rapport du réessai final](correction-r1-rechargement-rtwp-2026-10-31.md), les [corrections A0](corrections-a0-v0.5.0-2026-10-31.md) et les [premiers parcours à deux instances](rapport-retest-a0-corrections-2026-10-31.md).

Cette préversion ne certifie ni une campagne complète, ni deux PC physiques, ni le doublage, les sorts complexes ou toute la matrice de reconnexion et de réparation. Essayer sur des copies de sauvegardes avec les installations et profils de test adaptés. Des exceptions natives de caméra et de météo subsistent à la fermeture des jeux ; elles sont décrites dans le rapport. L'avertissement AutoMapper NU1903 préexistant subsiste.
