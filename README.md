# MSFS Rhumb

Outil de préparation de navigation **VFR et IFR** pour la simulation de vol (Microsoft Flight Simulator).
Application Windows en **C# / .NET 10** (WPF + [Mapsui](https://mapsui.com/)).

> Anciens noms : l'application s'appelait VFRNav. Le dépôt GitHub garde le nom `NavVFR`, les projets et les espaces de noms `VFRNav.*`, et les données de l'utilisateur restent dans `%AppData%\VFRNav`.

> **Pour la simulation uniquement.** Ce logiciel ne remplace ni l'AIP, ni les NOTAM, ni la météo officielle.
> Ne l'utilise pas pour un vol réel.

## Ce que fait l'application

### Carte, zones et aide

- **Carte** avec terrains (OurAirports), espaces aériens (OpenAIP, contours avec nom et limites le long du trait), balises **VOR** et **NDB/ADF**, fonds de carte au choix, points de report VFR (triangles jaunes, case à cocher pour les masquer).
- **Zones** : au survol d'un contour, la fiche de la zone concernée (type, classe, limites, fréquences) et une explication pour débutants.
- **Glossaire** de 110 sigles des cartes, de la météo et de l'application, avec recherche.
- **Terrains** : fiche au survol (météo en clair, procédures VFR), pastilles météo (VFR, MVFR, IFR, LIFR), METAR et TAF traduits en français.

### Préparer une route

Le panneau de gauche a quatre parties : **Commun** (notes du point, calcul du plan de vol et de la feuille de route, simulateur), **VFR** et **IFR** (chacun avec sa liste de points de route et ses boutons Ouvrir / Enregistrer .pln, son appareil, ses aéroports de dégagement) et **Paramètres** (conditions, fond de carte, balises, filtres, clés, données et toutes les mises à jour : cartes VAC, SIA, SID/STAR et routes ATS lues dans MSFS). La route est la même dans les deux listes, avec la même sélection.

- **Aéroports de dégagement** (VFR et IFR) : codes OACI de détournement saisis dans chaque panneau ; distance, cap, temps et carburant depuis le dernier point de la route, avec le carburant total route + dégagement le plus lointain comparé à l'appareil ; repris sur la feuille de route.

- **Route** : clic pour poser un point, glisser pour le déplacer, clic sur un point pour le retirer. Étiquette de chaque branche sur la carte (route magnétique et longueur) et nom de chaque point sous son cercle numéroté, y compris les points des SID et des STAR ; un nom disparaît quand la carte est trop éloignée pour qu'il reste lisible.
- **Log de navigation** : routes vraie et magnétique, dérive, cap, vitesse sol, temps, carburant (vent supposé constant).
- **Altitude par point** : colonne « Alt (ft) », ou glisser-déposer dans le profil (un point ; double-clic pour revenir à la croisière) ou du trait de croisière pour toute la route.
- **Briefing de chaque point** : zones traversées à l'altitude du point, ce que chacune implique (survol, radio, clairance) et fréquences à contacter.
- **Profil vertical** (les terrains y sont au sol ; les points sans altitude montent et descendent à la pente de l'appareil, d'après ses taux de montée et de descente, facultatifs dans la fiche) : relief (Open-Meteo), zones, obstacles (mâts, tours, cheminées, éoliennes d'OpenStreetMap), altitudes saisies. **Zoom** : molette sur la distance, Ctrl + molette sur l'altitude, glisser le fond pour se déplacer, double-clic sur le fond pour tout revoir.
- **Notes** par point de route, **infos en direct** dans une fenêtre séparée.
- **Plan de vol `.pln`** : la route s'enregistre et s'ouvre directement dans le fichier de MSFS, avec les altitudes et les notes par point.
- **Feuille de route imprimable** (aperçu, impression, PDF) : navigation, briefing par point, relief, profil, terrains, météo, zones, balises, obstacles.
- **Appareils** : liste modifiable (nom, VFR et/ou IFR, vitesses de croisière et maximale, consommation, carburant utilisable, plafond), un choix par type de navigation. Le log prévient quand le carburant dépasse la capacité ; deux cercles de portée sont tracés autour du point de départ : la portée utile (tout le carburant moins la réserve de l'appareil, 30 min en VFR et 45 min en IFR par défaut) et la portée avec tout le carburant, sans vent. Ils ne sont dessinés que s'ils traversent la zone affichée, et leurs étiquettes suivent la vue. Quand un cercle enferme un pôle (grands porteurs), la zone hors de portée est grisée et la réserve en orange clair au lieu d'un trait. Le log et la vérification de la route signalent quand la route entame la réserve de l'appareil (ou dépasse son réservoir), et les branches de la route sont tracées sur le grand cercle, comme le calculent distance et cap.

### Cartes VAC et procédures VFR

- **Base d'aérodromes** livrée avec l'application (`vfr-data` : liste nationale et environ 425 profils VFR avec position, altitude, fréquences, pistes), sans autre logiciel à installer.
- **Lecteur de cartes VAC** (PDF) : les cartes du SIA sont celles du pilote (fichiers `AD-2.LFxx.pdf`, dossier réglé dans le panneau Données, sinon `%AppData%\VFRNav\Cartes VAC`). Elles sont lues sur place, jamais copiées ni livrées. **Mise à jour** : le bouton « Mettre à jour les cartes VAC (SIA) » du panneau Données télécharge, à ta demande, les cartes du cycle AIRAC en vigueur depuis le site du SIA ; une carte n'est remplacée que si elle diffère de la tienne, et le bouton peut aussi récupérer celles qui manquent (une à la fois, quelques minutes). Un second clic arrête.
- **Procédures VFR** (points de report, circuit, arrivées et départs, notes) lues dans les profils VFR de `vfr-data`, en français quand ils le fournissent. Un profil placé dans `%AppData%\VFRNavfr-profiles` remplace celui livré.

### Route IFR

- **Calcul de route** : départ, arrivée, niveau de vol, SID, STAR et pistes au choix ; plus court chemin (A*) sur les routes ATS ; route libre à partir du FL195, seulement entre points de pays où elle existe (liste européenne indicative). Les listes de SID et de STAR indiquent le point de sortie (« AGOP6H → AGOPA ») ou d'entrée (« NISA7R ← NISAR ») ; « Aucune » raccorde directement le terrain au réseau, sans procédure.
- **France** : routes ATS de l'export AIXM 4.5 gratuit du SIA (bouton « Charger les données AIRAC »). Cet export ne contient pas les SID/STAR.
- **Monde, lu dans MSFS** (SimConnect), une fois, puis utilisable **sans simulateur** :
  - les **SID et STAR** de tous les terrains (environ 2 800 terrains, 59 000 procédures, 4 Mo, lecture d'environ 75 s) ;
  - les **routes ATS hors de France** (environ 91 000 tronçons sur 9 600 routes et 28 000 terrains, 2,4 Mo, lecture de quelques minutes), explorées de proche en proche à partir des points des SID/STAR. La France reste celle du SIA.
- **Mise à jour manuelle** : relancer les boutons de lecture quand MSFS a mis à jour ses données. La fenêtre compare la version enregistrée à celle du simulateur ; seuls les terrains modifiés sont réécrits. Un second clic arrête la lecture.
- **Piste conseillée** d'après le vent du METAR (repli : le vent supposé du formulaire) : elle est sélectionnée, et les listes de SID et de STAR ne montrent que celles qui la desservent. « Auto » reprend le trajet le plus court toutes pistes confondues.
- Hors de France, les points sont repérés par leur nom et leur région, pour ne pas confondre deux points de même nom dans deux pays.

### Avec MSFS

- **Météo en temps réel sur la carte** (panneau Paramètres) : radar de précipitations (RainViewer, image toutes les 10 min) et nuages du satellite Meteosat (EUMETSAT) en deux couches à afficher séparément ou ensemble : la couverture nuageuse (stratus bas compris) et les sommets froids (orages, fronts), chacun avec son opacité, mis à jour automatiquement. Le radar peut être animé sur ses 4 dernières images (30 min). Sans clé. Météo réelle : elle peut différer de celle de MSFS.
- **SIGMET sur la carte** (panneau Paramètres) : les zones de phénomènes dangereux en vigueur (orages, turbulence, givrage, ondes orographiques, cendres volcaniques, cyclones), une couleur par phénomène, avec infobulle au survol (niveaux, validité, message). Un contour plein et épais signale ceux qui traversent la route, et une ligne apparaît dans la vérification de la route. Option « seulement à mon altitude ».
- **Finale ILS indicative** (route IFR, France) : quand la piste d'arrivée a un ILS dans l'export du SIA, la route reçoit l'interception de l'axe à 10 NM puis la finale à 5 NM sur la pente de l'ILS, avec leurs altitudes (profil de descente) et une note (indicatif, fréquence, QDM, pente, catégorie) reprise sur la feuille de route. Ce n'est pas la procédure d'approche publiée : ses points, altitudes et minima ne sont pas dans les données du SIA.
- **SIGMET sur la feuille de route** : les messages SIGMET en vigueur (aviationweather.gov) qui traversent la route sont listés avec leur phénomène, leurs niveaux, leur validité et leur position par rapport à ton altitude prévue.
- **Suivi de l'avion en direct** (SimConnect) : un avion dessiné sur la carte, qui suit le cap.

## Où sont les données

| Quoi | Où |
| --- | --- |
| Réglages, clés, route (jamais dans le dépôt) | `%AppData%\VFRNav\settings.json` |
| SID/STAR du monde | `%AppData%\VFRNav\procedures\` : un fichier `.json.gz` par terrain et un `index.json` |
| Routes ATS hors de France | `%AppData%\VFRNav\network\world-network.bin.gz` |
| Données du SIA | ton propre fichier `export_xml_bd_SIA_….zip` : MSFS Rhumb n'en garde que le chemin |

Les données lues dans MSFS viennent de sa base de navigation : elles ne sont **pas dans le dépôt** (`data/` est ignoré) ni dans les releases. MSFS Rhumb les cherche aussi à côté de son exécutable (`data\procedures`, `data\network`) : un installeur construit sur une machine qui les a les emporte. Sur un autre PC, copier les dossiers ci-dessus ou relancer les lectures, MSFS lancé. Le fichier du SIA et les cartes VAC (© SIA) sont sous licence et ne sont pas inclus.

## Installer

Télécharger `MSFS-Rhumb-Setup-x.y.z.exe` depuis les [releases](https://github.com/feanor91/NavVFR/releases) (aucun .NET à installer), ou l'archive `.zip` portable. Le numéro de version s'affiche dans la barre de titre.

`SimConnect.dll` appartient à Microsoft : l'installeur GitHub ne l'embarque pas. MSFS Rhumb la cherche au lancement, dans cet ordre : à côté de `MSFSRhumb.exe`, puis dans le SDK de MSFS (`MSFS_SIMCONNECT_PATH`, `MSFS_SDK`, `C:\MSFS 2024 SDK`, `C:\MSFS SDK`). Copier la DLL à côté de l'exécutable si le SDK n'est pas installé : sans elle, ni le suivi de l'avion ni les lectures dans MSFS ne fonctionnent.

### Clés (optionnel)

Les clés se saisissent dans le panneau « Données ».

| Clé | Rôle |
| --- | --- |
| OpenAIP | espaces aériens |
| CARTO | fonds de carte CARTO (le fond « Clair » Esri n'en demande pas) |
| ICAO API Data Service | NOTAM affichés dans l'application (sinon : bouton vers SOFIA-Briefing du SIA) |

## Compiler et lancer

Prérequis : Windows 10/11 et le SDK [.NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/VFRNav.App
```

Si le SDK de MSFS est installé (`C:\MSFS 2024 SDK` ou `MSFS_SIMCONNECT_PATH`), `SimConnect.dll` est copiée à côté de l'exécutable à la compilation. Aucune référence au SDK n'est nécessaire pour compiler.

## Publier une version

```bash
git tag v0.12.0 && git push origin v0.12.0
```

Le workflow `release.yml` teste, publie l'application, compile l'installeur (Inno Setup) et crée la release GitHub ; le numéro du tag devient la version affichée. Le workflow `ci.yml` construit et teste chaque push. Avant de poser un tag, vérifier que le dernier push est bien passé : un tag sur un commit absent de `main` publie une version sans le travail des autres.

## Sources de données

OurAirports (domaine public) · OpenAIP · aviationweather.gov · Open-Meteo (modèle numérique de terrain, usage non commercial) · © contributeurs OpenStreetMap (ODbL) via Overpass · Esri / CARTO (fonds de carte) · SIA (routes ATS de France, AIXM 4.5, sous licence) · Microsoft Flight Simulator (SID/STAR et routes du monde, par SimConnect) · profils VFR relevés sur les cartes VAC (© SIA).

## Limites connues

- **Vent et déclinaison** : le vent du log de navigation est unique pour toute la route, la déclinaison est celle du terrain de départ.
- **Carburant** : calculé avec la vitesse et la consommation de l'appareil choisi, sans roulage, montée, tours de piste ni approche. La réserve est indicative.
- **Routes ATS hors de France** : sens uniques et niveaux sont ceux que donne MSFS (traitées dans les deux sens ; routes basses limitées au FL180), à vérifier. Non comparées à des cartes réelles. Les fréquences et noms des VOR/NDB hors de France ne sont pas lus.
- **Route libre** : au-dessus du FL195, elle n'est permise qu'entre points de la liste de pays européens du programme (`IfrPlanOptions.DefaultFreeRouteRegions`), indicative : espaces et niveaux d'entrée réels à vérifier.
- **SID/STAR de MSFS** : leur cycle AIRAC est celui du simulateur, qui peut différer du SIA ; beaucoup de STAR ne sont pas liées à une piste.
- **Piste conseillée** : le METAR donne le vent en nord vrai ; la variation magnétique n'est connue que pour la France (profils VFR). Ni longueur de piste, ni minima, ni restrictions de bruit.
- **Relief et obstacles** : modèle d'environ 90 m ; les obstacles d'OpenStreetMap sont incomplets (lignes électriques absentes, hauteurs parfois manquantes).
- Les textes d'aide, le glossaire et les seuils (marges de 1 000 ft, catégories de vol) sont indicatifs.

## Licence

Aucune licence n'est définie pour l'instant : tous droits réservés par défaut.
