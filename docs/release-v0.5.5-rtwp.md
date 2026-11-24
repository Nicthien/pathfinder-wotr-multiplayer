# v0.5.5-rtwp — corrections de la session du 18 novembre

Correctif du 24 novembre 2026, protocole 95.

## Corrections

- Les dialogues transmettent maintenant l'identifiant exact de la réplique autoritaire. Si un client affiche une autre réplique, il se recale sur celle de l'hôte et confirme cette réplique au lieu de bloquer toute la partie.
- La fermeture de la fenêtre multijoueur pendant un changement de zone tolère désormais une vue Unity déjà détruite.
- Une sélection d'archétype vide reçue avant la sélection de classe est traitée comme un état valide et n'interrompt plus la synchronisation de montée de niveau.

Le protocole passe à 95. Tous les joueurs doivent installer exactement le même paquet.

## Téléchargement

Télécharger uniquement **`wotr-multiplayer-v0.5.5-rtwp.zip`**. C'est la compilation Release standard, sans le prototype ni la passerelle de laboratoire.

Installer cette même archive sur les deux PC, jeu fermé. Un seul des mods stable et RTwP doit être actif dans `Mods`.

## Vérifications

- 33 tests de localisation, 217 tests réseau et 1 440 tests de logique réussis, soit 1 690 tests sans échec.
- Six contrôles de protocole réussis sur la DLL Release standard.
- Deux instances isolées ont chargé le candidat laboratoire 0.5.5 au protocole 95 avec la même empreinte, rejoint la même partie et convergé sur la zone, les personnages, les points de vie et les positions.
- Un chargement synchronisé du camp de Seelah a convergé sans l'exception `HostMenuItemController.get_Title` observée dans les logs de la session.
- Le dialogue exact `KnightCamp_SeelahQ1_dialog` a été lancé en 0.5.5. Les deux instances ont affiché `Cue_0003`, le client a confirmé la réplique et l'hôte a immédiatement constaté que tous les joueurs l'avaient vue.

La correction de reprise autoritaire d'une réplique volontairement divergente est couverte par un test de régression, mais cette divergence artificielle n'a pas été injectée dans le jeu. La montée de niveau complète n'a pas été rejouée manuellement dans l'interface. L'archive joueur 0.5.5 standard n'a pas été installée dans l'installation habituelle ni chargée en jeu ; la validation à deux instances a utilisé la compilation laboratoire 0.5.5, qui ajoute uniquement la passerelle de test au même code du mod.
