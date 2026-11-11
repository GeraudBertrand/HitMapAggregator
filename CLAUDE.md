# HitMapAggregator

## Le projet

Extension Twitch permettant aux viewers de cliquer sur des zones de l'écran pendant un
stream. Les clics sont agrégés côté serveur en zones d'intérêt avec pourcentages,
affichées dans une browser source OBS que le streamer incruste dans son flux.

**Stack** : .NET 10, C#, ASP.NET Core Minimal API. Frontend d'extension en HTML/JS vanilla.

**Charge visée** : 5 000 à 10 000 viewers en nominal, scalabilité prévue jusqu'à 50 000.

## Décisions d'architecture — NON NÉGOCIABLES

Ces choix ont été pris après analyse. Ne les remets pas en question, ne propose pas
d'alternative, ne les « améliore » pas.

1. **Ingestion viewer : HTTP POST stateless.** Pas de SignalR, pas de WebSocket. Les
   viewers n'envoient que des positions, ils ne reçoivent rien.
2. **Diffusion overlay : SSE** via `TypedResults.ServerSentEvents` (.NET 10 natif). Un
   seul destinataire par canal.
3. **Découplage strict ingestion / diffusion.** Ne jamais broadcaster à chaque clic. Un
   timer périodique (200 ms) produit un snapshot.
4. **Stockage : positions normalisées brutes** (x, y ∈ [0,1]), jamais les cellules
   calculées. C'est ce qui rend les changements de réglage rétroactifs.
5. **Déduplication par `opaque_user_id`**, dernier clic gagne. Upsert, pas incrément.
   Pas d'identité liée Twitch nécessaire.
6. **Viewers anonymes filtrés à l'agrégation, pas à l'ingestion.** Le toggle doit être
   réversible sans perte de données.
7. **Grille de densité 128×72**, quantifiée en octets + base64 pour le transport.
8. **Deux rendus calculés côté serveur** : ellipse de covariance ET champ de densité. Le
   choix d'affichage est un drapeau transporté, le serveur ne branche jamais dessus.
9. **Une boucle async par canal** (`PeriodicTimer` + `await`), pas un thread par canal.
10. **`Core` ne référence rien d'ASP.NET.** Logique métier pure, testable sans serveur.

## Contraintes techniques précises

- **Flou séparable à somme glissante**, jamais de convolution naïve — le coût doit être
  indépendant du rayon.
- **Hacher l'`opaque_user_id` en `ulong`** dès l'ingestion (xxHash ou équivalent). Pas de
  `Dictionary<string, _>`.
- **`readonly record struct`** pour les positions. Zéro allocation par point.
- **Pooler les buffers** du champ de densité : deux buffers pré-alloués par
  `ChannelSession`, réutilisés à chaque tick. Zéro allocation par tick.
- **`JsonWebTokenHandler`** (`Microsoft.IdentityModel.JsonWebTokens`), jamais l'ancien
  `JwtSecurityTokenHandler`.
- **Ne jamais logger l'URL complète** de l'overlay (elle contient un token secret).
- **Aucune donnée client n'est digne de confiance** : `channel_id` et `role` sont toujours
  extraits du JWT validé côté serveur, jamais du corps de la requête.

## Priorités

**Fonctionnel et stable avant tout.** La qualité visuelle du rendu sera revue plus tard
par le propriétaire du projet — ne passe pas de temps sur l'esthétique, les animations,
les palettes ou le polish CSS. Un rendu correct et lisible suffit.

En cas d'arbitrage : **correction > testabilité > performance > élégance > apparence**.

## Style de collaboration attendu

- **Explique tes choix d'implémentation** au fil de l'eau, en une ou deux phrases. Le
  propriétaire du projet veut comprendre le code, pas seulement le recevoir.
- **Signale les décisions ambiguës** au lieu de trancher seul. Si une spec est incomplète,
  demande.
- **Ne code pas au-delà de l'étape en cours.** Pas d'anticipation, pas de « j'ai aussi
  ajouté ».
- Pas de commentaires décoratifs. Commente le **pourquoi** quand ce n'est pas évident,
  jamais le **quoi**.

## Protocole d'étapes

Chaque étape se termine par un **arrêt obligatoire** : tests verts, rapport de fin
d'étape, puis attente d'un « OK, passe à l'étape N+1 » explicite.

1. Noyau métier (`Core` + `Tests`) — **terminée**
2. API, SSE, overlay, simulateur — sans Twitch — **terminée**
3. Intégration Twitch : JWT, tokens overlay, extension — **terminée** (validée le 2026-11-11)
4. Regroupement et nettoyage — **terminée** (validée le 2026-11-11)
5. Phase de mesure : banc `bench` (Traffic), page en direct et rapport — **terminée**
   (validée le 2026-11-11).
6. **Test de l'intégration dans le vrai Twitch** (Local Test, puis hosted test).

Si un test échoue : le signaler et le corriger. **Jamais** désactiver un test ni assouplir
une assertion pour la faire passer.

---

# Conventions établies (étape 1)

## Espace corrigé de l'aspect

Les positions sont normalisées dans [0,1]², donc une unité en X et une unité en Y n'ont
pas la même taille physique sur un écran 16:9. Toutes les **distances** et la **covariance
des zones** se mesurent dans l'espace corrigé : `(x, y * Grid.AspectY)` avec
`AspectY = 72/128 = 0,5625`. Voir `Grid.cs`.

Conséquences à connaître :

- `Zone.SigmaA` et `Zone.SigmaB` sont dans la **même échelle**, en fraction de la
  **largeur** d'écran. Un rendu correct multiplie les **deux** par la largeur du canevas.
  Multiplier SigmaB par la hauteur déformerait une ellipse pivotée.
- `Zone.Angle` est en radians dans `[0, π)`, mesuré dans cet espace avec Y vers le bas
  comme à l'écran — il se reporte tel quel dans un `rotate()` SVG ou Canvas.
- `Zone.Center` reste en coordonnées **normalisées**, prêt pour le placement.
- Une cellule de grille est carrée dans cet espace, donc un rayon de flou exprimé en
  cellules est isotrope à l'écran.

## Réglages

`Threshold` est le seuil de détection de pic, en fraction du maximum du champ.
`BandCount` sert uniquement au rendu contour côté client : le serveur le transporte sans
s'en servir. `AssignmentRadius` est `null` par défaut et reprend alors
`PeakMergeDistance` ; il reste surchargeable.

**Le critère qui décide de l'existence d'une zone est `MinZonePercentage`**, pas un
plafond de comptage. `MaxZones` (12 par défaut) est une borne de lisibilité qui ne mord
que si le vote est très fragmenté. `PeakDetector.HardCeiling` (32) est une borne
*structurelle*, jamais un réglage produit : elle dimensionne à la compilation les
`stackalloc` des accumulateurs de covariance.

## Contrat envers l'overlay

`HeatmapSnapshot.Zones` est **une liste déjà triée par effectif décroissant**. L'indice 0
est la zone majeure, la tranche `[0, MajorZones)` est le groupe de tête. C'est le contrat,
pas un détail d'implémentation — un test le vérifie sur les six scénarios.

Le consommateur est supposé **sans mémoire** : il lit un classement, il ne retient rien
d'un tick à l'autre. Corollaire assumé, **les zones ne portent aucun identifiant stable**.
Si le classement s'inverse, l'indice 0 désigne une autre zone, et c'est voulu : on
transporte un classement, pas des individus.

Conséquence pratique : ne jamais concevoir une fonctionnalité qui supposerait que
l'overlay sache apparier les zones entre deux instantanés. Il ne le peut pas. Le banc
d'essai, lui, le fait (`ZoneTracker`) — mais c'est un instrument de diagnostic, jamais un
aperçu du produit.

### Contrat JSON de l'étape 2 (amendé)

⚠️ **Le champ `id` des zones est supprimé** par rapport à la spec d'origine. Il ne pouvait
être qu'une étiquette de rang redondante avec l'indice du tableau, ou la promesse d'une
identité stable qu'on a justement décidé de ne pas fournir. Un champ fantôme dont
quelqu'un finirait par dépendre à tort.

```json
{
  "renderMode": "both",
  "majorZones": 3,
  "bandCount": 6,
  "totalVotes": 1847,
  "scatteredPct": 8.2,
  "grid": { "w": 128, "h": 72, "max": 12.4, "data": "<base64>" },
  "zones": [
    { "cx": 0.27, "cy": 0.34, "pct": 41.2,
      "sa": 0.038, "sb": 0.031, "angle": 0.42 }
  ]
}
```

`majorZones` et `bandCount` s'ajoutent parce que ce sont des drapeaux d'affichage que le
serveur transporte sans les interpréter, au même titre que `renderMode` — l'overlay en a
besoin pour savoir combien de zones souligner et combien de bandes de niveau tracer.
L'ordre du tableau `zones` porte tout le sens : première ligne = zone majeure.

## Ce qui limite vraiment le nombre de zones

Ce n'est pas le plafond, c'est le **rayon de lissage**. Contraste du champ (max/moyenne)
mesuré sur N zones égales :

| Zones | r=1 | r=2 | r=3 | r=4 |
|---|---|---|---|---|
| 10 | 22,6 | 12,0 | 7,0 | 4,5 |
| 20 | 11,4 | 6,1 | 3,5 | 2,3 |
| 40 | 5,9 | 3,1 | 1,8 | 1,5 |

Au rayon par défaut de 4, le champ cesse de résoudre les zones vers 15-20. Pour un usage
« carte à nombreuses cibles », baisser conjointement `SmoothingRadius` et
`PeakMergeDistance`. Voir le test `Twenty_zones_are_limited_by_the_smoothing_radius_not_by_the_cap`.

⚠️ **`MinFieldContrast` (3 par défaut) est un compromis, pas une garantie.** Le contraste
du bruit uniforme dépend du nombre de clics (5,2 à 500 clics et r=1 ; 1,3 à 50 000 clics)
et celui d'un vrai champ chute avec le nombre de zones. Les deux plages se recouvrent :
aucun seuil fixe ne les sépare partout. À remplacer par un test de significativité
statistique si le besoin s'en fait sentir.

## Flou

4 passes de boîte **par axe** (8 au total). Les poids de dispersion
(`FillScatterWeights`) rendent le flou exactement conservatif en masse, bords compris.

---

# Usage et structure

Commandes, routes, secrets, extension et outils : voir **README.md**. Ce fichier ne garde
que ce qui guide le travail sur le code.

Disposition : `src/` (Core, Api), `tests/` (Tests, et Testing pour les données
synthétiques et JWT partagés avec les outils), `tools/` (Traffic, Visualizer),
`extension/`. Réglages communs dans `Directory.Build.props`, versions de paquets
uniquement dans `Directory.Packages.props` — jamais de `Version=` dans un `.csproj`.

Lancer les tests : `dotnet run --project tests/HitMapAggregator.Tests -c Release`.
`dotnet test` est **cassé sur cette machine** (le SDK .NET 10 ne dialogue pas avec
l'exécutable de test, reproduit sur un template vierge) : ne pas perdre de temps dessus.
Les tests de perf sont sautés en Debug, par conception.

# Pièges à connaître

- **Le canal et le rôle viennent du JWT**, jamais d'un paramètre ou du corps. Les routes
  `/api/control/*` n'ont aucun paramètre de canal, et doivent rester ainsi.
- **La non-journalisation de l'URL de l'overlay** repose sur `Microsoft.AspNetCore:
  Warning` dans les appsettings. Le test `The_overlay_token_never_reaches_the_logs`
  échoue si on l'abaisse : ne pas l'abaisser, ne pas assouplir le test.
- **Les générations de jetons overlay** sont un état persisté sur disque
  (`data/overlay-generations.json`). Un fichier illisible bloque le démarrage, exprès.
  Une seule instance supposée : à revoir avant de passer à plusieurs instances.
- **Fermer un flux SSE** se fait en complétant le canal de l'abonné
  (`SnapshotBroadcaster.Disconnect`), pas par annulation : une annulation remonterait en
  erreur sur une connexion encore vivante.
- **`HitMapAggregator.Testing` contient `Scenario`**, qui entre en conflit avec le
  `Scenario` de NBomber : `Load.cs` utilise donc des noms qualifiés.
- **L'iframe d'extension est sandboxée** : pas de `confirm()`/`alert()`, et le
  presse-papiers peut être refusé.

# Décisions produit datées

- 2026-11-10 : `/api/control/*` réservé au diffuseur, pas aux modérateurs.
- 2026-11-11 : `stop` publie un dernier instantané (tick final joué par la boucle du canal) : la carte gelée montre tous les clics acceptés.
- 2026-11-11 : jeton overlay HMAC + génération persistée en JSON ; régénérer coupe les
  flux ouverts ; `/api/metrics` protégé par clé ; clics espacés de 250 ms côté extension
  seulement (confort, pas sécurité).
