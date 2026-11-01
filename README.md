# NavVFR (VFRNav)

Outil de préparation de navigation VFR pour la simulation de vol (Microsoft Flight Simulator).
Application Windows en **C# / .NET 10** (WPF + [Mapsui](https://mapsui.com/)).

> **Pour la simulation uniquement.** Ce logiciel ne remplace ni l'AIP, ni les NOTAM, ni la météo officielle.
> Ne l'utilise pas pour un vol réel.

## Fonctions

- **Carte** avec terrains (OurAirports), espaces aériens (OpenAIP), balises **VOR** et **NDB/ADF**, fonds de carte au choix.
- **Route** : clic pour poser un point, glisser pour le déplacer, clic sur un point pour le retirer.
- **Log de navigation** : routes vraie et magnétique, dérive, cap à tenir, vitesse sol, temps, carburant (vent supposé constant).
- **Export `.PLN`** pour MSFS.
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

Les DLL du SDK MSFS ne sont pas redistribuées. Pour activer le suivi de l'avion, indique le chemin de
`Microsoft.FlightSimulator.SimConnect.dll` avant de compiler :

```bash
set MSFS_SIMCONNECT_PATH=C:\MSFS 2024 SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll
```

Sans le SDK, l'application compile et fonctionne ; seul le suivi de l'avion est indisponible.

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
