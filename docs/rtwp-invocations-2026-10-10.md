# Invocations interrompues — 10 octobre 2026

## Preuves de la session .41

- Cliente, `Player-prev.log.txt`, 21:52:59 : reçoit `SummonNaturesAllyAasimard3`, retrouve exactement la capacité, crée la commande puis la termine en succès avant OnStart. Aucun jet d'invocation ni création des trois grenouilles. Au tour suivant, unité introuvable.
- Hôte : même sort démarré, jet de trois créatures, créations `UN_-164100303_1`, `_2`, `_3`. La transition ultérieure échoue sur ces références absentes chez la cliente.
- Cliente, `Player.log.txt`, 22:01 : le même sort fonctionne et crée trois créatures. Le premier essai suivait deux déplacements et transportait `OneAction`, le second `FiveFootStep`.
- La recherche des variantes corrigée en .42 n'explique donc pas cet incident. La fibre n'est pas incriminée par ces éléments.

## Correction .45 / protocole 65

Le moteur a deux branches pertinentes dans `UnitCommandController.TickCommandTurnBased` : veto de budget/état avant lancement, et arrêt d'approche sans mouvement. Inspection IL statique uniquement, sans exécuter le jeu. Les anciens journaux ne distinguent pas exactement laquelle a arrêté le sort.

1. `NotifyAbilityUsed` provient du callback OnStart : c'est une action déjà commencée chez son propriétaire, pas une intention. La nouvelle commande reçue est marquée par identité exacte, via une table à clés faibles.
2. Pour cette commande uniquement, avant son démarrage et en combat tour par tour ordinaire, le veto `HasCooldownForCommand(UnitCommand)` n'est pas réappliqué. Disponibilité du sort, état incapacitant, portée et consommation réelle dans OnStart sont conservés. Pas de réinitialisation globale des ressources/cooldowns, ni relance automatique du sort.
3. Position du lanceur au démarrage ajoutée au contrat, appliquée avant reproduction, avec contrôle des valeurs finies. Les intentions RTwP n'appliquent pas cette position et ne portent pas le marqueur.
4. Une liste de trajet vide ne crée plus de `ForcedPath` vide.
5. Journaux `[AbilityReplay]` : position alignée, budget confirmé autorisé, incantation démarrée, ou arrêt prématuré avec portée et cooldowns. Permet de distinguer les branches si un problème subsiste.

Le transport historique tour par tour conserve ses notifications des actions du propriétaire ; ceci ne le transforme pas en client de présentation RTwP. Pas de modification de l'autorité des intentions RTwP, de l'IA ou du doublage.

## Limites et essai manuel

Compilation Release réussie : zéro erreur, deux avertissements préexistants NU1903 pour AutoMapper. Empreinte exacte du candidat : `AC2AE2B28AC255850C1276F2667E18574EE4A51F928BC0F32B33DF9240778140`. Protocole 65, version .45, validation moteur absente dans le manifeste. Les fichiers binaires et traductions du candidat ont été contrôlés par empreinte avant empaquetage.

ZIP UMM prêt : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.45-UMM.zip`. Structure et hash de chaque fichier dans l'archive vérifiés. SHA-256 du ZIP : `F47820A0D75AA7B848A1081AF42F2FD8F6D24E8130064E5E7823BEA06D628AC9`.

Compilation Release et contrôle du ZIP ne démontrent pas la disparition du bug dans Unity. Aucun jeu ni suite de tests lancé, aucune installation et aucun changement aux sauvegardes ou au projet voix. Pas de marqueur de validation moteur.

Installer le ZIP .45 identique sur les deux PC, jeu fermé, via Unity Mod Manager. Ne pas réutiliser un combat déjà divergent : charger une copie saine antérieure à l'incident. Refaire une invocation après déplacement, vérifier les trois créatures sur les deux écrans puis le passage du tour. Essayer aussi une invocation sans déplacement. Si le blocage revient, conserver les deux journaux et l'heure.

Ce correctif préventif ne reconstruit pas rétroactivement les invocations manquantes d'une session déjà cassée. Une récupération bloquée n'est toujours pas une reprise jouable garantie.
