# Durée de vie des animations pendant la pause

7 octobre 2026 — variante 0.4.16.35, protocole 58 et empreinte schéma 18 inchangés. Campagne de base sans DLC.

## Défaut trouvé dans le code

Le client gardait chaque handle de présentation avec un `DateTime.UtcNow` de départ. Après quinze secondes murales, `TickRealTimePresentation` libérait silencieusement le handle, même si le combat était resté en pause. Une incantation longue ou une animation ralentie pouvait aussi dépasser ce délai sans être bloquée.

La lecture seule de `Kingmaker.Controllers.TimeController` dans l'assembly 2.7.0x confirme que `GameTime` retourne le temps du joueur, et que `Tick` ne l'incrémente que dans les modes Default ou Cutscene, pas dans le mode Pause. Aucun appel à ces méthodes natives n'a été exécuté pendant cet audit.

## Correction raccordée à la présentation

- Chaque Start mémorise désormais le temps logique local du jeu, pas la date système.
- Le budget est de quinze secondes logiques pour une attaque ordinaire. Pour un sort, la durée d'incantation s'y ajoute. Une animation ralentie obtient un budget étendu ; une animation accélérée ne perd pas sa marge.
- Tant que le temps logique reste figé, les ticks ne consomment pas ce budget.
- Un handle terminé ou libéré suit toujours le nettoyage normal. End/Interrupt, changement de phase et nettoyage avant chargement natif conservent leurs chemins distincts.
- Un handle encore actif au dépassement du budget, ou après une régression de la ligne temporelle, est libéré et signalé avec unité, action et cause. Le signalement emprunte la pause protégée et le message de défaut existants, au lieu d'un abandon silencieux.
- Les durées/vitesses invalides ou impossibles à représenter sont refusées avant création d'un handle.

Ce watchdog ne prétend pas prouver la perte d'un événement End : il signale une présentation anormalement longue. Il ne remplace ni une référence d'actions actives, ni un seek de clip, ni le Commit de reconstruction. Les déplacements visuels conservent leur interpolation existante ; leur cadence n'est pas modifiée ici.

## Preuves et reproduction hors jeu

Les deux compilations passent chacune **1 229 tests** : 33 traduction, 140 réseau, 1 056 principaux. Les 22 nouveaux cas couvrent temps figé, frontière d'expiration, incantations longues, vitesse ralentie/accélérée, durée/vitesse non finie ou non représentable, régression et proximité de la limite du temps logique.

Le diagnostic Mono du paquet exact vérifie la politique pure de durée de vie : 10 000 ticks avec temps inchangé, expiration logique, régression, budget d'une incantation lente. Aucune animation native, règle, commande ou monde n'est exécuté par ce nouveau scénario. Les autres scénarios synthétiques du diagnostic passent également, ainsi que les sept contrôles du protocole. La configuration Mono autonome n'est toujours pas une preuve du chargement des dépendances dans le jeu.

- Empreinte standard : `E0CC375A7731669CE5D707CECD08F70092FB83877191CE5D81839A48C48F1F45`.
- Candidat prototype : `727F67D12143632AA84D923D4B441034DC099D53662266EF0B77E72F971E7F63`.
- SHA-256 DLL principale : `913422D1D9BB8D5C90627D1A65497E866F2FD3EB8E2C6756AD7EAD6560B00E8F`.

```powershell
& .\tools\rtwp-local.ps1 -Action Build
& .\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
& .\tools\check-rule-dispatch.ps1 -CandidateFingerprint 727F67D12143632AA84D923D4B441034DC099D53662266EF0B77E72F971E7F63
& .\tools\check-package-protocol.ps1 -CandidateFingerprint 727F67D12143632AA84D923D4B441034DC099D53662266EF0B77E72F971E7F63
```

## Limites et suite

La conservation effective d'une animation Unity pendant une longue pause reste à observer en jeu, sur autorisation. Les tests et l'audit prouvent la suppression du délai mural dans ce chemin, pas une session jouable. La reconstruction des actions et contrôleurs, la cohérence mécanique complète, le Commit, la récupération automatique, la reconnexion, le repli jouable et la couverture de campagne restent incomplets.

Aucun jeu lancé, paquet installé ou activé ; sauvegardes originales, stable 0.4.16, WOTR_AIVO et production des voix préservés. `runtimeValidated=false`, aucun `RTWP_RUNTIME_VALIDATED.json`. Avertissement AutoMapper NU1903 préexistant non résolu.
