# MSFS Rhumb

**VFR and IFR** navigation planning tool for flight simulation (Microsoft Flight Simulator).
Windows application in **C# / .NET 10** (WPF + [Mapsui](https://mapsui.com/)). The interface is available in **French and English** (see [Language](#language)). French version of this file: [README.md](README.md).

> Former names: the application used to be called VFRNav. The GitHub repository keeps the name `NavVFR`, the projects and namespaces stay `VFRNav.*`, and user data stays in `%AppData%\VFRNav`.

> **For simulation only.** This software replaces neither the AIP, nor NOTAMs, nor official weather.
> Do not use it for a real flight.

## Language

The interface, messages, flight sheet, glossary and decoded weather are available in French and English. The language is chosen in **Settings → LANGUAGE** (applied after a restart); by default it follows the Windows display language (French if it is neither). Texts live in resource files, `src/VFRNav.Core/Localization/Strings.resx` (French, neutral language) and `Strings.en.resx` (English); in XAML they are read with `{local:Tr Key}` and in code with `Loc.Get("Key")` / `Loc.F("Key", args)`. To add a language, add a `Strings.<code>.resx` and an entry in `Loc.Languages`. A test checks that both files have the same keys and placeholders and that every key used in the code exists.

Not translated: the VFR procedure texts read from the VAC charts (`vfr-data`), which are French source data.

## What the application does

### Map, zones and help

- **Map** with airports (OurAirports), airspaces (OpenAIP, outlines with name and limits along the line), **VOR** and **NDB/ADF** beacons, a choice of base maps, VFR reporting points (yellow triangles, tick box to hide them).
- **Zones**: hovering an outline shows the card of the zone (type, class, limits, frequencies) and an explanation for beginners.
- **Glossary** of 110 abbreviations from charts, weather and the application, with search.
- **World airports** (48,000 OurAirports airports, downloaded once then cached for 7 days, "Refresh" button in Data): filters by size, scheduled service, IATA code, paved runway and minimum length, country or continent, corridor around the route. The display is spread out according to zoom (the most important first, at most 800 on screen); a **search** by ICAO/IATA code, name or city centres the map and adds the airport to the route.
- **Airports**: hover card (plain-language weather, VFR procedures), weather dots (VFR, MVFR, IFR, LIFR), METAR and TAF decoded in plain language.

### Preparing a route

The left panel has four parts: **Common** (point notes, flight plan and flight sheet calculation, simulator), **VFR** and **IFR** (each with its own route point list and Open / Save .pln buttons, its aircraft, its alternate airports) and **Settings** (conditions, base map, navaids, filters, keys, data and all the updates: VAC charts, SIA, SIDs/STARs and ATS routes read from MSFS). The route is the same in both lists, with the same selection.

- **Alternate airports** (VFR and IFR): ICAO diversion codes entered in each panel; distance, heading, time and fuel from the last route point, with the total route + farthest alternate fuel compared to the aircraft; repeated on the flight sheet.
- **Route**: click to place a point, drag to move it, click a point to remove it. Each leg is labelled on the map (magnetic course and length) and each point has its name under its numbered circle, including SID and STAR points; a name disappears when the map is too zoomed out for it to stay readable.
- **Nav log**: true and magnetic courses, drift, heading, ground speed, time, fuel (wind assumed constant).
- **Altitude per point**: "Alt (ft)" column, or drag and drop in the profile (one point; double-click to return to cruise) or from the cruise line for the whole route.
- **Briefing for each point**: zones crossed at the point's altitude, what each implies (overflight, radio, clearance) and frequencies to contact.
- **Vertical profile** (airports are on the ground; points without an altitude climb and descend at the aircraft's rate, from its climb and descent rates, optional in its record): terrain (Open-Meteo), zones, obstacles (masts, towers, chimneys, wind turbines from OpenStreetMap), entered altitudes. **Zoom**: wheel on distance, Ctrl + wheel on altitude, drag the background to pan, double-click the background to see everything.
- **Notes** per route point, **live info** in a separate window.
- **`.pln` flight plan**: the route is saved and opened directly in the MSFS file format, with altitudes and notes per point.
- **Printable flight sheet** (preview, print, PDF): navigation, briefing per point, terrain, profile, airports, weather, zones, navaids, obstacles.
- **Aircraft**: editable list (name, VFR and/or IFR, cruise and maximum speeds, fuel flow, usable fuel, ceiling), one choice per navigation type. The log warns when fuel exceeds capacity; two range circles are drawn around the departure point: the usable range (all fuel minus the aircraft's reserve, 30 min for VFR and 45 min for IFR by default) and the range with all fuel, without wind. They are only drawn if they cross the displayed area, and their labels follow the view. When a circle encloses a pole (large airliners), the out-of-range zone is greyed and the reserve shown in light orange instead of a line. The log and route check report when the route eats into the aircraft's reserve (or exceeds its tank), and route legs are drawn on the great circle, as distance and heading compute them.

### VAC charts and VFR procedures

- **Aerodrome database** shipped with the application (`vfr-data`: national list and about 425 VFR profiles with position, elevation, frequencies, runways), no other software to install.
- **VAC chart viewer** (PDF): the SIA charts are the pilot's own (`AD-2.LFxx.pdf` files, folder set in the Data panel, otherwise `%AppData%\VFRNav\Cartes VAC`). They are read in place, never copied or shipped. **Update**: the "Update the VAC charts (SIA)" button in the Data panel downloads, on request, the charts of the current AIRAC cycle from the SIA website; a chart is only replaced if it differs from yours, and the button can also fetch missing ones (one at a time, a few minutes). A second click stops it.
- **VFR procedures** (reporting points, circuit, arrivals and departures, notes) read from the VFR profiles in `vfr-data`, in French as they provide them. A profile placed in `%AppData%\VFRNavfr-profiles` replaces the shipped one.

### IFR route

- **Route calculation**: departure, arrival, flight level, SID, STAR and runways of your choice; shortest path (A*) over ATS routes; free route from FL195, only between points of countries where it exists (indicative European list). SID and STAR lists show the exit point ("AGOP6H → AGOPA") or entry point ("NISA7R ← NISAR"); "None" connects the airport directly to the network, without a procedure.
- **France**: ATS routes from the free SIA AIXM 4.5 export ("Load the AIRAC data" button). This export does not contain SIDs/STARs.
- **World, read from MSFS** (SimConnect), once, then usable **without the simulator**:
  - the **SIDs and STARs** of all airports (about 2,800 airports, 59,000 procedures, 4 MB, about 75 s to read);
  - the **ATS routes outside France** (about 91,000 segments on 9,600 routes and 28,000 airports, 2.4 MB, a few minutes to read), explored step by step from the SID/STAR points. France stays the SIA's.
- **Manual update**: run the read buttons again when MSFS has updated its data. The window compares the saved version with the simulator's; only changed airports are rewritten. A second click stops the reading.
- **Recommended runway** from the METAR wind (fallback: the form's assumed wind): it is selected, and the SID and STAR lists only show those serving it. "Auto" takes the shortest path across all runways.
- Outside France, points are identified by their name and region, so as not to confuse two points of the same name in two countries.

### With MSFS

- **Real-time weather on the map** (Settings panel): precipitation radar (RainViewer, a frame every 10 min) and Meteosat (EUMETSAT) satellite clouds in two layers to show separately or together: cloud cover (low stratus included) and cold tops (thunderstorms, fronts), each with its opacity, updated automatically. The radar can be animated over its last 4 frames (30 min). No key. Real weather: it may differ from MSFS.
- **SIGMET on the map** (Settings panel): the hazardous-phenomena areas in force (thunderstorms, turbulence, icing, mountain waves, volcanic ash, cyclones), one colour per phenomenon, with a hover tooltip (levels, validity, message). A thick solid outline marks those crossing the route, and a line appears in the route check. "Only at my altitude" option.
- **Indicative ILS final** (IFR route, France): when the arrival runway has an ILS in the SIA export, the route receives the localizer interception at 10 NM then the final at 5 NM on the ILS glide path, with their altitudes (descent profile) and a note (indicative, frequency, QDM, slope, category) repeated on the flight sheet. This is not the published approach procedure: its points, altitudes and minima are not in the SIA data.
- **SIGMET on the flight sheet**: the SIGMET messages in force (aviationweather.gov) crossing the route are listed with their phenomenon, levels, validity and position relative to your planned altitude.
- **Live aircraft tracking** (SimConnect): an aircraft drawn on the map, following the heading.

## Where the data is

| What | Where |
| --- | --- |
| Settings, keys, route (never in the repository) | `%AppData%\VFRNav\settings.json` |
| World SIDs/STARs | `%AppData%\VFRNav\procedures\`: one `.json.gz` file per airport and an `index.json` |
| ATS routes outside France | `%AppData%\VFRNav\network\world-network.bin.gz` |
| World airports (OurAirports) | `%AppData%\VFRNav\airports\` |
| SIA data | your own `export_xml_bd_SIA_….zip` file: MSFS Rhumb only keeps the path |

Data read from MSFS comes from its navigation database: it is **not in the repository** (`data/` is ignored) nor in the releases. MSFS Rhumb also looks for it next to its executable (`data\procedures`, `data\network`): an installer built on a machine that has them takes them along. On another PC, copy the folders above or run the readings again, with MSFS running. The SIA file and VAC charts (© SIA) are licensed and not included.

## Install

Download `MSFS-Rhumb-Setup-x.y.z.exe` from the [releases](https://github.com/feanor91/NavVFR/releases) (no .NET to install), or the portable `.zip` archive. The version number appears in the title bar.

`SimConnect.dll` belongs to Microsoft: the GitHub installer does not ship it. MSFS Rhumb looks for it at launch, in this order: next to `MSFSRhumb.exe`, then in the MSFS SDK (`MSFS_SIMCONNECT_PATH`, `MSFS_SDK`, `C:\MSFS 2024 SDK`, `C:\MSFS SDK`). Copy the DLL next to the executable if the SDK is not installed: without it, neither aircraft tracking nor the MSFS readings work.

### Keys (optional)

Keys are entered in the "Data" panel.

| Key | Purpose |
| --- | --- |
| OpenAIP | airspaces |
| CARTO | CARTO base maps (the Esri "Light" base map needs none) |
| ICAO API Data Service | NOTAMs shown in the application (otherwise: button to the SIA's SOFIA-Briefing) |

## Build and run

Requirements: Windows 10/11 and the [.NET 10](https://dotnet.microsoft.com/download) SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/VFRNav.App
```

If the MSFS SDK is installed (`C:\MSFS 2024 SDK` or `MSFS_SIMCONNECT_PATH`), `SimConnect.dll` is copied next to the executable at build time. No reference to the SDK is needed to build.

## Publish a version

```bash
git tag v0.12.0 && git push origin v0.12.0
```

The `release.yml` workflow tests, publishes the application, builds the installer (Inno Setup) and creates the GitHub release; the tag number becomes the displayed version. The `ci.yml` workflow builds and tests every push. Before placing a tag, check that the last push went through: a tag on a commit missing from `main` publishes a version without other people's work.

## Data sources

OurAirports (public domain) · OpenAIP · aviationweather.gov · Open-Meteo (digital elevation model, non-commercial use) · © OpenStreetMap contributors (ODbL) via Overpass · Esri / CARTO (base maps) · SIA (ATS routes of France, AIXM 4.5, licensed) · Microsoft Flight Simulator (world SIDs/STARs and routes, via SimConnect) · VFR profiles taken from the VAC charts (© SIA).

## Known limitations

- **Wind and variation**: the nav log wind is a single value for the whole route, the variation is that of the departure airport.
- **Fuel**: calculated with the speed and fuel flow of the chosen aircraft, without taxi, climb, circuits or approach. The reserve is indicative.
- **ATS routes outside France**: one-way directions and levels are those given by MSFS (treated as two-way; low routes limited to FL180), to be checked. Not compared with real charts. Frequencies and names of VORs/NDBs outside France are not read.
- **Free route**: above FL195 it is only allowed between points of the European country list in the program (`IfrPlanOptions.DefaultFreeRouteRegions`), indicative: real entry airspaces and levels must be checked.
- **MSFS SIDs/STARs**: their AIRAC cycle is the simulator's, which may differ from the SIA's; many STARs are not linked to a runway.
- **Recommended runway**: the METAR gives the wind in true north; magnetic variation is only known for France (VFR profiles). No runway length, minima or noise restrictions.
- **Terrain and obstacles**: model of about 90 m; OpenStreetMap obstacles are incomplete (power lines missing, heights sometimes missing).
- Help texts, the glossary and thresholds (1,000 ft margins, flight categories) are indicative.

## Licence

No licence is defined for now: all rights reserved by default.
