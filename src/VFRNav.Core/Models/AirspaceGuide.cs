using System.Globalization;
using System.Text.RegularExpressions;

namespace VFRNav.Core.Models;

public sealed record GuideSection(string Heading, string Text);

/// <summary>
/// Explications en français pour un débutant : ce qu'est une zone, sa classe, ses limites et ce que cela change en vol à vue.
/// Texte général, pour la simulation : il ne remplace ni l'AIP ni les publications du SIA.
/// </summary>
public static partial class AirspaceGuide
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public const string Disclaimer = "Explications générales pour la simulation. Pour un vol réel, se référer à l'AIP France et aux publications du SIA.";

    public static IReadOnlyList<GuideSection> Explain(Airspace a, double? altitudeFt = null)
    {
        var sections = new List<GuideSection>
        {
            new($"{Airspace.TypeName(a.TypeCode)} : qu'est-ce que c'est ?", TypeText(a.TypeCode))
        };
        if (a.IcaoClass.Length > 0) sections.Add(new($"Classe {a.IcaoClass}", ClassText(a.IcaoClass)));

        sections.Add(new("Limites verticales",
            $"Plancher : {Show(a.Lower)}. Plafond : {Show(a.Upper)}. La zone n'existe qu'entre les deux : en dessous du plancher ou au-dessus du plafond, ses règles ne s'appliquent pas."));
        if (altitudeFt is { } alt) sections.Add(new($"À ton altitude ({alt.ToString("N0", Fr)} ft)", PositionText(a, alt)));

        sections.Add(new("En pratique en vol à vue", PracticeText(a)));

        if (a.Details?.Flags is { Count: > 0 } flags) sections.Add(new("Conditions d'activation", string.Join(", ", flags) + "."));
        if (a.Details?.Remarks is { Length: > 0 } remarks) sections.Add(new("Remarques publiées", remarks.Trim()));
        if (a.Details?.Frequencies is { Count: > 0 } freqs)
            sections.Add(new("Fréquences à utiliser", string.Join(" · ", freqs.Select(f => f.Name.Length > 0 ? $"{f.Name} {f.Value}" : f.Value))));
        return sections;
    }

    private static string Show(string limit) => limit.Length == 0 ? "non précisé" : ExplainLimit(limit) is { Length: > 0 } e ? $"{limit} ({e})" : limit;

    /// <summary>Traduit une limite telle que « FL65 », « 1500ft AMSL », « 500ft GND » ou « SFC ».</summary>
    public static string ExplainLimit(string limit)
    {
        limit = limit.Trim();
        if (limit == "SFC") return "depuis le sol";
        var m = FlRx().Match(limit);
        if (m.Success)
        {
            int fl = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return $"niveau de vol {fl}, soit environ {(fl * 100).ToString("N0", Fr)} ft au calage standard 1013 hPa";
        }
        m = HeightRx().Match(limit);
        if (m.Success)
        {
            string unit = m.Groups[2].Value == "m" ? "m" : "ft";
            string n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString("N0", Fr);
            return m.Groups[3].Value == "GND"
                ? $"{n} {unit} au-dessus du sol"
                : $"{n} {unit} d'altitude, au-dessus du niveau de la mer";
        }
        return "";
    }

    private static string PositionText(Airspace a, double altitudeFt)
    {
        string approx = " (comparaison faite sans tenir compte du relief)";
        if (altitudeFt < a.LowerFt) return $"Tu serais sous le plancher : en dessous de la zone, ses règles ne s'appliquent pas à ton altitude{approx}.";
        if (altitudeFt > a.UpperFt) return $"Tu serais au-dessus du plafond : la zone est sous toi{approx}.";
        return $"Tu serais dans la zone : ses règles s'appliquent à ton altitude{approx}.";
    }

    private static string ClassText(string cls) => cls switch
    {
        "A" => "Réservée aux vols aux instruments (IFR). Le vol à vue (VFR) y est interdit.",
        "B" => "Vols IFR et VFR acceptés, avec clairance du contrôle pour tous. Tous les avions sont séparés.",
        "C" => "Clairance du contrôle obligatoire pour tous. Les IFR sont séparés de tous ; un VFR reçoit des informations de trafic sur les autres VFR.",
        "D" => "Clairance du contrôle et radio obligatoires pour tous (IFR et VFR). Les IFR sont séparés entre eux ; les VFR reçoivent des informations de trafic. Classe courante autour des aérodromes contrôlés.",
        "E" => "Contrôlé pour les IFR, mais un VFR peut y voler sans clairance (la radio n'est pas obligatoire). Le contrôle donne des informations de trafic si possible.",
        "F" => "Non utilisée en France : service de conseil, sans obligation de clairance.",
        "G" => "Espace non contrôlé : tu vols sous ta responsabilité, en regardant dehors, avec ou sans service d'information.",
        _ => ""
    };

    private static string TypeText(int type) => type switch
    {
        1 => "Zone réglementée (R) : le vol n'y est autorisé qu'à certaines conditions (activité militaire, sécurité, protection de l'environnement). Elle peut n'être active qu'à certaines heures.",
        2 => "Zone dangereuse (D) : des activités risquées pour un avion peuvent s'y dérouler (tirs, exercices, essais). Le survol n'est pas interdit mais fortement déconseillé quand elle est active.",
        3 => "Zone interdite (P) : le survol est interdit, sauf autorisation spéciale.",
        4 => "CTR (zone de contrôle) : espace contrôlé autour d'un aérodrome important, du sol jusqu'à une hauteur donnée. Le contrôleur y gère les décollages, atterrissages et arrivées.",
        5 => "TMZ (zone à transpondeur obligatoire) : le transpondeur doit être allumé et réglé pour y entrer.",
        6 => "RMZ (zone à radio obligatoire) : il faut écouter la fréquence indiquée, et en général s'annoncer, avant d'y entrer. Pas de clairance demandée pour autant.",
        7 => "TMA (région de contrôle terminale) : espace contrôlé en altitude, au-dessus d'un ou plusieurs aérodromes, pour gérer les arrivées et départs. Son plancher est en altitude, pas au sol.",
        8 => "TRA (zone temporairement réservée) : réservée à une activité, souvent militaire, quand elle est activée.",
        9 => "TSA (zone temporairement ségréguée) : réservée à une activité quand elle est activée ; les autres avions doivent l'éviter.",
        10 => "FIR (région d'information de vol) : grand découpage du ciel dans lequel un service d'information et d'alerte est assuré. Ce n'est pas une contrainte pour le pilote.",
        11 => "UIR (région supérieure d'information de vol) : l'équivalent de la FIR pour les hautes altitudes.",
        12 => "ADIZ : zone où les aéronefs doivent être identifiés avant d'y entrer.",
        13 => "ATZ (zone de circulation d'aérodrome) : zone autour d'un aérodrome où la circulation locale est intense ; il faut respecter les règles du circuit et la radio.",
        14 => "MATZ : zone de circulation d'un aérodrome militaire.",
        15 => "Voie aérienne : couloir utilisé surtout par les vols aux instruments.",
        16 => "MTR : itinéraire de vol militaire à basse altitude.",
        17 => "Zone d'alerte : activité inhabituelle (par exemple entraînement intensif) ; redoublez de vigilance.",
        18 => "Zone d'avertissement : activité dangereuse pour la navigation aérienne, souvent hors des eaux territoriales.",
        19 => "Zone protégée : survol à éviter ou à haute altitude (par exemple site naturel, centrale).",
        20 => "HTZ : zone d'intense activité d'hélicoptères.",
        21 => "Secteur planeur : des planeurs y évoluent (treuil, remorquage, vol de pente). Ils sont difficiles à voir et se déplacent sans bruit.",
        22 => "Zone où le transpondeur est recommandé ou demandé.",
        23 => "TIZ : zone d'information de trafic.",
        24 => "TIA : espace où un service d'information de trafic est assuré.",
        25 => "MTA : région de contrôle militaire.",
        26 => "CTA (région de contrôle) : espace contrôlé en altitude, au-dessus d'un plancher. Voisine de la TMA mais plus générale, souvent plus haute.",
        27 => "Secteur de contrôle de route (ACC) : découpage interne du contrôle en route. Aucune contrainte directe pour un vol à vue.",
        28 => "Activité aérienne sportive : voltige, parachutage, aéromodélisme, ULM… Les autres avions sont avertis de s'en écarter.",
        29 => "Restriction de survol à basse altitude : on ne doit pas survoler la zone sous une certaine hauteur (par exemple pour le bruit ou la faune).",
        30 => "MRT : route militaire d'entraînement.",
        31 => "TFR : restriction temporaire de vol.",
        32 => "Secteur VFR : espace aménagé pour faciliter le passage des vols à vue.",
        33 => "Secteur FIS (service d'information de vol) : un contrôleur donne des informations sur la fréquence indiquée. Tu peux t'y connecter pour être renseigné ; ce n'est pas obligatoire.",
        34 => "LTA : espace contrôlé à basse altitude.",
        35 => "UTA : espace contrôlé en haute altitude.",
        36 => "MCTR : zone de contrôle militaire.",
        _ => "Espace aérien dont le type n'est pas précisé par la source."
    };

    private static string PracticeText(Airspace a) => a.TypeCode switch
    {
        3 => "N'entre pas : le survol est interdit.",
        1 or 2 or 8 or 9 or 17 or 18 or 19 or 29 or 31 =>
            "Avant le vol, vérifie si la zone est active (NOTAM, SIA). Si elle l'est, contourne-la ou contacte l'organisme indiqué avant d'entrer. Dans le doute, évite-la.",
        4 or 36 => "Pour entrer, appelle le contrôle de l'aérodrome sur la fréquence indiquée, attends sa clairance et prépare radio et transpondeur. Sans clairance, contourne la zone.",
        7 or 24 or 25 or 26 or 34 or 35 => "En dessous du plancher tu es hors de la zone. Au-dessus, la clairance du contrôle est nécessaire quand la classe l'exige. Surveille ton altitude.",
        6 => "Écoute la fréquence avant d'entrer et annonce ta position et tes intentions. Aucune clairance n'est demandée.",
        5 or 22 => "Transpondeur allumé et réglé sur le code demandé pendant toute la traversée.",
        13 or 14 => "Annonce-toi par radio sur la fréquence de l'aérodrome et respecte le circuit et la circulation locale.",
        33 => "Tu peux appeler la fréquence FIS pour recevoir des informations de trafic et une aide à la navigation ; ce n'est pas une obligation.",
        10 or 11 or 27 or 32 or 15 or 16 or 30 => "Aucune contrainte particulière pour un vol à vue simple : c'est surtout un repère d'organisation.",
        _ => a.Kind switch
        {
            AirspaceKind.Leisure => "Regarde dehors en permanence : ces aéronefs sont difficiles à voir. Passe au large, ou franchis la zone bien au-dessus ou en dessous.",
            AirspaceKind.Ctr or AirspaceKind.Tma => "Vérifie la classe et les limites avant d'entrer ; un contact radio et une clairance peuvent être nécessaires.",
            AirspaceKind.Restricted => "Vérifie l'activation de la zone avant d'y entrer.",
            AirspaceKind.Radio => "Un contact radio ou un transpondeur peut être demandé : lis les remarques de la zone.",
            _ => "Lis les remarques publiées de la zone et applique la prudence."
        }
    };

    [GeneratedRegex(@"^FL(\d+)$")] private static partial Regex FlRx();
    [GeneratedRegex(@"^(\d+)(ft|m) (AMSL|GND)$")] private static partial Regex HeightRx();
}
