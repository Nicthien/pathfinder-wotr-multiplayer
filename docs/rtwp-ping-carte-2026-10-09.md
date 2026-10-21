# Ping de carte — variante 0.4.16.43, protocole 64

Empreinte du candidat : `60BF2596A3D069D1621296534B6D1CA8398DC6F669F4FE06E758E782D97686CB`.
Compilation finale : zéro erreur, deux avertissements NU1903 préexistants concernant AutoMapper. ZIP UMM : `local-artifacts/livraisons/WOTRMultiplayerRTwP-0.4.16.43-UMM.zip`.

## Utilisation

Dans une zone de jeu, maintenir **Ctrl gauche ou droit**, puis effectuer un **clic gauche** sur le terrain : marqueur partagé et petit son de sélection du jeu sur les deux PC. Bleu pour l'hôte, orange pour la cliente. Durée de trois secondes, y compris pendant une pause.

Le clic est réservé au ping : il ne déplace pas les personnages, ne lance pas d'attaque, n'interagit pas avec un objet et ne lance pas le sort sélectionné. Les handlers de terrain, unité, objet, capacité sélectionnée et placement de repos sont interceptés avant l'exécution normale. Les clics simulés à partir du réseau et les clics dans l'interface ne sont pas interceptés.

Le raccourci de ping historique du mod reste disponible. Limite locale de 400 ms entre deux pings. Les indications hors caméra existantes sont conservées. Aucun nouveau fichier audio ni changement de WOTR_AIVO : le son intégré `GlobalMapLocationsSelect` est réutilisé.

Cette commande cible le monde 3D ; elle n'ajoute pas de Ctrl + clic dans la carte plein écran, la minimap ou les handlers de croisade.

## Réseau et présentation

Réutilisation du message transférable `NotifyPingedByPlayer`, ajout de session, chargement et graine de zone pour rejeter les messages retardés. Aucun ordre mécanique, jet ou changement de mode associé au ping. Les deux PC doivent utiliser le même ZIP .43/protocole 64.

Réutilisation du marqueur existant, coordonnées monde corrigées, couleurs normalisées, découverte des renderers sans dépendance à leur profondeur de hiérarchie, destruction différée et nettoyage des matériaux instanciés. Expiration par temps réel indépendante de la pause du jeu. Une création mise en file est abandonnée si la zone chargée change avant son affichage.

Journaux : `Local map ping sent` et `Remote map ping received`, avec joueur, zone et position.

## Vérification et limites

Compilation Release seule, sans jeu lancé ni suite de tests. Inspection statique des méthodes du jeu : signature OnClick et branche retour du contrôleur. Le préfixe retourne false pour supprimer aussi l'événement de clic normal et le postfix de réplication des objets ; le contrôleur n'essaie pas un autre handler après ce retour.

Le comportement moteur, le rendu du prefab, l'audibilité du son et le trajet réseau à deux PC restent à vérifier en jeu. Aucun marqueur de validation moteur, aucune activation ou installation automatique, aucun changement aux voix ou sauvegardes originales.

Prochain essai manuel : un ping de chaque joueur sur le terrain, puis pendant la pause ; vérifier la position et les couleurs sur les deux écrans, le son, l'expiration, et l'absence de déplacement. Vérifier aussi Ctrl + clic avec un sort sélectionné et sur un objet/unité, puis un clic ordinaire sans Ctrl. Aucun essai de ce type n'a été exécuté par l'agent.
