# Incidents de la session multijoueur du 21 octobre 2026

Ce rapport rapproche les journaux des deux PC avec les heures des incidents
signalés. Les copies privées des journaux sont conservées hors Git sous
`local-artifacts/field-reports/2026-10-21-nicolas-niece/`.

## Constats établis

- Les deux PC utilisaient la version `0.4.16.99`, le protocole `74` et la même
  empreinte de paquet `65A8BD24C21A...`.
- La restauration d'une sauvegarde en combat a réellement abouti sur les deux
  mondes : l'hôte a libéré la barrière à 17:10:59 et la cliente à 17:11:00.
  Cette session ne justifie donc pas de contourner les protections de la
  sauvegarde ou de la restauration.
- La première bascule RTwP a échoué à 18:33:08 sur la cliente avec
  `spellbook-slot-identity`. Les deux mondes possédaient les mêmes sorts
  préparés, mais pas dans le même ordre d'indices locaux.
- Le combat RTwP de 19:40:02 a été arrêté proprement par la protection
  `extra-buff:FA_-1532947374_1`. Un effet natif équivalent existait localement
  sous un autre identifiant ; il n'était pas rapproché avant l'application du
  snapshot périodique.
- Entre 17:41:25 et 17:41:31, l'hôte a reçu les demandes d'utilisation de la
  potion de la cliente, puis les a refusées avec `Item usage has been failed`.
  La demande avait été acceptée hors combat par la cliente mais rejouée après
  l'entrée en combat de l'hôte, ce qui expliquait le retour aux anciens PV au
  checkpoint suivant.
- Pour les répliques de dialogue possédant une réponse système « Continuer »,
  l'hôte réactivait son bouton une fois les deux témoins enregistrés, sans
  transmettre cet état d'activation à la cliente.
- La montée de niveau distante était volontairement répliquée dans une fenêtre
  rendue transparente. Cela produisait exactement le fond flou sans contenu
  visible. La respécialisation signalée à 19:23 n'était pas perdue : les deux
  joueurs étaient prêts et elle s'est terminée sur les deux PC à 19:35:45.
  La fenêtre doit néanmoins rester bloquante pendant la reconstruction du
  personnage afin de ne pas exposer un groupe partiellement modifié.
- Les transitions de zone pouvaient lancer une marche/commande locale avant la
  transition de groupe autoritaire, ce qui laissait un personnage secondaire à
  une position incohérente.
- Le déplacement accéléré était explicitement forcé à `false` par les réglages
  multijoueur.

## Correctifs du candidat 0.4.16.100

- Les sorts préparés sont comparés et appliqués par identité stable
  (niveau, type, sort, métamagie et opposition), sans utiliser l'indice local
  d'affichage comme identité réseau.
- Les identifiants d'effets natifs équivalents sont rapprochés avant chaque
  snapshot RTwP. Les effets réellement manquants ou supplémentaires restent des
  erreurs bloquantes : aucune divergence n'est transformée artificiellement en
  réussite.
- Une utilisation d'objet déjà validée par le pair d'origine peut être rejouée
  par l'hôte malgré une entrée en combat intervenue pendant le transport.
- L'état autoritaire du bouton « Continuer » est transmis avec les votes de
  dialogue et appliqué sur la cliente.
- Un clic de transition effectué par n'importe quel joueur devient une demande
  unique à l'hôte. L'hôte déclenche immédiatement la transition de tout le
  groupe ; la cliente ne lance plus sa propre marche ou son propre chargement.
- La réplication distante de montée de niveau reste en lecture seule mais son
  contenu est visible au lieu d'un écran seulement flou.
- Le déplacement accéléré est activé dans les réglages imposés aux deux pairs.

Le contrat réseau est passé au protocole `75` afin d'interdire le mélange de ce
candidat avec les paquets précédents.

## Regroupement optionnel au début du combat — 0.4.16.102

- Une nouvelle option d'hôte, activée par défaut dans **Paramètres > Divers**,
  regroupe les personnages contrôlés par les joueurs qui se trouvent à plus de
  30 mètres du point d'engagement.
- L'hôte choisit comme ancre le membre du groupe contrôlé par un joueur qui est
  le plus proche d'un ennemi déjà engagé. Les personnages proches restent à
  leur place.
- Les destinations sont projetées sur le navmesh et espacées autour de l'ancre.
  Les montures sont déplacées avec leur cavalier et les personnages regroupés
  sont ajoutés au combat si nécessaire. Leur ancien ordre de déplacement est
  interrompu avant la téléportation afin qu'il ne continue pas depuis la
  nouvelle position.
- Le regroupement n'est tenté qu'une fois, avant la capture de l'état initial
  autoritaire du combat. Les coordonnées font donc partie de l'initialisation
  normale reçue par les clients ; aucun mouvement concurrent n'est simulé par
  les pairs.
- Les personnages placés dans une autre scène chargée ne sont pas déplacés.
  L'option peut être désactivée avant d'héberger la partie.

La version visible passe à `0.4.16.102`. Le contrat réseau reste au protocole
`75`, car aucun message ni champ protobuf n'a été ajouté.

## Vérifications

- Release avec `HostPresentationPrototype=true` : 1 213 tests principaux,
  161 tests réseau et 33 tests de localisation réussis ; 270/270 clés françaises
  vérifiées.
- Six contrôles du protocole du paquet réussis ; protocole lu dans la DLL : 75.
- Candidat hors jeu :
  `2C1CE81933781640F48074989F83F331A0DAB9145E328CAC0F3E6FACAC0318D4`.
- ZIP UMM contrôlé : `WOTRMultiplayerRTwP-0.4.16.100-UMM.zip`, SHA-256
  `29579C9A59A7AA4A45542ADAD327A585C6DEB7EC82F29098162F9D03E2E014B6`.
- Les deux installations isolées ont chargé la version 0.4.16.100 avec un seul
  mod actif (`SUCCESSFUL LOADED 1/1 MODS`), puis ont été fermées proprement.

Limite de preuve : le contrôle natif des fenêtres du jeu n'était pas disponible
pour cette passe. Aucun combat, dialogue, changement de zone, montée de niveau
ou chargement hôte/client n'a donc été rejoué avec ce build exact. Le candidat
n'est pas marqué comme validé en jeu, n'a pas été installé dans le jeu habituel
et ne doit pas être publié comme version vérifiée avant un nouvel essai réel sur
les deux PC.

Vérification du candidat 0.4.16.102 : 1 218 tests principaux, 161 tests réseau
et 33 tests de localisation réussis en Release avec le prototype de
présentation hôte. Les 272 clés françaises correspondent au catalogue anglais.
Les cinq nouveaux tests couvrent le choix déterministe de l'ancre et le seuil
strict de 30 mètres. Candidat hors jeu :
`97853D73C62557735DAC4C079ED00085C4BDF83D98BCA2695737337BDEAE427D`.
ZIP UMM contrôlé : `WOTRMultiplayerRTwP-0.4.16.102-UMM.zip`, SHA-256
`EFF80F1437F54BF52C48A39B4A3D3A57E40A061CB2FD1C9FB52623D9C1958DD9`.
Aucune installation ni partie réelle n'a été lancée pour cette variante ; le
regroupement doit encore être essayé sur les deux PC.
