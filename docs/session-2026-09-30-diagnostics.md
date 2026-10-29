# Diagnostic local de la session du 30 septembre 2026

Source : `Player.log` du PC hôte, mod stable 0.4.16. Le journal du PC client n'était pas disponible. Ce document ne prouve donc pas que les deux simulations étaient identiques.

## Autour de 18 h 45

- Entre 18 h 46 et 18 h 55, les messages d'attente d'accusé de réception au début et à la fin des tours sont suivis d'un passage à `Playing` ou `Ended`, généralement en quelques dizaines de millisecondes. Ce sont des attentes transitoires, pas un blocage démontré.
- À 18 h 46 min 14 s, une notification de mort attend la fin de l'action d'une unité. Le journal ne donne pas l'unité ni la durée effective de l'attente ; l'ancien code affichait `Timeout=null` bien qu'un délai de 20 secondes fût appliqué. La variante locale journalise maintenant le délai effectif, l'étape, le tour et l'issue de l'attente.
- Le combat s'est poursuivi après cette plage. Il manque un horaire plus précis ou le journal client pour identifier la désynchronisation ressentie.

## Autres observations pertinentes

- Plusieurs échecs `System.Net.WebException: ConnectFailure` sont enregistrés vers 17 h 05–17 h 08 pour des tâches non observées. La connexion directe à la partie a néanmoins été établie. La source réseau exacte de ces échecs n'est pas prouvée par le journal ; ils ne doivent pas être assimilés d'office à la connexion directe.
- À la connexion, le client annonçait uniquement le mod multijoueur ; l'hôte annonçait trois mods, avec un seul écart actif. `SpeechMod` était actif sur l'hôte, `WOTR_AIVO` était installé mais désactivé. La variante locale autorise uniquement ces deux identifiants audio à différer entre joueurs ; les autres mods restent comparés strictement.
- Les messages de fin de tour anciens ou attribués à un autre émetteur sont désormais rejetés, avec une trace explicite. Cette protection réduit une source possible de désynchronisation, mais ne constitue pas une réparation complète de l'état du combat.

## Validation encore nécessaire

- Reproduire le problème avec les journaux des deux PC et noter l'heure approximative, la zone et l'action en cours.
- Vérifier en jeu une session de la variante lorsque le protocole RTwP sera réellement raccordé et validé. Le mod stable installé et les sauvegardes restent inchangés.
- Activer volontairement le doublage français sur le PC hôte puis vérifier une réplique entendue dans une session de test ; le journal observé indique `WOTR_AIVO` désactivé, donc la lecture effective du doublage n'est pas encore démontrée.
