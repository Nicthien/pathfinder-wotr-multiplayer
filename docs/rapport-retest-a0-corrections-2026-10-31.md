# Réessai moteur des corrections A0 — 31 octobre 2026

## Verdict

Les corrections améliorent effectivement le scénario local à deux instances : les tours TB démarrent, les commandes circulent dans les deux sens, la transition TB → RTwP fonctionne, et une rencontre démarrée directement en RTwP termine sa fermeture autoritaire après reconstruction native de la cliente.

**Le paquet reste expérimental : le chargement ordinaire d'une sauvegarde depuis un combat RTwP actif provoque une défaite cliente avec des PV maximum nuls.** Ce défaut empêche de déclarer la version utilisable en campagne.

Le paquet essayé est exactement celui des [corrections et tests Release](corrections-a0-v0.5.0-2026-10-31.md), distinct de l'archive publiée du [premier rapport A0](rapport-test-a0-v0.5.0-2026-10-31.md). Aucun correctif de code supplémentaire n'a été introduit pendant ces essais.

## Cadre et identité du paquet

| Élément | Valeur vérifiée |
|---|---|
| Version / protocole | `0.5.0` / `92`, prototype de présentation activé |
| Empreinte du paquet testé | `41B77DAB21B01B6BE5A5F426412C6A52280B345DCE9D05329F9262718E160A43` |
| DLL principale SHA-256 | `84497885BC1044118BCCD185375D616EAA20D67BB17F48D0A45FB07F28961792` |
| Jeu | WotR `2.7.0x` |
| Assembly du jeu SHA-256 | `2CB7160B7154D4FFACC77B9C51B1EB26199E1294300F04FDFC073367B2EF8953` |
| Chargement | Un seul mod actif, 941 méthodes Harmony patchées sur chaque processus |
| Instances | Deux processus réels sur **une même machine**, pas deux PC physiques |
| Premier parcours | Hôte PID `11228`, cliente PID `49664` |
| Parcours direct RTwP final | Hôte PID `17660`, cliente PID `25120` |
| Profils | `local-artifacts/runtime-profiles/host` et `client`, marqués et isolés |
| Sauvegarde | Copie `Manual_119_Rencontre_de_rue__13_Rova__IX__4715__00_36_19.zks` |
| Zone / cible | `RE_KenabresStreets02` / `Baphomet Cultist`, `UN_-555712917_1` |
| Personnage client | Daeran, `CBU_-203136706_1` |
| Transport | Connexion directe locale `127.0.0.1:1024` |
| Audio | Désactivé pour ces instances automatisées ; doublage non testé |

La connexion a vérifié l'égalité des empreintes du code installé, du protocole et du contenu déclaré. Les instances partagent l'installation du jeu, avec sauvegardes, cache et journaux propres à leurs profils. Les scripts de préparation/staging A0 ont été utilisés, avec l'autorisation explicite d'essai non validé ; aucun verrou de validation n'a été contourné.

La passerelle de test appelle les workflows du mod et du moteur. Le déclenchement de rencontre ajoute l'ennemi et le groupe au combat natif ; il ne prouve pas à lui seul toutes les variantes d'entrée par proximité ou script. Les déplacements TB ont été envoyés par l'interface du jeu. Les commandes RTwP utilisent le chemin réseau client normal. Aucune téléportation, injection de PV ou fabrication de résultat aléatoire n'a été utilisée.

## Parcours 1 : TB, commandes puis transition RTwP

La sauvegarde a été chargée en commun. La pause de chargement a été levée automatiquement sur les deux pairs à 13 h 48 min 39 s.

1. À 13 h 48 min 56 s, déclenchement de la rencontre en TB. L'initialisation et la synchronisation du premier tour sont confirmées ; Wenduag atteint `Playing` sur les deux instances à 13 h 49 min 01 s.
2. À 13 h 49 min 32 s, déplacement de Wenduag envoyé par l'hôte et exécuté sur la cliente. Les relevés stabilisés donnent exactement la même position : `(53.7479439, 40.1222725, 26.5111427)`.
3. La fin du tour de Wenduag mène au tour de Daeran sur les deux instances à 13 h 49 min 58 s.
4. À 13 h 50 min 49 s, déplacement de Daeran envoyé par la cliente et exécuté sur l'hôte. Après synchronisation de fin de tour, les positions sont identiques : `(51.7397041, 40.0725632, 25.7925262)`. Le tour de Nounours démarre ensuite sur les deux pairs à 13 h 51 min 29 s.
5. La demande TB → RTwP est acceptée à 13 h 51 min 49 s. Les deux mondes atteignent `Running` / `RealTimeWithPause` / `Stable`, époque `1`, avec des séquences autoritaires non nulles.
6. La cliente demande la pause ; l'hôte puis la cliente l'appliquent. L'hôte demande ensuite la reprise et les deux mondes reprennent. Le résultat immédiat d'une requête peut précéder l'application distante ; les relevés suivants ont servi à vérifier la convergence.
7. Les commandes clientes de déplacement `1:1:1` et d'attaque `1:1:2` sont acceptées et diffusées par l'hôte. Les journaux hôte contiennent les jets d'attaque et de dégâts ; les journaux clients de ce parcours ne montrent pas les jets mécaniques autonomes de l'ancien essai.
8. Avant rechargement, une nouvelle pause stabilise les mondes. Les sept membres ont les mêmes positions, PV et états de combat, sans différence à la comparaison. Les deux empreintes de combat valent `27FA8FE41F562B77`.

Preuve structurée : `local-artifacts/diagnostics/a0-corrections-20261031-1344/scenario-1-comparison.json`.

## Défaut restant R1 : chargement ordinaire depuis RTwP actif

**Suite du travail le même jour :** R1 a ensuite été corrigé et retesté sur deux instances, avec reprise de combat et fermeture synchronisée. Voir le [rapport de correction et de réessai R1](correction-r1-rechargement-rtwp-2026-10-31.md). Les observations ci-dessous décrivent le candidat antérieur et restent conservées comme preuves historiques.

À 13 h 54 min 03 s, l'hôte demande le chargement de la même copie de sauvegarde hors combat via le workflow existant `load_save`, depuis le combat RTwP encore actif et en pause. Ce n'est pas la création ou le chargement d'une archive de sauvegarde de combat TB.

**Attendu :** les deux pairs abandonnent l'ancien contexte de combat et reconstruisent la sauvegarde avec les PV normaux.

**Observé :**

- À 13 h 54 min 04 s, l'hôte échoue dans la capture du checkpoint de fin de combat : `NullReferenceException` dans `MultiplayerHost.CombatEnd.cs:120`, suivie de `final-checkpoint-capture-failed`. Cette pile est distincte de l'ancien accès nul dans `OnNotifyCombatInitiatedAsync`.
- La cliente demande ensuite une récupération après `host-id-timeout` pour un identifiant d'effet de zone.
- La cliente entre en `GameOver`. Le relevé à 13 h 55 affiche `hp=0` et `maxHp=0` pour les sept membres ; l'hôte conserve leurs valeurs normales.
- Le contexte réseau de l'hôte indique encore `Running` / RTwP alors que ses unités natives sont hors combat. La cliente est en `Repairing`, sans objet de combat réseau.
- La sauvegarde reste interdite sur les deux instances. Aucun déblocage forcé ni enregistrement n'a été tenté.

La défaite est confirmée visuellement. Les journaux et réponses de la passerelle sont archivés dans `session-1-final/` et `responses/` du dossier de diagnostic. L'erreur hôte commence ligne 892 de `session-1-final/host/Player.log`.

**Piste à auditer, non cause démontrée :** invalidation du contexte RTwP et des captures différées lors d'un chargement ordinaire, ainsi que les droits d'hydratation des statistiques clientes. La reconstruction native protégée décrite ci-dessous a, elle, terminé son certificat et sa fermeture. Ces deux chemins doivent rester distingués.

## Parcours 2 : nouvelle rencontre directement en RTwP

Après redémarrage propre des processus et nouveau chargement partagé de la copie, le mode RTwP est choisi hors combat par la touche T. Les deux journaux confirment l'application du réglage natif et de la préférence à 16 h 34 min 38 s.

1. À 16 h 34 min 54 s, déclenchement de la rencontre avec RTwP déjà sélectionné. Les relevés capturent l'hôte en `Initiating` et la cliente en `Resyncing`, avec époque et séquence `0`, **pause active, barrière active et sauvegarde bloquée**. La demande cliente de pause est acceptée dans cet état.
2. Les accusés du changement de mode aboutissent à 16 h 34 min 57 s. Les deux instances atteignent `Running` / RTwP / `Stable`, époque `1`, puis échangent des snapshots et leurs empreintes appliquées.
3. L'hôte produit les résultats mécaniques et la mort de l'ennemi cible. La fermeture autoritaire commence à 16 h 35 min 13 s, avec les unités supplémentaires découvertes pendant la rencontre.
4. La fermeture engage une reconstruction native protégée de la cliente à l'époque `2`. Le checkpoint `9d116609df764067b9471312a995a70a` est transféré et ses octets vérifiés à 16 h 35 min 58 s. Ce transfert n'est pas considéré comme une restauration mécanique réussie.
5. La cliente charge réellement ce checkpoint. La référence native est vérifiée à 16 h 37 min 25 s ; le protocole poursuit ensuite les confirmations requises.
6. La cliente ferme le combat à 16 h 37 min 26,527 s, puis l'hôte à 16 h 37 min 26,993 s. L'hôte confirme `Final combat checkpoint confirmed by all peers` à 16 h 37 min 27,002 s, empreinte décimale `4580708993570347845`.
7. La comparaison finale montre les deux instances hors combat, phase `Stable`, époque `2`, sauvegarde débloquée. Les sept membres ont exactement les mêmes positions, PV, PV maximum et états de combat ; distance maximale mesurée entre positions correspondantes : **0 m**.

Cette séquence vérifie la fermeture et une reconstruction native de fin de combat dans ce scénario local. Elle ne démontre pas tous les cas de réparation, de repli TB ou de reconnexion.

Preuve structurée : `scenario-2-final-comparison.json`. Journaux : `session-3-final/host/Player.log` et `session-3-final/client/Player.log`. La confirmation finale hôte se trouve ligne 1838 ; la fermeture cliente ligne 2329.

## Bilan des quatre incidents initiaux

| Incident | Résultat borné de ce réessai |
|---|---|
| A0-01, premier tour TB bloqué | Non reproduit ; plusieurs tours successifs et commandes hôte/client vérifiés |
| A0-02, initialisation RTwP et simulation à époque zéro | Initialisation directe gelée et protégée, puis époque stable ; fermeture complète également vérifiée |
| A0-03, accès nul après redémarrage raté | Ancienne pile non reproduite dans ces parcours ; le timeout de redémarrage raté reste couvert par le test automatisé, sans injection moteur de cet échec |
| A0-04, pause impossible en état non synchronisé | Pause active dès l'initialisation directe et demande acceptée ; pause/reprise normales également vérifiées en RTwP stable |

R1 reste un défaut majeur distinct. Les quatre lignes ci-dessus ne constituent pas une validation générale du mod.

## Tentatives écartées et limites

- Des relances intermédiaires ont présenté des bundles déjà chargés, puis un chargeur natif invalide avant le scénario. Ces tentatives sont archivées sous `startup-retry-host/` et `session-2-invalid-startup/`. Elles n'ont pas servi à conclure sur le démarrage du combat. La relance finale a laissé le démarrage natif se terminer sans commande `advance_startup` et n'a pas présenté ces erreurs de bundles.
- Une exception `CameraRig.OnDisable` et des avertissements de libération de ressources apparaissent lors de la fermeture des jeux. Leur attribution n'est pas établie ; les processus ont quitté. Ils ne sont pas confondus avec R1 ou A0-03.
- Aucun essai sur deux PC physiques, aucune campagne, aucun contrôle du doublage, des sorts complexes, de la reconnexion ou du repli après réparation échouée n'est revendiqué.
- Aucun marqueur de validation runtime n'est créé. Le manifeste du candidat conserve `runtimeValidated=false`.

## Remise en état et conservation

- Les deux jeux sont fermés. Le paquet antérieur est restauré dans l'installation : empreinte `C354EB88D3CC5497719D52CCE3EDD59393BE8B91477AE298A15F67AFDA906722`, vérifiée après restauration.
- Le paquet corrigé essayé est conservé hors de `Mods` dans `local-artifacts/mod-slots/a0-corrections-41B77DAB-teste-20261031`, ainsi que dans le dossier du candidat.
- Les paramètres Unity Mod Manager sont identiques à ceux sauvegardés avant l'essai, empreinte `47E4FDE1A2687FC1861774228F93F3F215BEE4A0BB65B40B7E30EE1B5B7365B9`.
- La copie hôte de sauvegarde conserve son SHA-256 `C6A4040F5E67D529CB5A709B5642372D27487E71C8F777989943DF66FDC0E30A`, ses 3 291 316 octets et sa date UTC du 23 octobre 2026 à 19 h 53 min 52 s. Le dossier hôte contient toujours cette seule archive `.zks`.
- Aucun original de campagne n'a été utilisé et aucune sauvegarde de combat n'a été créée.
- Racine des preuves privées : `local-artifacts/diagnostics/a0-corrections-20261031-1344/`. `restoration-receipt.json` consigne les empreintes et l'absence de processus ou de marqueur après restauration. Les journaux restent privés ; ils n'ont pas été publiés.
