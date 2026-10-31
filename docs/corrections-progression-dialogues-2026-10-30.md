# Corrections de progression et de dialogue — 30 octobre 2026

Cette validation du protocole 91 est historique. Le complément [Sécurisation des progressions](securisation-progression-2026-10-30.md) décrit les corrections et les vérifications du protocole 92.

Le protocole RTwP passe de **90 à 91**. Les deux joueurs doivent utiliser le même nouveau paquet ; l'égalité des paquets reste contrôlée par leur empreinte.

## Progression des personnages

Les 39 notifications de progression concernées transportent une identité explicite : identifiant de progression, session, graine de chargement, personnage, type et propriétaire. L'hôte publie cette identité au démarrage ; les choix et la validation la conservent lorsqu'ils sont relayés. La cliente ne tente plus de retrouver leur destination à partir de l'expéditeur apparent, qui est toujours l'hôte dans cette topologie réseau.

Les progressions différées sont indexées par identifiant de progression, indépendamment du personnage actuellement ouvert et de son type. Deux personnages du même propriétaire peuvent donc avoir des réplications en attente distinctes. Les identités étrangères, incohérentes ou relatives à un ancien chargement sont refusées. Les messages reçus après la fin d'une progression différée ne sont pas rejoués une seconde fois. Les ouvertures imposées par le jeu conservent également l'identité publiée par l'hôte.

Chaque relecture est liée à la session et à son propriétaire actif. Un contexte de relecture transmet cette autorisation jusque dans les callbacks du service de progression, y compris lorsqu'ils sont mis en file une seconde fois. La mutation réelle revérifie donc l'autorisation après la fin du callback extérieur. Un ancien contrôleur en arrière-plan est abandonné sans validation avant de servir une nouvelle progression.

## Dialogues

L'hôte refuse maintenant l'exécution d'une réponse tant que tous les joueurs synchronisés n'ont pas vu la réplique. Une entrée absente de l'historique des témoins n'est plus assimilée à un accord.

Une sélection demandée par la cliente est liée au tour de vote courant et au joueur auquel le contrôle des dialogues est attribué. L'hôte vérifie le contrôleur, les témoins et, pour un choix ordinaire, l'unanimité. La demande prépare ensuite le chemin normal de sélection de l'hôte sans annoncer prématurément une réponse acceptée. Le préfixe reconnaît la demande distante autorisée et ne bloque plus sa propre exécution. Le contexte est revérifié après la mise en file et après l'animation de sélection. Les deux ordres de vote sont couverts : hôte en premier et cliente en premier.

## Compilation et services de démonstration

La compilation de toute la solution a révélé des services factices du Playground qui ne correspondaient plus aux interfaces actuelles. Leurs signatures ont été mises à jour, notamment pour la progression, la carte globale, les réglages et le combat. Ces services ne certifient aucune reconstruction native : les opérations natives indisponibles refusent l'opération ou renvoient un résultat d'échec.

Le SDK officiel **9.0.318** a été installé sous `local-artifacts/toolchain/dotnet` pour respecter `global.json`. Aucun SDK ni paramètre du projet n'a été remplacé pour contourner cette exigence.

## Vérification

La vérification finale compile toute la solution en **Release**, puis exécute les trois projets de tests pour chacune des variantes avec et sans `HostPresentationPrototype`. Les résultats détaillés sont conservés dans `local-artifacts/verification-corrections/false` et `local-artifacts/verification-corrections/true`.

Les régressions couvrent les destinations de progression malgré un même expéditeur réseau, les propriétaires identiques ou différents, les types local et distant différents, le rejet d'anciens chargements et d'identités étrangères, le contrôle au dernier dispatch et les votes de dialogue. Un test vérifie aussi que les messages réellement émis portent l'identité annoncée par l'hôte ; 39 cas vérifient sa sérialisation dans les contrats réseau.

Résultats définitifs, SDK 9.0.318, configuration Release :

| Variante | Principaux | Réseau | Localisation | Total | Échecs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Standard | 1 340 | 206 | 33 | 1 579 | 0 |
| Prototype | 1 340 | 206 | 33 | 1 579 | 0 |

La solution complète, Playground compris, compile dans les deux variantes. Les six contrôles du protocole du paquet réussissent pour chacune ; la constante lue dans la DLL est 91. Les 279 clés de localisation et leurs paramètres concordent ; `git diff --check` réussit. Les fichiers TRX enregistrent les résultats de chaque projet séparément.

L'avertissement de dépendance AutoMapper `NU1903` préexistant reste hors de ce correctif. Les tests automatisés ne valident ni le rendu Unity, ni une campagne, ni le doublage audible, ni un combat réel entre deux instances. Aucun jeu lancé, aucun déploiement ou changement de sauvegarde effectué et aucun marqueur de validation runtime créé.
