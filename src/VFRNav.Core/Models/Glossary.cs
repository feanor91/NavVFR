using System.Globalization;
using System.Text;

namespace VFRNav.Core.Models;

/// <param name="Term">Sigle tel qu'on le voit sur les cartes, dans l'application ou dans un message météo.</param>
/// <param name="Meaning">Signification en toutes lettres.</param>
/// <param name="Explanation">Ce que cela veut dire en pratique pour un pilote à vue.</param>
public sealed record GlossaryEntry(string Category, string Term, string Meaning, string Explanation);

/// <summary>
/// Sigles des cartes aéronautiques (VAC, OACI), des zones, des messages météo et de VFRNav, expliqués en français.
/// Texte général pour la simulation : il ne remplace pas l'AIP ni les publications du SIA.
/// </summary>
public static class Glossary
{
    public const string Zones = "Espaces aériens";
    public const string Limits = "Altitudes, hauteurs et unités";
    public const string Services = "Services, radio et documents";
    public const string Navigation = "Navigation et radionavigation";
    public const string Aerodrome = "Aérodrome et cartes VAC";
    public const string Weather = "Météo";
    public const string Rules = "Règles de vol";
    public const string Symbols = "Symboles de la carte VFRNav";

    public static readonly IReadOnlyList<string> Categories = [Zones, Limits, Services, Navigation, Aerodrome, Weather, Rules, Symbols];

    public static readonly IReadOnlyList<GlossaryEntry> Entries =
    [
        // ---- espaces aériens
        new(Zones, "CTR", "Zone de contrôle (Control Zone)", "Espace contrôlé du sol jusqu'à une hauteur donnée, autour d'un aérodrome important. Il faut une clairance du contrôle pour y entrer."),
        new(Zones, "TMA", "Région de contrôle terminale", "Espace contrôlé en altitude au-dessus d'un ou plusieurs aérodromes, pour les arrivées et départs. Son plancher est en altitude : en dessous, on est hors de la zone."),
        new(Zones, "CTA", "Région de contrôle", "Espace contrôlé en altitude, voisin de la TMA mais plus général, souvent plus haut."),
        new(Zones, "LTA / UTA", "Région de contrôle inférieure / supérieure", "Espaces contrôlés à moyenne ou haute altitude (LTA : niveaux bas, UTA : niveaux hauts)."),
        new(Zones, "FIR / UIR", "Région d'information de vol / région supérieure", "Grands découpages du ciel dans lesquels un service d'information et d'alerte est assuré. Aucune contrainte pour un vol à vue simple."),
        new(Zones, "R", "Zone réglementée", "Le vol n'y est autorisé qu'à certaines conditions (activité militaire, sécurité, environnement). Elle peut n'être active qu'à certaines heures : vérifier avant d'entrer."),
        new(Zones, "D", "Zone dangereuse", "Activités risquées pour un avion (tirs, essais). Le survol n'est pas interdit mais déconseillé quand la zone est active."),
        new(Zones, "P", "Zone interdite", "Survol interdit, sauf autorisation spéciale."),
        new(Zones, "LF-R324, LF-D…, LF-P…", "Numéro de zone", "« LF » désigne la France, la lettre le type (R, D ou P) et le nombre l'identifie. Exemple : LF-R324 est la zone réglementée n° 324."),
        new(Zones, "TRA / TSA", "Zone temporairement réservée / ségréguée", "Réservée à une activité (souvent militaire) quand elle est activée. Les autres avions doivent l'éviter pendant ce temps."),
        new(Zones, "ZRT", "Zone réglementée temporaire (désignation de la carte)", "Restriction qui n'existe que pendant une période donnée, publiée par NOTAM ou information aéronautique."),
        new(Zones, "RMZ", "Zone à radio obligatoire (Radio Mandatory Zone)", "Il faut écouter la fréquence indiquée et, en général, s'annoncer avant d'y entrer. Aucune clairance n'est demandée."),
        new(Zones, "TMZ", "Zone à transpondeur obligatoire (Transponder Mandatory Zone)", "Le transpondeur doit être allumé et réglé pour entrer dans la zone."),
        new(Zones, "ATZ", "Zone de circulation d'aérodrome", "Zone autour d'un aérodrome où la circulation locale est intense : respecter le circuit et la radio."),
        new(Zones, "SIV", "Service d'information de vol", "Un contrôleur donne des informations de vol et de trafic sur la fréquence indiquée. S'y connecter est utile mais non obligatoire."),
        new(Zones, "FIS", "Flight Information Service", "Nom international du service d'information de vol (en France : SIV)."),
        new(Zones, "ADIZ", "Zone d'identification de défense aérienne", "Les aéronefs doivent être identifiés avant d'y entrer."),
        new(Zones, "Classe A", "Espace aérien de classe A", "Réservé aux vols aux instruments (IFR). Le vol à vue y est interdit en France sauf clairance particulière."),
        new(Zones, "Classe B / C", "Espaces aériens de classes B et C", "Clairance du contrôle obligatoire pour tous les vols. La classe B n'existe pas en France ; la classe C y est rare pour le vol à vue."),
        new(Zones, "Classe D", "Espace aérien de classe D", "Clairance et radio obligatoires pour tous. C'est la classe courante des CTR et de nombreuses TMA."),
        new(Zones, "Classe E", "Espace aérien de classe E", "Contrôlé pour les IFR ; un vol à vue peut y voler sans clairance, la radio n'étant pas obligatoire."),
        new(Zones, "Classe G", "Espace aérien non contrôlé", "On vole sous sa propre responsabilité, avec ou sans service d'information, en regardant dehors."),

        // ---- altitudes et unités
        new(Limits, "SFC", "Surface", "Limite qui part du sol : la zone commence au niveau du terrain."),
        new(Limits, "GND / AGL", "Au-dessus du sol (Ground / Above Ground Level)", "Hauteur mesurée à partir du terrain survolé. « 500ft GND » veut dire 500 ft au-dessus du sol, où qu'il soit."),
        new(Limits, "AMSL / MSL", "Au-dessus du niveau moyen de la mer", "Altitude mesurée à partir du niveau de la mer, lue sur l'altimètre calé au QNH. « 1500ft AMSL » est la même altitude partout."),
        new(Limits, "FL", "Niveau de vol (Flight Level)", "Altitude en centaines de pieds, lue avec le calage standard 1013 hPa. FL65 = environ 6 500 ft. On l'utilise au-dessus d'une altitude de transition."),
        new(Limits, "ft", "Pied (feet)", "Unité d'altitude : 1 ft = 0,3048 m. 1 000 ft ≈ 305 m."),
        new(Limits, "NM", "Mille nautique", "Unité de distance : 1 NM = 1,852 km, soit une minute d'arc de latitude."),
        new(Limits, "kt", "Nœud", "Vitesse d'un mille nautique par heure : 1 kt = 1,852 km/h."),
        new(Limits, "hPa", "Hectopascal", "Unité de pression atmosphérique. Au niveau de la mer, la pression standard est de 1013,25 hPa."),
        new(Limits, "QNH", "Calage altimétrique", "Pression ramenée au niveau de la mer. Affiché sur l'altimètre, il donne l'altitude vraie au-dessus de la mer."),
        new(Limits, "QFE", "Pression au niveau du terrain", "Affiché sur l'altimètre, il donne la hauteur au-dessus de l'aérodrome (0 au sol)."),
        new(Limits, "1013", "Calage standard", "Pression de référence (1013,25 hPa) utilisée pour les niveaux de vol."),
        new(Limits, "MAX / MIN", "Limite maximale / minimale", "Sur une carte VAC, « 1500 ft MAX » interdit de dépasser cette altitude à ce point ; « MIN » interdit d'être plus bas."),

        // ---- services, radio et documents
        new(Services, "ATC", "Contrôle de la circulation aérienne (Air Traffic Control)", "Le service qui donne les clairances et sépare les avions dans les espaces contrôlés."),
        new(Services, "ATS", "Services de la circulation aérienne", "Ensemble des services rendus aux aéronefs : contrôle, information de vol, alerte."),
        new(Services, "TWR", "Tour de contrôle", "Contrôle les décollages et atterrissages et la circulation autour de l'aérodrome."),
        new(Services, "APP", "Approche", "Contrôle les arrivées et départs aux instruments et donne les services en TMA."),
        new(Services, "GND / SOL", "Contrôle au sol", "Gère la circulation des avions sur les voies de circulation et les parkings."),
        new(Services, "AFIS", "Service d'information de vol d'aérodrome", "Un agent informe sur le trafic et la piste en service, sans donner de clairance. Le pilote reste responsable."),
        new(Services, "A/A", "Air-air", "Fréquence d'auto-information entre avions quand il n'y a pas d'organisme (« air-air 123,50 »)."),
        new(Services, "ATIS", "Service automatique d'information de région terminale", "Message radio continu : piste en service, vent, QNH. À écouter avant de contacter la tour."),
        new(Services, "RTF", "Radiotéléphonie", "Communications radio entre le pilote et le contrôle."),
        new(Services, "SIA", "Service de l'information aéronautique", "Organisme français qui publie l'AIP, les cartes VAC et les NOTAM."),
        new(Services, "AIP", "Publication d'information aéronautique", "Le document officiel qui décrit les aérodromes, les espaces aériens et les règles d'un pays."),
        new(Services, "SUP AIP / AIC", "Supplément AIP / circulaire d'information", "Modifications temporaires de l'AIP et informations générales publiées entre deux éditions."),
        new(Services, "NOTAM", "Avis aux navigants (Notice to Airmen)", "Message qui signale un changement temporaire : piste fermée, zone activée, grue… À consulter avant tout vol réel (SOFIA-Briefing)."),
        new(Services, "SOFIA", "Service en ligne d'information de vol (SIA)", "Site du SIA où l'on consulte les NOTAM, les zones actives et dépose un plan de vol."),
        new(Services, "OACI / ICAO", "Organisation de l'aviation civile internationale", "Elle fixe les codes utilisés partout : indicatifs de quatre lettres des aérodromes (LFPN, LFPL…), cartes, unités."),
        new(Services, "VAC", "Carte d'approche à vue (Visual Approach Chart)", "Carte d'un aérodrome pour les vols à vue : circuit, points de report, fréquences, pistes, consignes."),

        // ---- navigation
        new(Navigation, "VOR", "Radiophare omnidirectionnel VHF", "Balise qui donne la direction dans laquelle on se trouve par rapport à elle (radiale), sur une fréquence en MHz."),
        new(Navigation, "NDB", "Radiophare non directionnel", "Balise simple, sur une fréquence en kHz. Le récepteur ADF indique la direction vers la balise."),
        new(Navigation, "ADF", "Radiogoniomètre automatique", "L'instrument de bord dont l'aiguille pointe vers un NDB."),
        new(Navigation, "DME", "Équipement de mesure de distance", "Donne la distance en NM jusqu'à une balise. Souvent associé à un VOR."),
        new(Navigation, "ILS", "Système d'atterrissage aux instruments", "Guidage radio précis vers la piste, utilisé en IFR. Hors vol à vue, il sert aussi de repère d'axe."),
        new(Navigation, "GPS / RNAV", "Navigation par satellite / de surface", "Navigation par positions calculées plutôt que par balises."),
        new(Navigation, "WP", "Point de route (waypoint)", "Point tournant ou repère posé sur la route."),
        new(Navigation, "Rv", "Route vraie", "Direction de la route par rapport au nord géographique."),
        new(Navigation, "Rm", "Route magnétique", "Route vraie corrigée de la déclinaison magnétique : direction à lire sur le compas hors dérive."),
        new(Navigation, "Cm", "Cap magnétique", "Direction à tenir avec l'avion pour suivre la route malgré le vent."),
        new(Navigation, "Dérive", "Correction de dérive", "Angle dont il faut corriger le cap pour compenser le vent de travers."),
        new(Navigation, "GS / Vs", "Vitesse sol (Ground Speed)", "Vitesse de l'avion par rapport au sol, vent compris."),
        new(Navigation, "TAS / Vp", "Vitesse propre (True Air Speed)", "Vitesse de l'avion par rapport à l'air."),
        new(Navigation, "QDM / QDR", "Relèvement magnétique vers / depuis une station", "QDM : cap magnétique pour aller vers la station. QDR : direction dans laquelle on se trouve par rapport à elle."),
        new(Navigation, "Déclinaison", "Variation magnétique", "Écart entre le nord géographique et le nord magnétique, exprimé en degrés Est ou Ouest."),
        new(Navigation, "ETA / ETD / ETE", "Heure d'arrivée / de départ / durée estimées", "ETA : heure d'arrivée prévue. ETD : heure de départ prévue. ETE : temps de vol estimé."),

        // ---- aérodrome et cartes VAC
        new(Aerodrome, "ARP", "Point de référence d'aérodrome", "Position officielle de l'aérodrome sur les cartes."),
        new(Aerodrome, "RWY", "Piste (runway)", "Indiquée par son QFU arrondi : piste 25 pour un QFU voisin de 250°. Les lettres L, R, C : gauche, droite, centre."),
        new(Aerodrome, "QFU", "Orientation magnétique de la piste", "Cap magnétique de l'axe de piste, dans le sens d'utilisation."),
        new(Aerodrome, "TWY", "Voie de circulation (taxiway)", "Chemin que suit l'avion entre la piste et les parkings."),
        new(Aerodrome, "THR", "Seuil de piste (threshold)", "Début de la partie de piste utilisable pour l'atterrissage."),
        new(Aerodrome, "ELEV", "Altitude de l'aérodrome", "Altitude du point le plus haut de la piste, en pieds au-dessus de la mer."),
        new(Aerodrome, "AD", "Aérodrome", "Terrain d'aviation. Les cartes VAC portent le préfixe « AD 2 »."),
        new(Aerodrome, "PAPI", "Indicateur de pente d'approche", "Quatre feux à côté de la piste : deux rouges et deux blancs indiquent une bonne pente."),
        new(Aerodrome, "ACFT", "Aéronef", "Désigne un avion, un hélicoptère, un planeur, un ULM…"),
        new(Aerodrome, "ULM", "Ultra-léger motorisé", "Petit avion léger. Certains aérodromes lui réservent des circuits ou des horaires."),
        new(Aerodrome, "PN", "Préavis nécessaire", "Il faut prévenir l'aérodrome avant l'arrivée."),
        new(Aerodrome, "PPR", "Autorisation préalable (Prior Permission Required)", "L'aérodrome doit avoir donné son accord avant l'arrivée."),
        new(Aerodrome, "H24", "Vingt-quatre heures sur vingt-quatre", "Le service ou l'aérodrome est ouvert en permanence."),
        new(Aerodrome, "HJ", "Heures de jour", "Du lever au coucher du soleil."),
        new(Aerodrome, "HO", "Heures de bureau", "Aux heures d'ouverture du service, souvent à demander avant le vol."),
        new(Aerodrome, "Point de report", "Point de compte rendu VFR", "Repère connu (village, étang, route) où l'on annonce sa position à l'arrivée ou au départ. Désigné par une lettre ou deux (N, S, NE, EA…)."),
        new(Aerodrome, "Circuit", "Circuit d'aérodrome", "Trajectoire standard autour de la piste : vent arrière, base, finale. Il se fait à une hauteur et d'un côté fixés par la carte."),
        new(Aerodrome, "Main gauche / droite", "Sens du circuit", "Virages vers la gauche ou vers la droite. La main gauche est la règle, sauf indication contraire de la carte."),

        // ---- météo
        new(Weather, "METAR", "Observation météo d'aérodrome", "Message codé qui donne le vent, la visibilité, les nuages, la température et le QNH au moment de l'observation."),
        new(Weather, "TAF", "Prévision d'aérodrome", "Prévision de la météo sur un aérodrome pour les heures à venir. VFRNav la traduit en clair."),
        new(Weather, "SPECI", "Observation spéciale", "METAR émis en dehors de l'heure normale parce que la météo change vite."),
        new(Weather, "CAVOK", "Ceiling and Visibility OK", "Visibilité de 10 km ou plus, pas de nuage significatif sous 5 000 ft, pas de phénomène notable : conditions très bonnes."),
        new(Weather, "FEW / SCT / BKN / OVC", "Quantité de nuages", "FEW : 1 à 2 huitièmes, SCT : 3 à 4, BKN : 5 à 7 (fragmenté), OVC : 8 (couvert). Le nombre qui suit est la hauteur de la base en centaines de pieds."),
        new(Weather, "BECMG / TEMPO / PROB", "Évolution dans un TAF", "BECMG : devient. TEMPO : par moments, temporairement. PROB : probabilité de 30 ou 40 %."),
        new(Weather, "NOSIG", "Pas de changement significatif", "Aucune évolution notable prévue dans les deux heures qui suivent."),
        new(Weather, "Visibilité", "Visibilité horizontale", "Donnée en mètres (9999 = 10 km ou plus) ou en kilomètres."),
        new(Weather, "VMC / IMC", "Conditions météorologiques de vol à vue / aux instruments", "Les conditions minimales de visibilité et de distance aux nuages permettent, ou non, le vol à vue."),
        new(Weather, "VFR / MVFR / IFR / LIFR", "Catégories de vol d'après le METAR", "VFR : bonnes conditions. MVFR : marginales. IFR : mauvaises, le vol à vue est difficile. LIFR : très mauvaises. VFRNav les montre par la couleur d'un terrain."),

        // ---- règles de vol
        new(Rules, "VFR", "Règles de vol à vue", "On pilote en s'aidant de ce que l'on voit dehors : sol, horizon, autres avions."),
        new(Rules, "IFR", "Règles de vol aux instruments", "On pilote avec les instruments, y compris dans les nuages. Nécessite une qualification."),
        new(Rules, "SVFR", "VFR spécial", "Autorisation du contrôle d'entrer ou de traverser une CTR avec une visibilité inférieure aux minimums du vol à vue normal."),
        new(Rules, "Clairance", "Autorisation du contrôle", "Accord du contrôle pour entrer dans une zone ou faire une manœuvre. Sans clairance, on n'entre pas."),
        new(Rules, "Transit", "Traversée d'une zone", "Passage dans un espace contrôlé sans y atterrir, avec clairance si la classe l'exige."),

        // ---- symboles de la carte VFRNav
        new(Symbols, "Pastille de terrain", "Terrain et météo", "Rond sur un terrain : sa couleur donne la catégorie de la météo (vert VFR, bleu MVFR, rouge IFR, violet LIFR, gris : pas de METAR)."),
        new(Symbols, "Triangle jaune", "Point de report VFR", "Point de report d'un aérodrome (lu sur sa carte VAC). Un clic l'ajoute à la route sous le nom « ICAO-point »."),
        new(Symbols, "Losange violet", "VOR", "Balise VOR avec sa fréquence en MHz."),
        new(Symbols, "Rond orange", "NDB", "Radiophare non directionnel avec sa fréquence en kHz."),
        new(Symbols, "Contour rouge", "Zones réglementées, dangereuses, interdites", "Zones R, D, P : à vérifier avant d'entrer."),
        new(Symbols, "Contour bleu", "CTR et TMA", "Espaces contrôlés : la classe et les limites sont écrites le long du trait."),
        new(Symbols, "Contour violet", "Radio, transpondeur, circulation d'aérodrome", "RMZ, TMZ, ATZ : la radio ou le transpondeur est demandé."),
        new(Symbols, "Contour orange", "Loisirs et sport aérien", "Planeur, parachutage, voltige, ULM : regarder dehors."),
        new(Symbols, "Texte le long du trait", "Nom et limites de la zone", "Le nom, le plancher et le plafond sont écrits le long du contour, du côté intérieur de la zone. Survoler le trait affiche le détail."),
        new(Symbols, "Avion rose", "Votre avion dans MSFS", "Position et cap de l'avion, mis à jour une fois par seconde quand le simulateur est lancé."),
        new(Symbols, "Ronds numérotés", "Points de la route", "Départ et arrivée en bleu, points intermédiaires en rouge, numérotés dans l'ordre. Un clic sur un point le retire."),
    ];

    /// <summary>Entrées dont le sigle, le sens ou l'explication contient le texte cherché (sans tenir compte des accents ni de la casse).</summary>
    public static IReadOnlyList<GlossaryEntry> Search(string? query)
    {
        string q = Normalize(query ?? "");
        if (q.Length == 0) return Entries;
        return [.. Entries
            .Select(e => (Entry: e, Rank: Rank(e, q)))
            .Where(t => t.Rank > 0)
            .OrderByDescending(t => t.Rank)
            .Select(t => t.Entry)];
    }

    private static int Rank(GlossaryEntry e, string q)
    {
        string term = Normalize(e.Term);
        if (term == q) return 4;
        if (term.Split(' ', '/', ',', '-').Any(w => w.Trim() == q)) return 3;
        if (term.Contains(q, StringComparison.Ordinal)) return 2;
        return Normalize(e.Meaning).Contains(q, StringComparison.Ordinal) || Normalize(e.Explanation).Contains(q, StringComparison.Ordinal) ? 1 : 0;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Trim();
    }
}
