using System.Globalization;
using System.Text.RegularExpressions;

namespace VFRNav.Core.Weather;

/// <summary>Un METAR ou un TAF traduit en français.</summary>
/// <param name="Header">Titre : aérodrome, type, heure.</param>
/// <param name="Lines">Lignes en clair ; l'indentation (espaces) marque les sous-éléments.</param>
/// <param name="Category">Catégorie de vol (VFR, MVFR, IFR, LIFR) calculée sur le METAR ; null pour un TAF.</param>
/// <param name="VisibilityM">Visibilité en mètres (10 000 pour 9999 ou CAVOK) ; null si le message n'en donne pas.</param>
/// <param name="CeilingFt">Hauteur du plus bas nuage fragmenté ou couvert ; null si aucun.</param>
public sealed record DecodedReport(string Station, string Kind, string Raw, string Header, IReadOnlyList<string> Lines, string? Category,
    double? VisibilityM = null, int? CeilingFt = null);

/// <summary>Traduit les METAR et TAF en français clair. Les groupes inconnus sont signalés, jamais devinés.</summary>
public static partial class WeatherDecoder
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // ---------- découpage du texte brut ----------

    /// <summary>Un METAR par ligne (le service renvoie une observation par ligne).</summary>
    public static IReadOnlyList<DecodedReport> DecodeMetars(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(DecodeMetar)
            .ToList();

    /// <summary>Un TAF commence par « TAF » ; les lignes indentées qui suivent en font partie.</summary>
    public static IReadOnlyList<DecodedReport> DecodeTafs(string text)
    {
        var reports = new List<string>();
        foreach (string line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0))
        {
            if (reports.Count == 0 || line.TrimStart().StartsWith("TAF", StringComparison.Ordinal) && !char.IsWhiteSpace(line[0]))
                reports.Add(line.Trim());
            else
                reports[^1] += " " + line.Trim();
        }
        return reports.Select(DecodeTaf).ToList();
    }

    // ---------- METAR ----------

    public static DecodedReport DecodeMetar(string raw)
    {
        var tokens = Tokens(raw);
        int i = 0;
        string kind = "METAR";
        if (i < tokens.Count && tokens[i] is "METAR" or "SPECI") kind = tokens[i++];
        string station = i < tokens.Count ? tokens[i++] : "";
        string? time = i < tokens.Count && TimeRx().IsMatch(tokens[i]) ? Day(tokens[i++]) : null;

        var lines = new List<string>();
        var state = new Elements();
        var rest = tokens.Skip(i).ToList();
        int trend = rest.FindIndex(t => t is "BECMG" or "TEMPO" or "NOSIG" or "RMK");
        var main = trend < 0 ? rest : rest.Take(trend).ToList();

        foreach (string t in main)
        {
            if (t == "AUTO") lines.Add("Observation automatique (sans observateur)");
            else if (t == "COR") lines.Add("Message corrigé");
            else if (t == "NIL") lines.Add("Aucune observation disponible");
        }
        Decode(main.Where(t => t is not ("AUTO" or "COR" or "NIL")), lines, state, "");

        if (trend >= 0)
        {
            var tail = rest.Skip(trend).ToList();
            int rmk = tail.IndexOf("RMK");
            if (rmk >= 0) tail = tail.Take(rmk).ToList();
            DecodeTrend(tail, lines);
        }

        string? category = Category(state);
        if (category is not null) lines.Add($"Catégorie de vol : {CategoryText(category)}");
        string when = time is null ? "" : $" — {time}";
        return new DecodedReport(station, kind, raw, $"{station}  {kind}{when}", lines, category,
            state.VisibilityM, state.CeilingFt is int.MaxValue ? null : state.CeilingFt);
    }

    private static void DecodeTrend(List<string> tail, List<string> lines)
    {
        var group = new List<string>();
        string? label = null;

        void Flush()
        {
            if (label is null) return;
            lines.Add(label);
            if (group.Count > 0) Decode(group, lines, new Elements(), "  ");
            group.Clear();
        }

        foreach (string t in tail)
        {
            if (t == "NOSIG") { Flush(); label = null; lines.Add("Tendance : pas de changement significatif dans les 2 heures"); }
            else if (t == "BECMG") { Flush(); label = "Tendance : évolution durable vers :"; }
            else if (t == "TEMPO") { Flush(); label = "Tendance : variations temporaires :"; }
            else if (TrendTimeRx().IsMatch(t))
            {
                var m = TrendTimeRx().Match(t);
                string kind = m.Groups[1].Value switch { "FM" => "à partir de", "TL" => "jusqu'à", _ => "vers" };
                label = (label ?? "Tendance") + $" ({kind} {m.Groups[2].Value[..2]}:{m.Groups[2].Value[2..]} UTC)";
            }
            else group.Add(t);
        }
        Flush();
    }

    // ---------- TAF ----------

    public static DecodedReport DecodeTaf(string raw)
    {
        var tokens = Tokens(raw);
        int i = 0;
        var lines = new List<string>();
        if (i < tokens.Count && tokens[i] == "TAF") i++;
        while (i < tokens.Count && tokens[i] is "AMD" or "COR" or "NIL" or "CNL")
        {
            lines.Add(tokens[i] switch
            {
                "AMD" => "Prévision modifiée (amendée)",
                "COR" => "Prévision corrigée",
                "CNL" => "Prévision annulée",
                _ => "Aucune prévision disponible"
            });
            i++;
        }
        string station = i < tokens.Count ? tokens[i++] : "";
        string? issued = i < tokens.Count && TimeRx().IsMatch(tokens[i]) ? Day(tokens[i++]) : null;
        string? validity = i < tokens.Count && ValidityRx().IsMatch(tokens[i]) ? Period(tokens[i++]) : null;
        if (validity is not null) lines.Add($"Valable {validity}");

        var group = new List<string>();
        string label = "Prévision de base :";

        void Flush()
        {
            lines.Add(label);
            Decode(group, lines, new Elements(), "  ");
            group.Clear();
        }

        for (; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t.StartsWith("FM", StringComparison.Ordinal) && FromRx().IsMatch(t))
            {
                Flush();
                var m = FromRx().Match(t);
                label = $"À partir du {m.Groups[1].Value} à {m.Groups[2].Value}:{m.Groups[3].Value} UTC :";
            }
            else if (t is "BECMG" or "TEMPO" || ProbRx().IsMatch(t))
            {
                Flush();
                string prob = "";
                string kind = t;
                if (ProbRx().IsMatch(t))
                {
                    prob = $"Probabilité {t[4..]} % ";
                    if (i + 1 < tokens.Count && tokens[i + 1] == "TEMPO") { i++; kind = "TEMPO"; } else kind = "";
                }
                string when = i + 1 < tokens.Count && ValidityRx().IsMatch(tokens[i + 1]) ? " " + Period(tokens[++i]) : "";
                label = kind switch
                {
                    "BECMG" => $"Évolution progressive{when} :",
                    "TEMPO" => $"{prob}Variations temporaires{when} :",
                    _ => $"{prob}d'un phénomène{when} :"
                };
            }
            else group.Add(t);
        }
        Flush();
        if (lines.Count > 0 && lines[^1].StartsWith("Prévision de base", StringComparison.Ordinal) && group.Count == 0)
            lines.RemoveAt(lines.Count - 1);

        string when2 = issued is null ? "" : $" — émis {issued}";
        return new DecodedReport(station, "TAF", raw, $"{station}  TAF{when2}", lines, null);
    }

    // ---------- éléments communs ----------

    private sealed class Elements
    {
        public double? VisibilityM;
        public int? CeilingFt;
        public bool Cavok;
    }

    private static void Decode(IEnumerable<string> tokens, List<string> lines, Elements state, string indent)
    {
        var unknown = new List<string>();
        foreach (string t in tokens)
        {
            Match m;
            if (t == "CAVOK")
            {
                state.Cavok = true;
                state.VisibilityM = 10000;
                lines.Add($"{indent}Visibilité 10 km ou plus, pas de nuage significatif, pas de phénomène (CAVOK)");
            }
            else if (t == "NSW") lines.Add($"{indent}Fin des phénomènes météo significatifs");
            else if ((m = WindRx().Match(t)).Success) lines.Add(indent + Wind(m));
            else if ((m = WindVarRx().Match(t)).Success) lines.Add($"{indent}Direction du vent variable entre {m.Groups[1].Value}° et {m.Groups[2].Value}°");
            else if (VisRx().IsMatch(t))
            {
                int v = int.Parse(t[..4], CultureInfo.InvariantCulture);
                state.VisibilityM = v == 9999 ? 10000 : v;
                lines.Add($"{indent}Visibilité : {Visibility(v)}");
            }
            else if ((m = RvrRx().Match(t)).Success)
                lines.Add($"{indent}Portée visuelle de piste {m.Groups[1].Value} : {int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)} m");
            else if ((m = CloudRx().Match(t)).Success) lines.Add(indent + Cloud(m, state));
            else if ((m = VertRx().Match(t)).Success)
            {
                if (int.TryParse(m.Groups[1].Value, out int h))
                {
                    state.CeilingFt = Math.Min(state.CeilingFt ?? int.MaxValue, h * 100);
                    lines.Add($"{indent}Ciel invisible, visibilité verticale {h * 100} ft ({Metres(h * 100)} m)");
                }
                else lines.Add($"{indent}Ciel invisible, visibilité verticale non mesurée");
            }
            else if (t is "SKC" or "CLR") lines.Add($"{indent}Ciel dégagé");
            else if (t is "NSC") lines.Add($"{indent}Pas de nuage significatif");
            else if (t is "NCD") lines.Add($"{indent}Aucun nuage détecté (station automatique)");
            else if ((m = TempRx().Match(t)).Success)
            {
                string temp = $"température {Temp(m.Groups[1].Value)} °C";
                lines.Add(m.Groups[2].Success && m.Groups[2].Value.Length > 0
                    ? $"{indent}Température {Temp(m.Groups[1].Value)} °C, point de rosée {Temp(m.Groups[2].Value)} °C"
                    : $"{indent}{char.ToUpper(temp[0], Fr)}{temp[1..]}");
            }
            else if ((m = QnhRx().Match(t)).Success) lines.Add($"{indent}QNH {m.Groups[1].Value} hPa");
            else if ((m = InHgRx().Match(t)).Success)
            {
                double hpa = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0 * 33.8639;
                lines.Add($"{indent}QNH {hpa.ToString("0", Fr)} hPa ({m.Groups[1].Value[..2]},{m.Groups[1].Value[2..]} inHg)");
            }
            else if ((m = TafTempRx().Match(t)).Success)
            {
                string kind = m.Groups[1].Value == "TX" ? "maximale" : "minimale";
                lines.Add($"{indent}Température {kind} {Temp(m.Groups[2].Value)} °C le {m.Groups[3].Value} à {m.Groups[4].Value}h UTC");
            }
            else if (Weather(t) is { } wx) lines.Add($"{indent}{wx}");
            else unknown.Add(t);
        }
        if (unknown.Count > 0) lines.Add($"{indent}Non décodé : {string.Join(" ", unknown)}");
    }

    private static string Wind(Match m)
    {
        string dir = m.Groups[1].Value;
        int speed = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        bool mps = m.Groups[5].Value == "MPS";
        double toKt = mps ? 1.94384 : 1;
        int kt = (int)Math.Round(speed * toKt);
        if (kt == 0) return "Vent : calme";
        string gust = m.Groups[4].Success && m.Groups[4].Value.Length > 0
            ? $", rafales à {(int)Math.Round(int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * toKt)} kt"
            : "";
        string from = dir == "VRB" ? "de direction variable" : $"du {dir}° ({Compass(int.Parse(dir, CultureInfo.InvariantCulture))})";
        return $"Vent {from}, {kt} kt ({(int)Math.Round(kt * 1.852)} km/h){gust}";
    }

    private static string Compass(int degrees)
    {
        string[] names = ["nord", "nord-nord-est", "nord-est", "est-nord-est", "est", "est-sud-est", "sud-est", "sud-sud-est",
            "sud", "sud-sud-ouest", "sud-ouest", "ouest-sud-ouest", "ouest", "ouest-nord-ouest", "nord-ouest", "nord-nord-ouest"];
        return names[(int)Math.Round(degrees % 360 / 22.5) % 16];
    }

    /// <summary>Visibilité courte pour une étiquette de carte : « &gt; 10 km », « 4,5 km », « 800 m ».</summary>
    public static string ShortVisibility(double meters) => meters switch
    {
        >= 10000 => "> 10 km",
        >= 1000 => $"{(meters / 1000.0).ToString("0.##", Fr)} km",
        _ => $"{(int)meters} m"
    };

    private static string Visibility(int meters) => meters switch
    {
        9999 => "10 km ou plus",
        >= 1000 => $"{(meters / 1000.0).ToString("0.##", Fr)} km",
        _ => $"{meters} m"
    };

    private static string Cloud(Match m, Elements state)
    {
        string amount = m.Groups[1].Value switch
        {
            "FEW" => "Quelques nuages (1 à 2/8)",
            "SCT" => "Nuages épars (3 à 4/8)",
            "BKN" => "Nuages fragmentés (5 à 7/8)",
            _ => "Ciel couvert (8/8)"
        };
        int ft = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 100;
        if (m.Groups[1].Value is "BKN" or "OVC") state.CeilingFt = Math.Min(state.CeilingFt ?? int.MaxValue, ft);
        string type = m.Groups[3].Value switch { "CB" => ", cumulonimbus", "TCU" => ", cumulus bourgeonnants", _ => "" };
        return $"{amount} à {ft} ft ({Metres(ft)} m) au-dessus du sol{type}";
    }

    private static int Metres(int feet) => (int)Math.Round(feet * 0.3048 / 10) * 10;

    private static string Temp(string value) =>
        value.StartsWith('M') ? "-" + int.Parse(value[1..], CultureInfo.InvariantCulture) : int.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> Descriptors = new()
    {
        ["MI"] = "mince nappe de", ["BC"] = "bancs de", ["PR"] = "partiel", ["DR"] = "chasse basse de",
        ["BL"] = "chasse haute de", ["SH"] = "averses de", ["TS"] = "orage", ["FZ"] = "verglaçant"
    };

    private static readonly Dictionary<string, string> Phenomena = new()
    {
        ["DZ"] = "bruine", ["RA"] = "pluie", ["SN"] = "neige", ["SG"] = "neige en grains", ["PL"] = "granules de glace",
        ["GR"] = "grêle", ["GS"] = "grésil", ["UP"] = "précipitations de type inconnu", ["BR"] = "brume",
        ["FG"] = "brouillard", ["FU"] = "fumée", ["VA"] = "cendres volcaniques", ["DU"] = "poussière", ["SA"] = "sable",
        ["HZ"] = "brume sèche", ["PO"] = "tourbillons de poussière ou de sable", ["SQ"] = "grains", ["FC"] = "trombe ou tornade",
        ["SS"] = "tempête de sable", ["DS"] = "tempête de poussière"
    };

    /// <summary>Phénomène météo : intensité, descripteur, puis un ou plusieurs phénomènes (+TSRA, -SHRA, FZFG, VCSH...).</summary>
    private static string? Weather(string token)
    {
        string rest = token;
        string intensity = "";
        if (rest.StartsWith('+')) { intensity = "forte"; rest = rest[1..]; }
        else if (rest.StartsWith('-')) { intensity = "faible"; rest = rest[1..]; }
        else if (rest.StartsWith("VC", StringComparison.Ordinal)) { intensity = "à proximité"; rest = rest[2..]; }
        if (rest.Length < 2 || rest.Length % 2 != 0) return null;

        var descriptors = new List<string>();
        var phenomena = new List<string>();
        for (int i = 0; i < rest.Length; i += 2)
        {
            string code = rest.Substring(i, 2);
            if (Descriptors.ContainsKey(code) && phenomena.Count == 0) descriptors.Add(code);
            else if (Phenomena.ContainsKey(code)) phenomena.Add(code);
            else return null;
        }
        if (phenomena.Count == 0 && !descriptors.Contains("TS") && !descriptors.Contains("SH")) return null;

        var parts = new List<string>();
        bool thunder = descriptors.Contains("TS"), showers = descriptors.Contains("SH"), freezing = descriptors.Contains("FZ");
        string names = string.Join(" et ", phenomena.Select(p => Phenomena[p]));
        if (thunder) parts.Add(phenomena.Count > 0 ? $"orage avec {names}" : "orage");
        else if (showers) parts.Add(names.Length > 0 ? $"averses de {names}" : "averses");
        else if (names.Length > 0) parts.Add(names);
        foreach (string d in descriptors.Where(d => d is not ("TS" or "SH" or "FZ")))
            parts.Insert(0, Descriptors[d] + (Descriptors[d].EndsWith("de", StringComparison.Ordinal) ? $" {names}" : ""));
        string text = string.Join(", ", parts);
        if (freezing) text += phenomena.Contains("FG") ? " givrant" : " verglaçant(e)";
        if (intensity.Length > 0) text += " " + intensity;
        return char.ToUpper(text[0], Fr) + text[1..];
    }

    // ---------- catégorie de vol ----------

    private static string? Category(Elements e)
    {
        if (e.VisibilityM is null && e.CeilingFt is null && !e.Cavok) return null;
        double vis = e.VisibilityM ?? 10000;
        int ceiling = e.CeilingFt ?? int.MaxValue;
        if (vis < 1600 || ceiling < 500) return "LIFR";
        if (vis < 4800 || ceiling < 1000) return "IFR";
        if (vis <= 8000 || ceiling <= 3000) return "MVFR";
        return "VFR";
    }

    private static string CategoryText(string category) => category switch
    {
        "VFR" => "VFR (bonnes conditions de vol à vue)",
        "MVFR" => "MVFR (vol à vue possible, conditions marginales)",
        "IFR" => "IFR (vol aux instruments : vol à vue déconseillé)",
        _ => "LIFR (très mauvaises conditions : vol à vue impossible)"
    };

    // ---------- dates ----------

    private static string Day(string token) =>
        $"le {token[..2]} à {token[2..4]}:{token[4..6]} UTC";

    private static string Period(string token)
    {
        static string Hour(string s) => s[2..] == "24" ? "24h" : $"{s[2..]}h";
        string a = token[..4], b = token[5..];
        return $"du {a[..2]} à {Hour(a)} au {b[..2]} à {Hour(b)} UTC";
    }

    private static List<string> Tokens(string raw) =>
        raw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(t => t != "=").ToList();

    // ---------- expressions ----------

    [GeneratedRegex(@"^\d{6}Z$")] private static partial Regex TimeRx();
    [GeneratedRegex(@"^\d{4}/\d{4}$")] private static partial Regex ValidityRx();
    [GeneratedRegex(@"^FM(\d{2})(\d{2})(\d{2})$")] private static partial Regex FromRx();
    [GeneratedRegex(@"^PROB(30|40)$")] private static partial Regex ProbRx();
    [GeneratedRegex(@"^(FM|TL|AT)(\d{4})$")] private static partial Regex TrendTimeRx();
    [GeneratedRegex(@"^(VRB|\d{3})(\d{2,3})(G(\d{2,3}))?(KT|MPS)$")] private static partial Regex WindRx();
    [GeneratedRegex(@"^(\d{3})V(\d{3})$")] private static partial Regex WindVarRx();
    [GeneratedRegex(@"^\d{4}(NDV)?$")] private static partial Regex VisRx();
    [GeneratedRegex(@"^R(\d{2}[LCR]?)/[PM]?(\d{4})")] private static partial Regex RvrRx();
    [GeneratedRegex(@"^(FEW|SCT|BKN|OVC)(\d{3})(CB|TCU)?$")] private static partial Regex CloudRx();
    [GeneratedRegex(@"^VV(\d{3}|///)$")] private static partial Regex VertRx();
    [GeneratedRegex(@"^(M?\d{2})/(M?\d{2})?$")] private static partial Regex TempRx();
    [GeneratedRegex(@"^Q(\d{4})$")] private static partial Regex QnhRx();
    [GeneratedRegex(@"^A(\d{4})$")] private static partial Regex InHgRx();
    [GeneratedRegex(@"^(TX|TN)(M?\d{2})/(\d{2})(\d{2})Z$")] private static partial Regex TafTempRx();
}
