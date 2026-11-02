# NavVFR (VFRNav)

Outil de préparation de navigation VFR pour la simulation de vol (Microsoft Flight Simulator).
Application Windows en **C# / .NET 10** (WPF + [Mapsui](https://mapsui.com/)).

> **Pour la simulation uniquement.** Ce logiciel ne remplace ni l'AIP, ni les NOTAM, ni la météo officielle.
> Ne l'utilise pas pour un vol réel.

## Fonctions

- **Carte** avec terrains (OurAirports), espaces aériens (OpenAIP), balises **VOR** et **NDB/ADF**, fonds de carte au choix.
- **Route** : clic pour poser un point, glisser pour le déplacer, clic sur un point pour le retirer.
- **Log de navigation** : routes vraie et magnétique, dérive, cap à tenir, vitesse sol, temps, carburant (vent supposé constant).
- **Plan de vol `.PLN`** : la route s'enregistre et s'ouvre directement en `.pln` (le fichier de MSFS), avec les altitudes et les notes par point. Les anciennes routes `.json` s'ouvrent encore.
- **Étiquette de chaque branche** sur la carte : route magnétique et longueur (masquée quand la branche est trop courte à l'écran).
- **Altitude par point** : colonne « Alt (ft) » ou glisser-déposer dans le profil (un point ; double-clic pour revenir à la croisière), ou le trait de croisière pour toute la route.
- **Feuille de route imprimable** (aperçu, impression, PDF) : navigation, relief, profil vertical, terrains, météo en clair,
  espaces aériens traversés, balises proches, obstacles.
- **Profil du relief** sous la route (Open-Meteo) et **obstacles** (mâts, tours, cheminées, éoliennes d'OpenStreetMap).
- **METAR / TAF traduits en français**, catégorie de vol (VFR, MVFR, IFR, LIFR) et pastilles météo sur la carte.
- **Aide pour débutants** : explication des zones (type, classe, limites) au survol de leur contour.
- **Lecteur de cartes VAC** (PDF) via le projet compagnon StormATC : base d'aérodromes et cartes lues sur place, jamais copiées.
- **Suivi de l'avion en direct** dans MSFS (SimConnect).

## Compiler et lancer

Prérequis : Windows 10/11 et le SDK [.NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/VFRNav.App
```

### SimConnect (optionnel)

Le suivi de l'avion appelle la DLL native `SimConnect.dll` : aucune référence au SDK n'est nécessaire pour compiler.
Si le SDK de MSFS est installé (`C:\MSFS 2024 SDK` ou `MSFS_SIMCONNECT_PATH`), la DLL est copiée à côté de l'exécutable
à la compilation. Sinon, VFRNav la cherche au lancement, et le suivi est simplement indisponible tant qu'elle manque.

### Clés et données (optionnel)

Les clés se saisissent dans le panneau « Données » et sont enregistrées dans `%AppData%\VFRNav\settings.json`
(jamais dans le dépôt) :

| Clé | Rôle |
| --- | --- |
| OpenAIP | espaces aériens |
| CARTO | fonds de carte CARTO (le fond « Clair » Esri n'en demande pas) |
| ICAO API Data Service | NOTAM affichés dans l'application (sinon : bouton vers SOFIA-Briefing du SIA) |

Les cartes VAC (© SIA) ne sont pas incluses : elles viennent de ton propre dossier StormATC.

## Sources de données

OurAirports (domaine public) · OpenAIP · aviationweather.gov · Open-Meteo (modèle numérique de terrain, usage non commercial) ·
© contributeurs OpenStreetMap (ODbL) via Overpass · Esri / CARTO (fonds de carte) · StormATC (aérodromes, profils VFR).

## Limites connues

- Le vent est unique pour toute la route, la déclinaison magnétique est celle du terrain de départ.
- Le relief vient d'un modèle d'environ 90 m ; les obstacles d'OpenStreetMap sont incomplets (lignes électriques absentes, hauteurs parfois manquantes).
- Les textes d'aide et les seuils (marges de 1 000 ft, catégories de vol) sont indicatifs.

## Licence

Aucune licence n'est définie pour l'instant : tous droits réservés par défaut.

## Installation

Téléchargez `VFRNav-Setup-x.y.z.exe` depuis les [releases](https://github.com/feanor91/NavVFR/releases) (aucun .NET à installer), ou l'archive `.zip` portable.

Le suivi de l'avion en direct appelle directement la DLL native `SimConnect.dll`. Elle appartient à Microsoft : l'installeur GitHub ne l'embarque pas. VFRNav la cherche au lancement, dans cet ordre : à côté de `VFRNav.App.exe`, puis dans le SDK de MSFS (`MSFS_SIMCONNECT_PATH`, `MSFS_SDK`, `C:\MSFS 2024 SDK`, `C:\MSFS SDK`). Copiez-la à côté de l'exécutable si vous n'avez pas le SDK. Un installeur construit sur une machine où le SDK est installé l'inclut automatiquement.

## Publier une version

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Le workflow `release.yml` teste, publie l'application, compile l'installeur (Inno Setup) et crée la release GitHub. Le workflow `ci.yml` construit et teste chaque push.
