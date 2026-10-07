# Résultats durables des unités — vérification hors jeu

Variante **0.4.16.8**, protocole **45**, schéma d'empreinte **13**. Travail local, campagne sans DLC. Aucun lancement du jeu, aucune installation ou activation et aucune modification du doublage ou des sauvegardes.

## Ce que cet incrément apporte

- Événement `UnitResult` dans le flux autoritaire fiable existant : dégâts absolus, caches de PV/PV temporaires/non létaux, état conscient/inconscient/mort, mort finale, appartenance au combat, compteur de groupe, ressources et disponibilités de sorts.
- Capture hôte après les modifications de dégâts, recalculs de statistiques vitales, changement de vie et dépenses/restaurations de ressources ou de sorts. Les captures identiques d'une même rencontre sont supprimées ; une nouvelle rencontre ne réutilise pas cette déduplication.
- Prévalidation des valeurs et de la structure des ressources/sorts avant application. Les unités ou structures manquantes restent des erreurs explicites, pas des créations improvisées.
- Application cliente par champs et caches confirmés, sans recalcul de vie/statistiques, `GameHelper.KillUnit`, `Spend`, `Restore`, callbacks de mort, XP, butin ou quête. Les indicateurs visuels de mort/inconscience sont transmis à l'animation ; leur rendu réel reste non vérifié.
- Protection des setters de dégâts/statistiques vitales, du recalcul de vie et de `UnitLifeController`. Une écriture autonome cliente inattendue produit un diagnostic et une barrière protégée ; le tick de vie client normalement attendu est simplement ignoré.
- L'ancienne notification `NotifyRealTimeUnitKilled` n'est plus émise ni consommée par le prototype de présentation. Le service refuse également son ancien chemin de mise à mort. Le chemin de compilation sans prototype conserve ce comportement historique.
- Conservation des participants d'une rencontre après mort, inconscience ou retrait du combat. Sans cela, un état de référence pouvait annoncer une séquence comprenant une mort tout en omettant l'unité morte : la cliente aurait ignoré l'événement sans recevoir son résultat. Le registre est lié à l'objet rencontre, pas à l'époque de réparation ; les clés faibles empêchent sa conservation globale entre rencontres. Les collisions d'identifiants et dépassements de capacité sont refusés, jamais résolus en oubliant des morts.
- Empreinte et diagnostic incluent les états vitaux et l'appartenance au combat. Les checkpoints RTwP les capturent et les appliquent par le même chemin direct.

## Vérifications reproductibles

Depuis la racine du dépôt, uniquement en Release :

```powershell
& .\tools\rtwp-local.ps1 -Action Build -HostPresentationPrototype
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint <empreinte-du-candidat>
```

Les tests couvrent les champs réels du jeu avec des objets non initialisés et sans monde de jeu, les résultats absolus réordonnés/dupliqués, la reprise après séquence incluse dans un checkpoint, les enveloppes ambiguës, les structures absentes, la conservation des participants et le rejet des anciennes sessions/sauvegardes/époques. Les essais avec deux acteurs réseau utilisent des services moteur simulés : ils ne prouvent pas un combat dans Unity.

Le diagnostic Mono isolé installe la classe Harmony de production sur les **six cibles** des protections vitales. Il appelle réellement les setters de dégâts et de statistique : les deux écritures clientes sont bloquées, l'application directe d'une mort confirmée ne produit aucun callback de statistique et le setter hôte fonctionne encore. Il revérifie aussi le dispatch des règles du palier précédent. Le programme ne lance ni `Wrath.exe`, ni UnityPlayer, ni un monde de jeu et ne consulte pas `Game.Instance`.

`tools/inspect-game-method.ps1` permet de lire les corps IL nécessaires dans l'assembly installé, sans l'exécuter. Cette inspection a notamment montré les callbacks de `ModifiableValue.UpdateValue` et de `UnitLifeController.SetLifeState`, qui ne doivent pas être rejoués sur la cliente.

## Limites et preuve suivante

Cet incrément n'est **pas une validation de combat** et ne suffit pas à livrer une version habituelle.

- `CauseActionId` existe dans le contrat, mais la capture automatique ne corrèle pas encore chaque conséquence à son action d'origine. Le flux est ordonné et dédupliqué, ce qui n'est pas équivalent à une corrélation métier complète.
- Les écritures directes ne passant pas par les cibles instrumentées restent à auditer. Les notifications nécessaires à l'interface et aux voix doivent être distinguées des callbacks mécaniques et vérifiées dans le moteur.
- La fin du dernier ennemi et la fermeture de la rencontre nécessitent une barrière finale assurant que le dernier résultat a été intégré avant de libérer le combat et les sauvegardes. `MultiplayerActorBase.CombatEnded()` remet actuellement `Game.Combat` à `null` immédiatement ; la capture après changement de vie peut donc arriver trop tard si cette fermeture s'est produite dans le callback de mort. Ce chemin reste à corriger : conserver la rencontre et son dernier état jusqu'à confirmation cliente, puis terminer la fermeture. Le registre de participants ne suffit pas à lui seul.
- Les buffs/zones/invocations, leur création et leur reconstruction sans effets doublés, le rétablissement complet des contrôleurs avant repli, les montures et les mécanismes de campagne restent incomplets.
- Un participant détruit, remplacé ou changé de scène ne peut pas être traité comme un simple retrait du combat : le protocole de cycle de vie des entités doit prendre le relais. Le registre ne prétend pas résoudre cette reconstruction.
- La séparation complète simulation/présentation doit toujours être prouvée à deux véritables instances. Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` ne doit être créé avec ces seules preuves hors jeu.

## Résultats du palier

- **568 tests réussis**, zéro échec et zéro ignoré, avec et sans prototype : 33 traduction, 39 réseau et 496 principaux. Vérification de 232/232 clés françaises et de leurs paramètres.
- Candidat local : `local-artifacts/a0-candidates/2AA4A59C91D05D097F8A258F75B86D8A5487D6DF15848713EA6239CB081F583A`.
- SHA-256 du DLL principal : `EB8BD2585EC3F78EB17BA438EC07458B81F95A80E24B4E30DF5E5A0FF5D41E12`.
- Diagnostic Mono du **paquet exact** terminé avec code 0 ; preuves archivées dans son dossier `offline-rule-dispatch`. Six cibles vitales patchées, écritures clientes bloquées, état de mort confirmé appliqué, zéro callback de statistique et setter hôte préservé.
- `runtimeValidated=false`, aucun marqueur de validation moteur. Le stable 0.4.16 reste actif ; le slot expérimental antérieur 0.4.16.3 reste désactivé. Aucun jeu lancé.
- L'avertissement préexistant NU1903 concernant AutoMapper 10.1.1 reste présent ; aucune mise à jour de dépendance n'a été mêlée à cet incrément.
