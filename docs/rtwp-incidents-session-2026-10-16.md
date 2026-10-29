# Corrections de la session du 16 octobre 2026 — candidat 0.4.16.53

## Sources et portée

Analyse du `D:/ALL/Downloads/Player.log.txt` de la cliente : version 0.4.16.52, soirée du 16 octobre, arrêt vers 21 h 22. `Player-prev.log.txt` contient la session du 14 octobre en 0.4.16.51, même si le fichier a été copié le 16. Ces fichiers ne couvrent pas la reconnexion après 21 h 23 sous le mod original 0.4.31.

Travail local uniquement dans le dépôt expérimental. Aucun jeu lancé, aucun remplacement du mod installé, aucune modification des sauvegardes ou du doublage. Protocole 69 : les deux PC doivent utiliser le même paquet .53.

## Incidents et changements

| Incident | Preuve | Correction locale / limite |
| --- | --- | --- |
| T ne passe pas en temps réel, puis arrêt de synchronisation | À 20:38:45, `extra-buff:FA_-2123943058_2` lors du Commit puis de l'Abort | Le tour par tour rapprochait les buffs par modèle sans aligner leurs identifiants. La transition peut maintenant aligner un effet natif existant uniquement si la correspondance est unique, avec même modèle, lanceur, source, permanence, visibilité et rang. Aucune fusion de piles ambiguës. Les identifiants sont restaurés si l'application échoue. Le journal ne donne pas le modèle de cet effet : la disparition de cet incident précis reste à vérifier en jeu. |
| Combats bloqués en boucle | `invalid-buff-context-ability` dans la capture des buffs, y compris sur le chemin tour par tour | La capture détaillée de présentation est limitée aux snapshots RT, plus aux captures TB ordinaires. La référence de l'aptitude exécutée est conservée sans normalisation de commande. Le modèle associé et l'aptitude sont validés séparément ; leur égalité n'est pas une obligation native. Une aptitude absente reste refusée. |
| Ennemi bloque le groupe après ouverture du butin, 21:03 | Initialisation de combat confirmée par la cliente, mais aucune confirmation du premier tour ; les transferts de butin continuent | L'hôte redemande toutes les deux secondes l'initialisation manquante avec partie, chargement, combat et round. La cliente peut initialiser le tour demandé ou renvoyer sa confirmation, sans rejouer un tour actif. Aucun accusé fictif, aucun tour sauté. L'interaction précise du contrôleur de butin reste une hypothèse moteur. |
| Cliente reste en attente après chargement | Côté hôte : transfert et chargement terminés, puis Eosiris encore absente des confirmations | Confirmation de zone contextualisée et répétée jusqu'à la levée de la pause. Une confirmation arrivée avant la fin du chargement hôte peut être renvoyée. Les doublons ne recréent pas de pause, les anciennes parties/charges/zones sont refusées. Ce mécanisme est testé par mocks ; les journaux clients fournis ne couvrent pas l'incident du mod original. |
| Reconnexion pendant un état incompatible | Code expérimental refusait le combat TB, le dialogue et le butin au moment de la capture | La connexion attend désormais un état capturable, sans quitter ni recharger l'hôte. Message français d'attente sur les deux PC. Le combat TB doit se terminer, le dialogue/butin se fermer et la carte globale être quittée. La reconstruction en plein tour TB n'est pas implémentée. |
| Ancienne reprise de pause agit sur un nouveau chargement | Inspection du code : continuation différée sans identité de pause | La reprise est exécutée sur le thread du jeu et vérifie qu'il s'agit encore de la même session et de la même pause. |

Les corrections .52 du soin par potion sur autrui, de la visibilité confirmée, des doublons de monstres et de la sortie via l'interface sont conservées. Elles ne constituent pas une preuve que tous les cas d'invocation, de visibilité ou de soin sont résolus. Le menu multijoueur, les votes, les pings, la carte et le niveau par propriétaire restent présents.

## Vérification reproductible

Compilation Release avec `HostPresentationPrototype=true`, sur les assemblies locales du jeu 2.7.0x. Tests ciblés NUnit/FakeItEasy sans lancer le moteur : 69 réussis, zéro échec. Résultat final : `src/WOTRMultiplayer.UnitTests/TestResults/session-20261016-final.trx`. Les deux derniers cas vérifient que la reconnexion attend sans geler un hôte en combat TB et qu'une demande disparaît au remplacement de session.

Les tests ajoutés vérifient notamment : absence de confirmation de tour fabriquée, rejet des demandes anciennes/non hôte, rechargement entre réception et application, répétition sans redémarrage de tour, confirmation de zone trop précoce puis renvoyée, doublon après reprise, rejet des anciennes zones, identité de buff unique et refus des piles ambiguës, restauration du contexte de capture après exception.

Les méthodes natives `EntityFact.set_UniqueId` et `EntityFactsManager.FindById` ont été inspectées en lecture IL : le premier écrit le champ d'identité, le second recherche dans la liste des faits. Cette inspection ne lance pas le jeu et ne remplace pas une validation des références pendant une partie.

## Limites de livraison

Paquet : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.53-UMM.zip`.

Empreinte du candidat : `2D0BABA85861902B667027F7310B45A593B5C69889DACD57C384B8EC1EE55489`.

SHA-256 du ZIP : `E53F05525A05FADD5A2367A1251D4EA2585ADB8F552EB0D8BAB1C2B6759CB4FF`.

L'exporteur a vérifié la structure UMM et l'intégrité du contenu ; il n'a ni installé ni activé le paquet. Le mod original 0.4.31 reste le slot actif constaté sur ce PC.

Ce paquet est un candidat de correction à essayer, pas une version certifiée de campagne. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` créé. Les tests ne reproduisent pas un véritable moteur Unity, les contrôleurs de butin, toutes les animations ou les scripts de rencontre.

Un buff réellement supplémentaire ou ambigu continue de bloquer une transition plutôt que d'être supprimé arbitrairement. Un échec de reconstruction native peut encore conduire à un arrêt protégé ; le retour jouable automatique n'est pas garanti dans tous les cas. La reconnexion ne recharge pas l'hôte, mais sa reconstruction cliente et sa reprise restent à valider en jeu avec le paquet exact.

Vérifications restantes prioritaires : T dans les deux sens avec les buffs de la sauvegarde concernée ; ouverture de butin simultanée à l'entrée en combat ; fin de combat ; déconnexion/reconnexion hors combat ; invocations, nuées visibles et soin de l'ours. Les événements des essais doivent être rapprochés des journaux des deux PC. Ne pas annoncer « tous les bugs corrigés » à partir de la seule compilation ou des mocks.
