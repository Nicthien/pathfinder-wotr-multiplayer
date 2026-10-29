# Fermeture autoritaire du combat — 7 octobre 2026

Variante locale 0.4.16.9, protocole 46, prototype de présentation activé uniquement par compilation. Aucun lancement de jeu, aucune installation, aucune modification des sauvegardes ou du doublage.

## Problème corrigé dans le code

Le callback natif `CombatEnded` pouvait supprimer l'objet de combat réseau avant la capture des dernières écritures de mort et de sortie de combat. La cliente pouvait également fermer son combat d'après son état local, sans confirmation de l'hôte.

Dans le prototype, ce callback conserve désormais l'identité du combat. Sur la cliente, une fin locale n'arrête pas la réception des états autoritaires ; un redémarrage local ne remplace pas l'identité de la rencontre. La sauvegarde reste interdite dans cette attente.

## Barrière finale

1. L'hôte conserve les participants réseau requis au moment du callback. Il attend la fin des actions atomiques et projectiles, puis capture l'état sur le thread principal, après le retour du callback. Une transition ou réparation déjà engagée doit d'abord se terminer.
2. `Prepare` transmet en pause un état final immuable, son empreinte, la session, la sauvegarde, une nouvelle époque et un identifiant de fermeture. La cliente applique et relit cet état avant son accusé de réception.
3. `Commit` confirme que les participants ont accepté la référence ; le combat reste conservé.
4. `Finalize` permet à la cliente de fermer son combat. L'hôte ne ferme le sien qu'après l'accusé final. Une répétition de `Finalize` reçoit le même accusé sans rejouer le nettoyage.

Un participant requis ne disparaît jamais de la barrière pour autoriser une sauvegarde. Après expiration, la pause et l'interdiction de sauvegarder sont conservées. Les retransmissions continuent si la référence existe ; un accusé tardif valide peut encore terminer la fermeture. Une capture échouée avant création de la référence ne constitue pas une récupération automatique réussie.

Les commandes, changements de mode et nouvelles réparations sont refusés pendant cette fermeture. Les anciens états ordinaires ne doivent pas écraser la référence finale. Le chemin tour par tour sans prototype conserve sa fermeture habituelle.

## État natif de la cliente

La fermeture finale s'exécute sur le thread principal, avec contrôle de l'identité de session, de sauvegarde et de combat. Elle vérifie qu'aucun membre du groupe n'est encore en combat, puis remet le cache global `Player.IsInCombat` à faux. Le setter installé est une écriture de champ ; aucun callback de quête, de mort ou de combat n'est rejoué pour cette remise à jour.

## Preuves et portée

Le paquet prototype exact `511BD782172BBB7E241A0896E4BBB99098B8C314964D20EC171EEBC9F0330E58` a été préparé dans `local-artifacts/a0-candidates`, sans installation. Compilation Release et 594 tests réussis : 33 de localisation, 40 réseau et 521 principaux. Les 233 clés françaises sont présentes avec des paramètres concordants.

La compilation Release sans prototype a également réussi les mêmes 594 tests ; les scénarios spécifiques au prototype y vérifient le maintien de la fermeture historique, pas la nouvelle barrière. Son empreinte locale est `34B2058971DB47D29BD0D8B4492BB8DBA9A106FDDD83567D57BA39E22C0E0B1C`.

Le diagnostic Mono de ce même paquet a réussi, y compris `party-combat-cleared=True`, six protections des écritures vitales, aucun callback de statistiques lors de la mort confirmée et le maintien du chemin hôte. SHA-256 de l'assembly du mod contrôlée : `DB90025B7A41BB5450E4D47966406183B56662D7514F745017C44850C2E2F04D`.

Les tests d'acteurs utilisent des services de jeu simulés, pas deux instances du moteur. Ils vérifient notamment la capture différée de la dernière mort, les trois étapes, l'accusé final perdu, le premier échec d'application, l'accusé tardif après expiration, la fin pendant un changement de mode et le rejet des identités invalides.

Le diagnostic Mono séparé vérifie les interceptions de règles et les écritures natives sur des objets synthétiques. Il ne crée ni monde, ni unité réelle, ni `Game.Instance`. Les résultats du paquet exact sont archivés dans son dossier `offline-rule-dispatch` ; son manifeste reste `runtimeValidated=false`.

## Limites non résolues

- Aucun combat réel à deux instances n'a validé cette barrière, ses animations ou le drainage des projectiles.
- Une nouvelle rencontre qui démarre avant la fermeture confirmée provoque une protection ; sa poursuite transparente reste à traiter.
- Une action ou un projectile qui ne termine pas, notamment avec une pause déjà active, peut conduire à l'expiration protégée.
- La reconstruction transactionnelle des unités, zones et buffs manquants reste incomplète. Un échec protégé ne doit pas être présenté comme un repli jouable.
- La reconnexion, le repli au tour par tour, la sauvegarde/relecture, les rencontres de campagne et les voix doivent encore être validés dans le moteur avec le paquet exact.

Aucun marqueur `RTWP_RUNTIME_VALIDATED.json` ne peut découler des seuls tests décrits ici.
