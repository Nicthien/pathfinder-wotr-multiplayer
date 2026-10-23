# MCP de contrôle du laboratoire WotR

Ce serveur MCP pilote uniquement les instances isolées du laboratoire
`local-artifacts/combat-save-lab`. Le mod doit être compilé avec
`HostPresentationPrototype=true`, le profil doit contenir
`.rtwp-test-profile` et l'instance doit être lancée avec `-Automation`.

Le serveur communique avec le mod par des requêtes JSON locales placées sous
`<profil>/McpBridge`. Aucun port réseau n'est ouvert. Un identifiant et un
jeton aléatoires sont recréés à chaque lancement afin qu'une ancienne requête
ne puisse pas être rejouée.

Quand il est activé, le build de laboratoire force aussi l'option Unity
`runInBackground` afin que les deux instances locales continuent à traiter le
réseau et les requêtes MCP lorsqu'une seule fenêtre a le focus. La commande
`advance_startup` permet de franchir l'écran initial dans le profil isolé ; le
pont recrée ensuite son fichier `ready.json` si le chargement de scène redémarre
le composant.

## Outils exposés

- `advance_startup` : passage contrôlé de l'écran initial au menu principal ;
- `get_state` et `compare_instances` : état réseau, combat, pause, zone,
  positions et PV de l'équipe ;
- `set_pause` : pause/reprise par le chemin multijoueur normal ;
- `set_combat_mode` et `force_combat_sync` : commandes réservées à l'hôte ;
- `save_combat` et `load_save` : workflows synchronisés du mod, limités aux
  sauvegardes du profil hôte isolé ;
- `list_saves` et `wait_for_state` : inspection et attente déterministe.

## Configuration Codex

Le serveur requiert Python 3.10 ou supérieur et le paquet indiqué dans
`requirements.txt`. Depuis une session locale :

```powershell
codex mcp add wotr-test -- py -3.13 G:\Codex\PathFinder\WotR-Multiplayer-RTwP\tools\wotr-mcp\server.py
codex mcp list
```

Une nouvelle session Codex est nécessaire après l'ajout : un serveur configuré
en cours de conversation n'apparaît pas rétroactivement dans l'inventaire des
outils déjà chargé.
