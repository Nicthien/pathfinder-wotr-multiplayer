# Autorité des zones de sorts — 7 octobre 2026

Variante 0.4.16.15, protocole 50 inchangé, schéma d'empreinte 15 inchangé.

## Défaut corrigé

La restauration des zones existantes appelait `AreaEffectEntityData.UpdateViewAndUnits` après avoir appliqué la position et la liste d'occupants de l'hôte. L'inspection du code natif montre que cette méthode appelle `UpdateUnits`, qui peut invoquer directement les composants d'entrée, de déplacement et de sortie. Ces composants ne passent pas nécessairement par Rulebook : bloquer seulement les règles ne suffisait pas.

Le chemin de présentation applique désormais le transform et les occupants confirmés sans appeler cette méthode. Le chemin tour par tour conserve son comportement précédent.

Deux groupes de protections réservés à la compilation prototype empêchent également :

- la simulation autonome des zones : `Tick`, `TickRound`, `HandleTimeChanged`, `HandleEnd`, `UpdateUnits`, `UpdateUnit` ;
- les callbacks mécaniques du blueprint : `HandleSpawn`, `HandleTick`, `HandleRound`, `HandleUnitEnter`, `HandleUnitMove`, `HandleUnitExit`.

Les appels réguliers de simulation sont ignorés côté cliente. Une tentative de callback mécanique inattendue signale `autonomous-client-area-callback:<méthode>:<identifiant>` au mécanisme existant de pause protégée. Les préfixes laissent passer l'hôte et les modes non concernés ; ils n'accèdent pas à `Game.Instance`.

La capture RTwP prend aussi la position réelle de la vue hôte lorsqu'elle existe : les zones attachées à une unité peuvent déplacer cette vue sans rafraîchir `m_Position`.

## Vérification

Quatre tests contrôlent les douze cibles natives, leur unicité, la restriction au prototype et le passage du chemin inactif. Le diagnostic Mono est étendu pour installer réellement les protections et invoquer les douze méthodes natives sur des objets synthétiques sans monde ni vue. Il contrôle aussi les préfixes du chemin hôte, sans exécuter ses méthodes natives sur ces objets incomplets.

Prototype : 679 tests réussis (33 traduction, 41 réseau, 605 principaux), 233 clés françaises vérifiées. Candidat exact préparé hors du jeu : `7BC13CC13849BFED81C365DEC8D714959CA63A4D77DAC62C37F396870B38C979`. DLL SHA-256 : `18D0F2404DF38A1061AF27FFC5221CECE63EC33CBC55F88CE39B461795F8898C`.

Compilation par défaut : 679 tests réussis également ; empreinte du build `6462556F467427284577064EF93851DC10FAE1A1C33D02664F661D33F743D08E`. Cette compilation désactive les nouvelles protections expérimentales. Avertissement AutoMapper `NU1903` préexistant toujours présent et non résolu par cet incrément.

Diagnostic Mono du candidat exact réussi : six cibles de simulation et six callbacks protégés, appels clients bloqués sans monde, préfixes hôtes permissifs. Preuves : `local-artifacts/a0-candidates/7BC13CC13849BFED81C365DEC8D714959CA63A4D77DAC62C37F396870B38C979/offline-rule-dispatch/latest.json` et `latest.log`, avec `gameStarted=false`, `runtimeValidated=false`. Aucun résultat de ce diagnostic ne constitue une validation moteur Unity.

## Limites restantes

- Pas encore de création des zones absentes ni de suppression autoritaire des zones supplémentaires : le contrôle préalable continue de les refuser.
- Les constructeurs natifs ordinaires ne sont pas une fabrique de présentation sûre : ils recalculent des contextes et exécutent d'autres opérations avant les callbacks protégés.
- Orientation, forme, contexte calculé complet, durée et cycle de vie visuel restent à répliquer explicitement pour la reconstruction des zones.
- Les transformations visuelles et effets Unity n'ont pas été observés en jeu.
- Le repli doit toujours reconstruire les données mécaniques, pas simplement réactiver les contrôleurs sur les représentations partielles.

Aucun lancement du jeu, aucune activation, aucune sauvegarde originale modifiée, aucun accès en écriture à la production des voix. Le marqueur `RTWP_RUNTIME_VALIDATED.json` ne doit pas être créé pour cette vérification hors jeu.
