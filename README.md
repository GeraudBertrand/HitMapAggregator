# HitMapAggregator

Extension Twitch : les viewers cliquent sur des zones de l'écran pendant un stream, le
serveur agrège les clics en zones d'intérêt avec pourcentages, et une browser source OBS
les affiche en incrustation.

.NET 10, ASP.NET Core Minimal API, frontend d'extension en HTML/JS vanilla.

## Structure

    src/HitMapAggregator.Core           logique métier pure (agrégation, sessions), sans ASP.NET
    src/HitMapAggregator.Api            EBS : ingestion, pilotage, SSE, overlay (wwwroot/overlay)
    tests/HitMapAggregator.Tests        tests unitaires, d'intégration et de performance
    tests/HitMapAggregator.Testing      données synthétiques et JWT de test, partagés avec les outils
    tools/HitMapAggregator.Traffic      trafic synthétique : simulate (à la main) et load (NBomber)
    tools/HitMapAggregator.Visualizer   banc d'essai WinForms et export SVG
    extension/                          frontend de l'extension Twitch

Réglages communs dans `Directory.Build.props`, versions de paquets dans
`Directory.Packages.props`.

## Lancer l'API

    dotnet run --project src/HitMapAggregator.Api -c Release --urls http://localhost:5080

Overlay : `http://localhost:5080/overlay/?channel=<id>&token=<jeton>`, avec
`&mode=ellipse` ou `&mode=contour` pour forcer un rendu (sinon on suit le `renderMode`
transporté). L'URL complète s'obtient dans le panneau live_config de l'extension.

### Routes

    POST  /api/clicks                       JWT viewer · 204 · 400 · 401 · 409 collecte fermée
    GET   /api/control/state                JWT diffuseur · running + réglages (null sans session)
    POST  /api/control/start                JWT diffuseur · ouvre la collecte, démarre le tick
    POST  /api/control/stop                 JWT diffuseur · ferme la collecte, gèle sans effacer
    POST  /api/control/reset                JWT diffuseur · vide les positions
    POST  /api/control/settings             JWT diffuseur · patch JSON partiel
    GET   /api/control/overlay-token        JWT diffuseur · jeton overlay courant
    POST  /api/control/overlay-token/rotate JWT diffuseur · nouveau jeton, ancien refusé, flux ouverts coupés
    GET   /api/stream/heatmap               ?channelId=&token= · SSE, 401 si jeton invalide
    GET   /api/metrics                      en-tête X-Metrics-Key · p50/p99 du tick, débit, taille, positions

Le canal et l'`opaque_user_id` viennent toujours du JWT ; le corps d'un clic ne porte que
`{ x, y }`. `/api/control/*` exige le rôle `broadcaster` (pas les modérateurs).

`start`/`stop` pilotent la collecte à la source : un clic reçu pendant l'arrêt est refusé
en 409, et c'est le seul signal qu'un viewer reçoit du serveur.

### Secrets

Section `Secrets` :

- `TwitchExtension` : secret base64 de la console Twitch, vérifie les JWT ;
- `OverlayToken` : clé base64 à nous, signe les jetons overlay ;
- `MetricsKey` : protège `/api/metrics`.

Les deux premiers font au moins 32 octets, le troisième au moins 16 caractères, sinon
l'API refuse de démarrer. `appsettings.Development.json` contient des valeurs de
**développement** ; pour tester dans Twitch, surcharger `TwitchExtension` par le vrai
secret via `dotnet user-secrets` ou la variable `Secrets__TwitchExtension`.

### Jeton overlay

HMAC(`OverlayToken`, canal + génération). Les générations sont persistées dans
`Overlay:GenerationsPath` (défaut `data/overlay-generations.json` sous la racine du
contenu), écrites à chaque régénération, relues au démarrage. Un fichier illisible bloque
le démarrage plutôt que de remettre les générations à zéro. Suppose **une seule
instance** avec un disque persistant.

Régénérer coupe aussi les flux SSE ouverts. Limite connue : un flux validé juste avant la
rotation mais abonné juste après survit.

## Extension

Dossier `extension/` :

- `video_overlay.html` : clic viewer, positions relatives à l'image (bandes noires
  exclues), clics espacés d'au moins 250 ms, message sur 409 ;
- `live_config.html` : pilotage diffuseur et URL de l'overlay.

L'adresse de l'EBS est dans `extension/config.js` et doit figurer dans l'allowlist « URL
Fetching Domains » de la console Twitch. CORS : origines `https://*.ext-twitch.tv`, plus
localhost en Development.

## Tests

`dotnet test` ne fonctionne pas sur la machine de développement (le SDK .NET 10 n'arrive
pas à dialoguer avec l'exécutable de test). Utiliser le runner in-process de xUnit :

    dotnet run --project tests/HitMapAggregator.Tests -c Release

Options utiles : `-notrait "Category=Performance"` pour exclure les mesures,
`-showLiveOutput` pour voir les chiffres de perf. L'Explorateur de tests de Visual Studio
fonctionne normalement. Les tests de perf sont ignorés sur une build Debug.

## Trafic synthétique

    dotnet run --project tools/HitMapAggregator.Traffic -c Release -- simulate --url http://localhost:5080 --channel demo --scenario random --rate 3000 --duration 60
    dotnet run --project tools/HitMapAggregator.Traffic -c Release -- load --url http://localhost:5080 --clients 20000

`simulate` : scénarios `random` (monde aléatoire dérivant), `noise` ou les six jeux figés.
`load` : montée en charge NBomber puis verdict sur le p99 du tick (seuil 30 ms). Un canal
qui vient de démarrer paie la compilation JIT sur ses premiers ticks : mesurer sur un
serveur chaud.

Les deux signent leurs JWT avec le secret de développement (`--secret` pour en changer) ;
un JWT par viewer est signé au démarrage, d'où quelques secondes de latence. `load`
envoie la clé de métriques de dev (`--metrics-key` pour une autre).

⚠️ **NBomber est gratuit pour un usage personnel uniquement**, pas pour une organisation.
Il tire aussi MessagePack 2.5.302, signalé par NuGet pour une vulnérabilité de gravité
moyenne (GHSA-qhrr-8q5h-9q3h) — outil de développement seulement, jamais déployé.

## Visualiseur

    dotnet run --project tools/HitMapAggregator.Visualizer -c Release
    dotnet run --project tools/HitMapAggregator.Visualizer -c Release -- shot artifacts/explorer.png
    dotnet run --project tools/HitMapAggregator.Visualizer -c Release -- all artifacts/svg 20000

**Banc d'essai interactif** (sans argument). Un monde de clics tiré au hasard
(`RandomWorld`) dont les parts suivent une marche aléatoire à chaque tick. Cette dérive est
essentielle : sans elle aucun classement ne s'inverserait, donc il n'y aurait jamais rien à
détecter.

`ZoneTracker` donne une identité stable aux zones d'un tick à l'autre (appariement par
proximité des centres), d'où un graphique des parts sur 48 s, un journal des dépassements
et un flash sur les zones concernées. C'est un instrument de diagnostic : l'overlay, lui,
ne sait pas apparier les zones.

Le panneau « Ce que reçoit l'overlay » affiche la liste triée telle quelle, c'est la vue
de référence. « Couleurs par rang » colorie comme l'overlay (par position) ; décoché, on
repasse aux couleurs d'identité, utiles au diagnostic mais impossibles en production. La
barre du bas compare parts injectées et parts détectées. Le chronomètre écarte les premiers
ticks (JIT). Il pilote un vrai `ChannelSession` : chemin et temps sont ceux de la production.

`shot` produit une capture reproductible (Windows refuse de mettre au premier plan une
fenêtre lancée depuis un terminal, donc l'application se dessine elle-même). `all` écrit
un SVG par scénario synthétique, référence de non-régression.

Le projet cible `net10.0-windows` pour WinForms ; tout le reste est en `net10.0`.
