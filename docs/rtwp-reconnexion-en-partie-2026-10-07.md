# Reconnexion en cours de partie — 0.4.16.41 / protocole 63

## Raccordement implémenté

La confirmation d'une connexion alors que l'hôte est déjà en jeu déclenche désormais un chemin distinct du démarrage du lobby. Le PC hôte reste dans son monde ; aucun appel de rechargement de sa sauvegarde n'est ajouté.

1. Compatibilité exacte et nombre de joueurs vérifiés. Pause protégée et blocage des sauvegardes.
2. Capture native de l'état actuel en mémoire au point sûr, empaquetage ZIP en mémoire, état de référence et empreinte.
3. Transfert dédié par blocs de 32 Kio, fenêtre de huit blocs, empreintes, retransmissions et délais bornés. Le transfert est lié à la session, au chargement, à un checkpoint unique, au paquet et au ticket de la connexion courante ; les messages de l'ancien lobby ne terminent pas cette barrière.
4. Création d'un checkpoint unique dans le cache `RTwP-Rejoin` de la cliente, sans écrasement d'une sauvegarde originale. Chargement via le chemin natif client, avec admission de l'objet SaveInfo effectivement lu pour éviter la retransmission/reinitialisation multijoueur habituelle de ForceLoadGame. Les changements de modes de ce chargement ne sont pas diffusés à l'hôte.
5. Comparaison des états canoniques, données temporelles et topologie native réellement chargés. Nouvelle lecture de l'hôte avant validation.
6. Restitution des personnages du ticket de reconnexion, mise à jour explicite des états de synchronisation et de l'interface. Sans ticket reconnu, pas de revendication de personnage sur le seul nom du joueur : réattribution manuelle nécessaire.
7. Hors combat, Resume et accusé de réception avant levée de la pause hôte. En RTwP, réinitialisation de la représentation réseau du combat côté client, nouvelle époque et raccordement à la reconstruction/vérification/Commit existante avant reprise.

Le ticket est conservé localement par graine de session afin de permettre la restitution après redémarrage de la cliente. Ce n'est ni une migration d'hôte, ni une preuve de sécurité Internet. Les tickets ne sont pas inclus dans la collecte de journaux. Les checkpoints ne sont jamais promus en sauvegardes de campagne valides.

## Limites explicites

- Carte globale, croisade, combat tour par tour, dialogues et cinématiques ne sont pas pris en charge par cette capture à chaud. Revenir à une zone ordinaire sûre avant l'essai.
- Le RTwP refuse une capture pendant une action atomique ou un projectile. La réparation native peut encore échouer ; une pause permanente n'est pas une récupération réussie.
- Le chargement initial utilise le chargeur normal et est suivi de comparaisons. L'absence absolue de callbacks mécaniques autonomes pendant ce chargement n'est pas démontrée. Les contrôles et la reconstruction RTwP ne constituent pas une preuve moteur.
- La reprise exacte de toutes les animations, tous les caches du moteur et toutes les entités de campagne n'est pas démontrée.
- Le ticket peut ne pas être lisible ou persisté ; dans ce cas, les personnages ne sont pas automatiquement restitués après redémarrage.
- Si la capture échoue avant la création du manifeste, la cause est indiquée côté hôte et dans son journal ; la cliente peut rester au lobby.
- Un échec protège la partie par pause et blocage de sauvegarde, sans garantir une reprise jouable. Aucun rechargement silencieux de l'hôte n'est utilisé comme substitution.

## Preuves et prochain essai

Compilation Release uniquement ; aucun lancement du jeu, aucun test unitaire ou moteur, aucune installation. Les avertissements AutoMapper et CS1998 préexistants ne sont pas corrigés ici. Les anciens tests ne valident pas ce nouveau chemin.

La compilation finale a réussi avec zéro erreur. Les JSON français/anglais et les espaces du diff ont été vérifiés. Le stable installé est toujours 0.4.16 ; aucun dossier expérimental installé et aucun marqueur de validation moteur. Empreinte du candidat : `CBD82BF1938EDB7DC2C689F0D893C10EE4602A8543133B6FF0C5EE3FE23742BC`.

Essai manuel : même paquet sur les deux PC dès le début de la session, attributions connues, quitter côté cliente seulement, conserver l'hôte en zone sûre, rejoindre la même IP, vérifier le lieu/état actuel, les personnages, la reprise et l'absence de rechargement côté hôte. Refaire avec redémarrage de la cliente, transfert interrompu et RTwP au point sûr. Archiver les deux journaux via COLLECTER-JOURNAUX.cmd avant de redémarrer le jeu.
