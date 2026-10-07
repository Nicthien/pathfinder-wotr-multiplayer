# Valeurs de contexte des buffs — 7 octobre 2026

Variante 0.4.16.12, protocole 48, schéma d'empreinte 14. Aucun lancement de jeu, installation ou modification du doublage.

## Capture et transport

Le prototype capture désormais une chaîne plate de contextes, du buff vers ses parents. Chaque entrée contient les identifiants du blueprint, du caster, du propriétaire et de l'objet source, le type de contexte d'aptitude, les paramètres déjà calculés, les rangs, valeurs partagées et propriétés, les descripteurs/école/niveau de sort, la direction, la désactivation des effets visuels et la cible explicite éventuelle.

Les tableaux sont copiés, pas conservés par référence. Les identifiants d'entités sont lus dans les références natives sans résoudre les entités. `MechanicsContext.Params` est un accès en lecture avec repli sur le parent ou la valeur vide ; aucun `Recalculate`, callback de règle, sérialisation de sauvegarde ou chargement n'est exécuté par cette capture.

La topologie est vérifiée avant l'échantillonnage : référence cyclique interdite et maximum 16 contextes. Les tableaux sont bornés à 256 entrées et les directions/cibles non finies sont refusées. Le réseau ajoute les données sous le champ 14 du buff et le niveau de sort sous le champ 7 des paramètres d'aptitude, sans renuméroter les champs existants.

L'empreinte inclut les contextes, paramètres, tableaux et références utiles. Une différence de rang calculé, de DD, de niveau de sort ou de parent n'est donc plus invisible derrière un buff de même identifiant.

## Application aux contextes existants

Avant de modifier les unités, le service contrôle la topologie, les blueprints, références d'entités/objet source, types de contexte, formes des tableaux et références de cibles. Les cibles ponctuelles changées restent refusées ; ce chemin ne remplace pas silencieusement leur origine.

Les paramètres sont convertis avant toute écriture dans la chaîne. L'application copie ensuite les tableaux et les champs simples : paramètres, descripteurs, école, niveau, direction et indicateur visuel. Elle ne fait ni `Recalculate`, ni activation de fait. Le chemin tour par tour ordinaire conserve son service historique ; la capture supplémentaire est limitée au prototype.

## Limites importantes

Il s'agit de **valeurs de présentation**, pas d'un contexte mécanique complet pour reprendre la simulation cliente. Les calculateurs, règles en cours, état d'ombre, compteurs liés et composants runtime ne sont pas reconstruits par ce contrat. Les données d'aptitude et d'objet sont transportées pour identifier leur origine, pas pour créer un inventaire ou un livre de sorts.

La brique de registre des buffs absents n'est toujours pas appelée automatiquement : son intégration doit construire les contextes depuis l'hôte, enregistrer les nouvelles représentations dans une transaction, relire l'état complet, puis confirmer ou annuler. Les zones, invocations et le repli mécanique au tour par tour restent à terminer. Une empreinte divergente garde la partie protégée ; cela ne constitue pas encore une récupération jouable.

## Vérifications

Les tests couvrent la sérialisation du contrat, les cycles, la profondeur, les valeurs invalides, l'effet des divergences sur l'empreinte et la copie/application native des valeurs sur un contexte synthétique sans monde. Les diagnostics Mono du paquet exact restent hors jeu et ne peuvent pas créer de marqueur de validation moteur.

Prototype compilé et préparé : `B1EA116C0A2CCB5BDEFB9204E5F2E57FE5B41C5FCCC5EB5D4B65436C6E3FDC84`. SHA-256 du mod : `33A815A20BE52F978B563742CF81B1E558FC8B15A5715C972B54553207EB0D1E`. Les 637 tests réussissent (33 de localisation, 41 réseau, 563 principaux).

La compilation Release sans prototype réussit également les 637 tests ; empreinte locale `95D68A6F8340CF5D574311A7D5B7F2EF87DECD9CF043996C0CA621AF07C19970`. Le mod stable installé reste 0.4.16 et la variante installable reste désactivée. Aucun marqueur moteur n'est présent.

Le diagnostic Mono du même paquet réussit : `installed-buff-context: captured-without-world=True; applied-without-recalculation=True`. La copie de tableaux, l'écriture des rangs et paramètres sur le contexte synthétique, l'enregistrement/annulation des buffs vides et les protections précédentes restent vérifiés. Le convertisseur du diagnostic est simulé ; cette preuve ne valide pas la résolution des références et aptitudes dans une vraie partie. Le manifeste reste `runtimeValidated=false`.
