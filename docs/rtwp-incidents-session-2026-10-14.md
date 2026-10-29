# Incidents du 14 octobre — candidat 0.4.16.52, protocole 68

## Preuves et modifications

Les heures correspondent aux deux PC, dont les horloges présentent un décalage d'environ deux secondes. Sources : journal hôte local et `D:/ALL/Downloads/Player.log.txt`, `Player-prev.log.txt` de la cliente.

| Incident | Preuve | Modification |
| --- | --- | --- |
| 17 h 29, bouton temps réel | Cliente : rejet du Commit puis de l'Abort avec `extra-buff:FA_-2123943058_5`. | Avant la conversion du tour par tour, réconciliation native des buffs par le chemin existant du tour par tour, tant que ce mode moteur est actif. Ensuite vérifications strictes du checkpoint, des contextes et de son empreinte conservées. Aucun retrait de composant mécanique en le faisant passer pour une réplique visuelle. |
| 17 h 34, deuxième nuée invisible | Trois unités `UN_-1938042558_1..3` présentes des deux côtés, et jets d'entrée en discrétion côté cliente. Pas de preuve d'une unité absente. | Capture et réplication de la détection par le groupe (`IsSpottedByPlayer`) dans les états de combat. Application via `Stealth.AddSpottedBy` uniquement quand la détection est confirmée. Aucun jet de perception supplémentaire. Cause précise visuelle non prouvée ; si la nuée reste invisible, ce correctif ne suffit pas. |
| 18 h 48, potion sur l'ours | Ordre reçu, potion trouvée ; recherche infructueuse dans les conversions générales. | Reproduction de la conversion native de la barre d'action d'objet : copie de l'Ability de la potion et `PotionForOther=true`. Objet résolu par identifiant ET blueprint ; capacité résultante également vérifiée par blueprint. La vraie potion reste liée à l'action pour sa consommation. Pas de soin artificiel ni de création d'objet. |
| 19 h 02, ennemi non ciblable | Deux Dretches créés côté cliente à 19:01:07, un seul côté hôte. Exclusion de `UN_47356231_2`, toujours présent visuellement puis tentative cliente de relancer un combat avec ce seul ennemi après la fin hôte. | Après exclusion autoritaire, comparer au checkpoint hôte : retirer uniquement un ennemi non joueur, sans maître, absent de l'état confirmé et correspondant à un unique ennemi confirmé de même blueprint/scène à moins de deux mètres. Hors de ces critères, ne pas supprimer : diagnostic explicite. Vérifier session, combat et sauvegarde juste avant mutation. Ce mécanisme retire le doublon local identifié, mais ne prouve pas la suppression de toutes les causes de double création scénarisée. |
| Sortie vers 19 h 05 | Crash natif juste après réception de `NotifyOvertipInteracted` à 19:04:53. Pile : thread du consommateur réseau → `MainThreadDispatcher.Initialize` → `UnityEngine.Object.FindObjectsOfType`. | `Post` n'appelle plus Unity ni UniRx : file concurrente pure .NET. UMM dépile les actions sur le thread Unity dans `OnUpdate`, avec limite par frame et journalisation des erreurs. |

## Limites et livraison

Tous les changements restent locaux. Ne pas lancer le jeu pour la compilation. Aucun déploiement, changement du stable, du doublage ou des sauvegardes originales. Aucun marqueur de validation moteur.

Même version et protocole nécessaires sur les deux PC. Le comportement moteur des cinq scénarios reste à confirmer ; aucune annonce « tous les bugs éliminés » ne peut être fondée sur la compilation seule. Les protections et vérifications RTwP restent actives : un checkpoint non applicable n'est jamais présenté comme synchronisé.

Diagnostics ajoutés : `[MainThread]`, `[AbilityReplay] Potion for other`, `[CombatVisibility]`, `[CombatEntities]`, `[CombatMode] Reconciled native TB buffs`.

## Paquet vérifié

Compilation Release réussie : zéro erreur, avertissements existants NU1903 (AutoMapper) et CS1998. Les sources de cet incrément précèdent le DLL compilé final. Archive UMM exportée et chaque fichier contrôlé contre le manifeste du candidat. Aucun marqueur de validation moteur créé.

Empreinte : `C8501CCE051045662BDFAB10924848325EC0F457FAAE3A3FF496524BEE23E786`.

ZIP : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.52-UMM.zip`.

SHA256 ZIP : `3723750A964F1403FCCEE68547A88283197BCEAD5641AA4AB1665897FB90E6A2`.

Copies de travail des trois journaux conservées dans `local-artifacts/diagnostics/session-20261014`, hors du ZIP. Ces journaux contiennent notamment des noms et adresses réseau : ne pas publier sans nettoyage.
