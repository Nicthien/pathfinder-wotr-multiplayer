# Valeurs calculées des caractéristiques — 7 octobre 2026

Variante 0.4.16.14, protocole 50, schéma d'empreinte 15. Aucun lancement de jeu ou d'outil de voix ; aucune installation.

## Pourquoi cet incrément

La restauration antérieure dite complète ne recharge pas les composants mécaniques natifs : elle met à jour des valeurs, positions et données de tour. L'inspection du sérialiseur natif `UnitSerialization.Serialize` montre qu'il appelle `TurnOff`, `PreSave`, puis `TurnOn` sur l'unité. Il ne peut donc pas être utilisé comme une copie passive sans gérer ses conséquences mécaniques. **Ce sérialiseur n'a pas été exécuté.**

Un manque préalable concernait les résultats dérivés : seuls les PV, PV temporaires et dégâts non létaux avaient leurs caches calculés répliqués explicitement. Un buff de présentation nouvellement créé ne pouvait pas, à lui seul, fournir la CA, les caractéristiques, les compétences ou les bonus calculés de l'hôte.

## Chemin autoritaire ajouté

- Capture de chaque entrée native de `CharacterStats.AllStats` : type, valeur de base, valeur modifiée, valeur modifiée brute et valeur permanente. La liste native inspectée comporte 38 entrées ; le protocole transmet les entrées effectivement présentes, sans imposer arbitrairement cette longueur.
- Transmission dans les événements durables `UnitResult` et les états complets RTwP, sous le nouveau champ 9 du descripteur d'unité.
- Validation des types, des doublons, des formes des collections et de la cohérence des trois valeurs vitales avec leur copie dans le résultat vital.
- Inclusion de tous ces caches dans l'empreinte canonique, avec tri par type. Une divergence de CA, vitesse ou bonus ne reste donc plus invisible derrière des PV identiques.
- Écriture des champs natifs calculés sans `UpdateValue`, setter de base, callback `OnChanged`, dépense ou jet client.
- Protection du setter de base et de `UpdateValue` pour toutes les caractéristiques côté client RTwP, pas uniquement les trois valeurs vitales. L'hôte conserve le chemin natif et publie les résultats modifiés.
- Pas de capture d'une unité partiellement construite avant que sa collection de caractéristiques existe. Les événements suivants et états complets prennent en charge son état initialisé.

Le chemin tour par tour n'ajoute pas cette capture. L'absence des données est distinguée d'une collection valide ; un résultat RTwP reçu sans ces données ne passe pas la validation contre une unité dont la capture native les contient.

## Limites à ne pas confondre avec une reconstruction mécanique

Les totaux/caches calculés ne remplacent pas les listes de modificateurs, les composants de buffs, les abonnements, les compteurs liés, les propriétés dérivées spécifiques ni leurs historiques. Les détails d'infobulles et le rendu de l'interface restent à vérifier. La restitution d'un contexte mécanique complet et la réactivation du tour par tour ne sont pas résolues par cette transmission.

Le garde-fou de caractéristiques est actuellement global pendant le combat RTwP client. Une éventuelle valeur temporaire calculée par l'interface peut donc également être interceptée ; les faux positifs et la portée de cette protection devront être vérifiés dans le moteur avant activation. La fréquence et le coût des événements de caractéristiques ne sont pas encore mesurés en combat réel.

Les barrières contre sauvegarde et réactivation mécanique d'une représentation inerte restent en place. Les zones et invocations manquantes, le retrait sûr des buffs mécaniques ordinaires, la reconnexion et la récupération jouable restent à terminer.

## Tests hors jeu

Quatorze nouveaux tests couvrent copie/application native sans callback, pénalités négatives, structures malformées, caractéristique native manquante, caches divergents, diagnostic nommant l'unité et les caractéristiques calculées, ordre canonique, capture absente, cohérence des valeurs vitales et copie profonde/dédoublonnage des événements durables. Le test réseau existant vérifie également le transport d'une valeur calculée négative.

Le diagnostic Mono utilise une valeur native synthétique de bonus d'attaque : il invoque le vrai setter installé pour vérifier son blocage client, puis applique et recapture les caches confirmés sans recalcul ni callback. Cette preuve reste hors monde de jeu et ne valide ni un combat ni l'interface.

Le prototype compile et réussit **675 tests** : 33 de localisation, 41 réseau et 601 principaux. Candidat exact : `5B8584CBF428FB8BC5D2C3B9CA3788CD583A35E4786EDF386EC9AF43F6F111DE`. SHA-256 du DLL : `6007F9EF6C988ECF0FB91A407A291D7D493E96A3B3AD4327F143F4B2E0CC774E`.

La compilation Release sans prototype réussit également les 675 tests ; empreinte `07A62C111E2428F1C0EA27D3B7EF87FD8F874D8C07C711517A2B300FEF2FC1EA`. Le mod stable installé reste 0.4.16 et la variante reste désactivée.

Le diagnostic Mono du même candidat réussit avec `installed-calculated-stats: client-setter-blocked=True; applied-without-recalculation=True`, ainsi que les protections, contextes et transactions des incréments précédents. Son manifeste reste `gameStarted=false` et `runtimeValidated=false`. Aucun marqueur de validation moteur n'est autorisé par ces tests.
