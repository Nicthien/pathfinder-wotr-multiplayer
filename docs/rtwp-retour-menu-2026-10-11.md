# Retour multi → solo → menu — 0.4.16.51 / protocole 67

Le journal hôte du 11 octobre montre une reconstruction de menu à 08:22:39 ignorée par les contrôleurs (« Already initialized »), puis une exception dans `IsActive` au clic Multi à 08:23:38. Les contrôleurs singleton conservaient des objets Unity détruits ; une fenêtre inactive peut disparaître sans exécuter son nettoyage `OnDestroy`.

Le contrôleur est maintenant reconstruit quand l'objet de menu change ou que sa sélection a été détruite. Le nettoyage tolère les objets déjà détruits et retire uniquement ses propres écouteurs. Une ancienne fenêtre détruite tardivement ne peut plus nettoyer les contrôleurs appartenant à une nouvelle fenêtre. Lecture de `IsActive` protégée et diagnostic `[MenuLifecycle]` ajouté.

Compilation Release seule, sans jeu ni tests exécutés. Retour multi → menu → solo → menu → Multi à confirmer en jeu. Aucun changement de protocole, du stable, des voix ou des sauvegardes.

Empreinte du candidat final : `7457BF636D6FA7798F5A3773B83F1478F46A429F93916CCB66216A52B76B31C3`. ZIP UMM exporté et contenu vérifié : `WOTRMultiplayerRTwP-0.4.16.51-UMM.zip`, SHA256 `FE7831BDFCA2B69F8048C73DD04ECB2F9222AD38A58E8BD79D76FF43B50ACBF5`. Compilation : zéro erreur, avertissements NU1903 existants sur AutoMapper. Aucun paquet installé.
