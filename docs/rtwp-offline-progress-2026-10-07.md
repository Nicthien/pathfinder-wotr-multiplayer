# Bilan hors jeu — 7 octobre 2026

## Statut réel

Variante locale **0.4.16.4**, protocole **42**, empreinte canonique de schéma **11**.
Prototype de présentation activable uniquement par compilation ; pas une version finale.
Campagne de base uniquement, sans DLC. Aucun lancement du jeu autorisé pendant la génération des voix.

Les travaux se font dans `D:\Codex\PathFinder\WotR-Multiplayer-RTwP`.
Le mod stable installé, les sauvegardes et le projet de doublage ne sont pas modifiés.
Le paquet expérimental précédemment rangé dans le slot désactivé reste en 0.4.16.3 ; préparer un nouveau candidat hors jeu ne l'active pas et ne remplace pas ce slot.

## Changements de cet incrément

### Ressources de capacités

- Capture des identifiants de ressources, quantités et compteurs de rétention sur l'hôte.
- Indicateur explicite distinguant une collection vide capturée d'une collection non capturée.
- Prévalidation de toutes les unités avant la mise à jour de l'état de combat.
- Application directe des quantités, sans `Add`, `Spend` ou `Restore` et leurs callbacks.
- Blocage des appels clients autonomes de dépense/restauration dans le prototype.
- Ressources intégrées à l'empreinte et au diagnostic de divergence.
- Correction du faux diagnostic dû à un descriptif absent comparé à un descriptif sans capture.

### Emplacements de sorts

- Capture de chaque grimoire, du nombre d'emplacements spontanés par niveau et de la disponibilité des emplacements préparés.
- Identité d'un emplacement préparé : niveau, index, type, sort, métamagie et opposition.
- Vérification de la structure avant application : un grimoire, niveau ou emplacement différent ne peut pas être remplacé silencieusement.
- Application directe des nombres et disponibilités ; aucune invocation de `Spend`, `Rest`, `Memorize` ou `RestoreSpontaneousSlots`.
- Garde-fous sur `Spellbook.Spend`, `SpellSlot.Spend`, `RestoreSpontaneousSlots` et le chemin `SpendInternal` lorsque `doSpend=true`.
- Les requêtes de disponibilité utilisant `SpendInternal(doSpend=false)` restent permises.
- Transport, mapping, empreinte et diagnostic étendus aux grimoires.

Cette capture ne reconstruit pas un grimoire ou un emplacement manquant. Elle ne réplique pas encore les ressources des objets, les mécanismes de conversion spéciaux ou toutes les mutations de grimoire. Elle ne démontre pas non plus le rafraîchissement de la barre d'actions dans le moteur.

### Identité et immutabilité réseau

- Les notifications de mort portent session et chargement ; ces identités sont revérifiées avant l'application différée.
- Une erreur d'un ancien chargement ne met pas en pause sa partie de remplacement.
- La déduplication des morts est réinitialisée lors d'un changement de chargement, même si une séquence est réutilisée.
- La file d'événements conserve une copie de chaque événement et retourne une copie au consommateur.
- Une séquence réutilisée avec un contenu contradictoire est rejetée dans la fenêtre de rétention, y compris après application.

## Vérifications hors moteur

Les tests vérifient notamment la prévalidation des structures, les quantités divergentes, la métamagie, les collections vides, le mapping et le transport protobuf, l'ordre canonique, les doublons et les anciens chargements.

Un essai sur objets CLR sans propriétaire ni monde de jeu vérifie l'application répétée des disponibilités de sorts sans lancement du moteur, sans reconstruction d'emplacements et sans modification du message source. Cet essai n'est pas un combat réel.

Commandes de reproduction depuis le dépôt :

```powershell
.\tools\rtwp-local.ps1 -Action Build
.\tools\rtwp-local.ps1 -Action PrepareA0Candidate -HostPresentationPrototype
```

La préparation relance compilation et tests, puis copie le candidat dans `local-artifacts\a0-candidates\<empreinte>`. Elle n'installe ni ne lance le jeu. Son manifeste conserve `runtimeValidated=false`.

Résultats observés le 7 octobre :

- Configuration précédente sans prototype : 467 tests réussis (33 traduction, 36 réseau, 398 principaux), avant les deux derniers essais sur objets CLR.
- Candidat prototype final de cet incrément : **469 tests réussis** (33 traduction, 36 réseau, 400 principaux), aucun ignoré.
- Empreinte exacte du candidat : `0A74595DB599AD99C75CFCE1BACF5E56F92E4648EC6D71DCC631564934B16B77`.
- Version du paquet : 0.4.16.4 ; protocole du manifeste : 42 ; `runtimeValidated=false`.
- Slot stable toujours actif en 0.4.16 ; ancien slot expérimental toujours désactivé en 0.4.16.3.
- Aucun processus `Wrath` observé au contrôle ; aucun lancement du jeu effectué.

Les deux configurations partagent le dossier de compilation ; seule l'empreinte du paquet préparé identifie le candidat à essayer. Une nouvelle compilation ne valide jamais un ancien paquet.

L'avertissement existant NU1903 sur AutoMapper 10.1.1 reste à traiter séparément ; aucune mise à niveau de dépendance n'a été introduite ici.

## Travaux indispensables toujours ouverts

1. **Porte A non franchie** : aucun combat à deux instances ne démontre encore l'absence complète de simulation cliente. Les hooks avant tous les callbacks et les contrôleurs/scripts restants doivent être audités puis éprouvés.
2. **Actions et résultats durables** : compléter les sorts ciblés, soins, interruptions, remplacements, impacts et le journal fiable des conséquences. Les états périodiques ne remplacent pas un journal complet d'événements durables.
3. **Présentation** : piloter projectiles et effets visuels depuis l'hôte, sans conséquence mécanique locale ; vérifier animations et interface.
4. **Reconstruction** : buffs, invocations, zones et références entre entités sans rejouer leurs effets. Les prévalidations protectrices actuelles ne constituent pas une réparation complète.
5. **Reprise jouable** : restauration atomique, annulation des créations partielles, empreinte commune, reconnexion et repli effectif au tour par tour. Une pause protégée permanente n'est pas une récupération.
6. **Campagne sans DLC** : montures, réactions, attaques d'opportunité, capacités mythiques, transformations, renforts, pièges et transitions. Préserver la croisade sur son chemin existant.
7. **Livraison** : notices françaises actualisées, paquet exact reproductible, essais sauvegarde/rechargement, compatibilité vocale vérifiée en jeu et preuves liées au paquet exact.

Le marqueur `RTWP_RUNTIME_VALIDATED.json` doit rester absent tant que les essais moteur requis ne sont pas réalisés. Aucun candidat de cet incrément ne peut être présenté comme une version habituelle validée.
