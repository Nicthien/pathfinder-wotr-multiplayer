# Représentation native d'un buff — préparation de la reconstruction

Variante locale 0.4.16.11, protocole réseau inchangé (47). Aucun lancement de jeu ou installation.

## Portée exacte

`RealTimeBuffReplicaStore` est une brique native destinée à la reconstruction cliente. **Elle n'est pas encore appelée par la réparation réseau.** Les checkpoints contenant un buff absent restent refusés par les contrôles existants. Le but de cet incrément est de vérifier le cycle de création/enregistrement/annulation avant de l'intégrer à la restauration d'un état complet.

## Construction sans composants mécaniques

L'inspection IL de l'assembly installée distingue deux chemins :

- Le constructeur ordinaire de `Buff` appelle `EntityFact.Setup`, qui crée des composants runtime à partir du blueprint. L'ajout normal par `EntityFactsManager.Add` prépare l'attachement, appelle `Attach`, puis des délégués d'événements. `Attach` consulte le monde et appelle les composants, `OnAttach` et potentiellement `Activate`.
- Le constructeur `Buff(JsonConstructorMark)` initialise la liste vide de composants, sans blueprint, contexte ni activation. Aucun `PostLoad` ou cycle de sauvegarde n'est nécessaire pour l'invoquer.

La nouvelle brique utilise ce second constructeur, renseigne directement le blueprint validé, l'identifiant hôte, le contexte fourni, le rang et les échéances. Elle exige que les composants restent vides, que le fait ne soit ni actif ni abonné au bus d'événements. Le contexte doit être préparé et validé par le futur appelant ; ce constructeur ne capture pas le contexte complet de l'hôte.

## Registre et annulation

L'enregistrement vérifie le propriétaire du registre, l'absence d'attachement préalable et les identifiants déjà présents, sans distinction de casse. Il ne touche qu'à la liste native, au cache typé et au lien vers le manager. Il n'utilise ni `Add`, ni `Attach`, ni leurs délégués.

Une poignée d'enregistrement est annulée à sa libération tant qu'elle n'a pas été confirmée. L'annulation retire l'instance de la liste et du cache et libère son lien au manager, sans `Remove`, `Detach`, `Dispose` natif ou callback de retrait. La libération répétée est sans effet. La confirmation est réservée au futur appelant après vérification de l'état de référence.

Le suivi utilise des clés faibles, afin de ne pas conserver des buffs devenus inaccessibles. Seules les représentations créées par cette brique peuvent être enregistrées : un fait mécanique préexistant ne peut pas être traité comme une représentation vide.

## Limites et étape suivante

- Les tests utilisent des blueprints, contextes et propriétaires synthétiques ; ils ne prouvent pas l'affichage d'icônes ou de particules.
- Les paramètres de contexte, références d'origine, propriétés dérivées et visibilité dynamique doivent être capturés/restaurés avant l'intégration réseau.
- L'application d'un état complet doit regrouper les nouvelles créations, les annuler en cas d'échec et ne les confirmer qu'après relecture et comparaison d'empreinte.
- Une représentation sans composants ne permet pas de reprendre le tour par tour par simple réactivation. Le repli doit restaurer un état mécanique complet ou rester protégé.
- Les suppressions de faits mécaniques existants, zones et invocations ne sont pas couvertes par cette brique.

Les diagnostics Mono éventuels restent hors jeu, sans `Game.Instance`, monde ou unité réelle ; ils ne doivent jamais produire un marqueur de validation moteur.

## Preuves du paquet exact

Prototype compilé et préparé : `CDFFB87737A3FCC5DF6A89E03E6484F24C1D7BD0DD972F311642E74C289E7DA6`. SHA-256 de l'assembly du mod : `F13F1A566D7F2DF284F3D8D6364676D03A519BB0E615D2C03D4DFAE73614271A`. Les 617 tests réussissent (33 de localisation, 40 réseau, 544 principaux).

La compilation Release sans prototype réussit également les 617 tests ; empreinte locale `DBBFC4C3F8D6371631F42CF407AAB3BC3B83C2784B903694079F4C0D3788C416`. Aucun appel au nouveau registre n'est ajouté au chemin de jeu normal dans cet incrément.

Le diagnostic Mono du même paquet réussit : `installed-buff-replica: empty=True; registered-without-components=True; cache-rollback=True`. Il instancie un blueprint, un contexte et un propriétaire synthétiques, utilise le constructeur natif vide, préchauffe le cache des buffs, vérifie l'enregistrement puis l'annulation. Les interceptions de règles, de statistiques vitales et de planification des buffs restent vérifiées. Le manifeste conserve `runtimeValidated=false` et `gameStarted=false` dans la preuve de diagnostic.
