# Retrait autoritaire des zones natives — 7 octobre 2026

Variante 0.4.16.21 ; protocole 51 et empreinte schéma 16 inchangés.

## Comportement implémenté

Une zone native ordinaire créée avant la prise de contrôle RTwP peut maintenant être retirée lors de l'application d'un état hôte qui ne la contient plus. La préparation n'accepte que le type natif exact, sans faits ni parties mécaniques inconnues. Les représentations détachées gardent leur chemin existant.

`RealTimeNativeAreaRetirement` vérifie les abonnements globaux de la zone et ses adaptateurs natifs de dégâts. Chaque adaptateur doit appartenir à cette zone. Une inscription partielle, dupliquée, un indicateur incohérent ou une distribution d'événement en cours provoque un refus avant mutation. Les inscriptions sont suspendues sans `HandleEnd`, `UnitExit`, destruction mécanique de faits ou notification `AreaEffectDestroyed`.

Le retrait utilise ensuite les magasins transactionnels existants : proxy, cache, scène et pools. En cas d'annulation, les données et listes sont restaurées avant les abonnements. La restauration remet l'ordre initial des callbacks et refuse d'écraser des inscriptions étrangères modifiées. Un échec de restauration du registre ou des pools laisse la zone suspendue et protégée.

Les trois callbacks natifs de conséquences de dégâts des adaptateurs sont également interceptés. Une zone marquée reste inerte même après déconnexion et même si une annulation incomplète a rétabli certains abonnements. Les callbacks des zones ordinaires sur l'hôte restent autorisés ; une exécution autonome sur une cliente active est refusée et diagnostiquée.

Après vérification de l'état réseau, le nettoyage de la vue native utilise son `OnDisable` audité : arrêt d'écoute, retrait de la découpe de navigation et désactivation du pilote de mise à jour. Les FX et la racine sont ensuite retirés ; la vue est marquée pour empêcher son `OnDestroy` différé d'entrer dans le code mécanique dépendant du monde. Ce nettoyage Unity n'est pas exécuté par les essais hors jeu.

## Références mécaniques restantes : obligation persistante

Des parties de personnages peuvent encore référencer une zone retirée. Ne pas émettre `AreaEffectDestroyed` évite ses callbacks mécaniques, mais ne prouve pas que tout ce graphe est reconstruit.

`RealTimeNativeGraphRepair` conserve donc une obligation liée à l'état persistant du monde, hors des pools filtrés. Une annulation vérifiée enlève sa propre obligation ; un retrait validé la conserve. La sauvegarde, la fermeture mécanique du combat et le passage au tour par tour consultent ce garde-fou. Une empreinte réseau identique ne le supprime pas.

Il n'existe pas encore de reconstruction mécanique complète permettant de lever cette obligation. Ce comportement permet d'avancer dans le combat RTwP, mais ne constitue pas une récupération jouable démontrée ni une fin de combat sauvegardable après retrait natif. Une pause protégée n'est pas comptée comme une réussite.

## Vérification hors jeu

Tests ciblés ajoutés : suspension/annulation, retrait validé, abonnement absent, abonnement réapparu, désinscription qui modifie puis échoue à chacune des trois positions, restauration qui échoue, conflit d'indicateur, véritables inscriptions globales et aux trois règles de dégâts, ordre de callbacks partagé, inscription partielle/dupliquée, distribution en cours, changement d'adaptateurs, annulation du registre/pool et échec de cette annulation, protection persistante des callbacks de dégâts.

Le diagnostic Mono exerce une zone synthétique, ses véritables inscriptions globales, leur restauration et un retrait transactionnel sans vue. Il installe aussi les trois interceptions de dégâts et appelle les véritables méthodes natives sur un adaptateur synthétique dont le déclencheur est nul : les originaux ne doivent pas être exécutés. Aucun processus du jeu, objet Unity ou véritable personnage n'est créé.

Les compilations par défaut et prototype passent chacune 802 tests : 33 traduction, 41 réseau et 728 principaux. Les 233 clés françaises et leurs paramètres sont vérifiés. L'avertissement AutoMapper `NU1903` préexistant reste non résolu.

Compilation par défaut : empreinte `E4FF6BE083C931DD93026B6C749550AE378B761578EA6B60AEBF3484EDC99DAB`.

Candidat prototype exact : `0AE8EA60BEEE87DA626955CEDC8C87F8BE978F8C30A40978FD3B0934DB4915C1`. DLL SHA-256 : `D2754C046D629840092BE1756B4042AB84845B178EB6ACBA8FA49987BD2FE41F`.

Diagnostic Mono de ce candidat réussi : `native-area-retirement-without-world=True`, avec les contrôles précédents. Preuves archivées dans `local-artifacts/a0-candidates/0AE8EA60BEEE87DA626955CEDC8C87F8BE978F8C30A40978FD3B0934DB4915C1/offline-rule-dispatch/`, avec `success=true`, `gameStarted=false`, `runtimeValidated=false`. Les trois interceptions natives de dégâts ont été installées et appelées sur un adaptateur synthétique sans exécuter leurs originaux.

Ces essais ne prouvent ni le nettoyage visuel, ni la navigation Unity, ni la reprise mécanique complète ou une rencontre réelle à deux joueurs. Le candidat reste expérimental, préparé hors de l'installation, et non activable par la procédure finale.

## Suite critique

Reconstruire les références et composants mécaniques nécessaires à la reprise, traiter le retrait des buffs natifs, créer les invocations et renforts manquants, auditer les FX de campagne et vérifier ensuite le moteur sur deux instances. Les animations, le nettoyage différé, la navigation, les voix et une session de campagne restent non validés.

Aucun jeu lancé, aucune installation/activation expérimentale, aucun marqueur de validation moteur. Mod stable, sauvegardes originales et production des voix inchangés.
