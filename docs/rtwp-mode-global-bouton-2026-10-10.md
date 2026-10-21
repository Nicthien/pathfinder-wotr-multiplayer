# Mode global et bouton natif — 0.4.16.50

## Demande retenue

Aligner le choix du mode sur le solo : T modifie le mode global, conservé au prochain combat. Cette demande remplace la préférence séparée du mode temporaire proposée précédemment. L'hôte reste seul décideur.

## Modifications

- Une transition en combat conserve le nouveau choix seulement après son Commit réussi ; un Abort ne conserve pas un choix non appliqué.
- Hors combat, le choix applique aussi `EnableTurnBasedMode` dans le moteur, sur l'hôte et la cliente. Auparavant, seul le champ de préférence du mod changeait.
- Le bouton natif est rafraîchi via `IngameMenuVM.InitTurnBasedModeValues` après confirmation du réglage moteur. Les états obligatoires/verrouillés du jeu restent respectés. La vue est suivie par référence faible et libérée à sa fermeture.
- La notification de préférence porte désormais session et sauvegarde ; l'application asynchrone vérifie aussi séquence, phase et absence de combat. Les demandes anciennes ne modifient pas une nouvelle partie.
- Une pause manuelle ne bloque plus une demande de changement en combat. Les pauses techniques, reconnexions et transitions restent protégées.
- Les contrôles redondants préférence/mode dans le menu de combat sont supprimés. En multijoueur, ni T ni le bouton ne basculent la cliente indépendamment.

## Vérification et limites

Compilation Release A0 réussie avec le prototype de présentation activé à la compilation. Avertissements NuGet existants concernant AutoMapper 10.1.1 ; dépendance non modifiée dans cet incrément. Protocole 67 : même paquet nécessaire sur les deux PC.

Aucun jeu lancé, aucune suite de tests exécutée, aucune installation ou activation réalisée. Stable, voix et sauvegardes originales non modifiés. Le rafraîchissement visuel, les bascules répétées et la persistance réelle doivent être confirmés en jeu. À deux, une transition peut attendre un point sûr et les confirmations réseau ; cet incrément ne supprime pas cette protection et ne certifie pas le combat RTwP complet.

Empreinte du candidat : `D3A8AB2FB6D3BEABF186C185A2BBD88B7A6C1FA7D2A8958CB50E20A44D51DBF1`.

ZIP UMM exporté et structure vérifiée : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.50-UMM.zip`. Empreinte du candidat inchangée à l'export ; SHA256 ZIP : `0B0D51A072DA4C7ABF71B153E6BE4C3976E4362C97AE64B621F0EDF9112FBED1`. Installer le même ZIP sur les deux PC, jeu fermé, sans activer simultanément le stable et la variante.
