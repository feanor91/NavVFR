using System.Globalization;
using System.Text.RegularExpressions;
using VFRNav.Core.Localization;

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
    private static CultureInfo Fr => VFRNav.Core.Localization.Loc.FormatCulture;

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
            if (t == "AUTO") lines.Add(Loc.Get("WeatherDecoder_Observation_automatique_sans_observateur"));
            else if (t == "COR") lines.Add(Loc.Get("WeatherDecoder_Message_corrige"));
            else if (t == "NIL") lines.Add(Loc.Get("WeatherDecoder_Aucune_observation_disponible"));
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
        if (category is not null) lines.Add(Loc.F("WeatherDecoder_Categorie_de_vol", CategoryText(category)));
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
            if (t == "NOSIG") { Flush(); label = null; lines.Add(Loc.Get("WeatherDecoder_Tendance_pas_de_changement_significatif")); }
            else if (t == "BECMG") { Flush(); label = Loc.Get("WeatherDecoder_Tendance_evolution_durable_vers"); }
            else if (t == "TEMPO") { Flush(); label = Loc.Get("WeatherDecoder_Tendance_variations_temporaires"); }
            else if (TrendTimeRx().IsMatch(t))
            {
                var m = TrendTimeRx().Match(t);
                string kind = m.Groups[1].Value switch { "FM" => Loc.Get("WeatherDecoder_a_partir_de"), "TL" => Loc.Get("WeatherDecoder_jusqu_a"), _ => Loc.Get("WeatherDecoder_vers") };
                label = (label ?? Loc.Get("WeatherDecoder_Tendance")) + $" ({kind} {m.Groups[2].Value[..2]}:{m.Groups[2].Value[2..]} UTC)";
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
                "AMD" => Loc.Get("WeatherDecoder_Prevision_modifiee_amendee"),
                "COR" => Loc.Get("WeatherDecoder_Prevision_corrigee"),
                "CNL" => Loc.Get("WeatherDecoder_Prevision_annulee"),
                _ => Loc.Get("WeatherDecoder_Aucune_prevision_disponible")
            });
            i++;
        }
        string station = i < tokens.Count ? tokens[i++] : "";
        string? issued = i < tokens.Count && TimeRx().IsMatch(tokens[i]) ? Day(tokens[i++]) : null;
        string? validity = i < tokens.Count && ValidityRx().IsMatch(tokens[i]) ? Period(tokens[i++]) : null;
        if (validity is not null) lines.Add(Loc.F("WeatherDecoder_Valable", validity));

        var group = new List<string>();
        string label = Loc.Get("WeatherDecoder_Prevision_de_base");

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
                label = Loc.F("WeatherDecoder_A_partir_du_a_UTC", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            }
            else if (t is "BECMG" or "TEMPO" || ProbRx().IsMatch(t))
            {
                Flush();
                string prob = "";
                string kind = t;
                if (ProbRx().IsMatch(t))
                {
                    prob = Loc.F("WeatherDecoder_Probabilite", t[4..]);
                    if (i + 1 < tokens.Count && tokens[i + 1] == "TEMPO") { i++; kind = "TEMPO"; } else kind = "";
                }
                string when = i + 1 < tokens.Count && ValidityRx().IsMatch(tokens[i + 1]) ? " " + Period(tokens[++i]) : "";
                label = kind switch
                {
                    "BECMG" => Loc.F("WeatherDecoder_Evolution_progressive", when),
                    "TEMPO" => Loc.F("WeatherDecoder_Variations_temporaires", prob, when),
                    _ => Loc.F("WeatherDecoder_d_un_phenomene", prob, when)
                };
            }
            else group.Add(t);
        }
        Flush();
        if (lines.Count > 0 && lines[^1].StartsWith(Loc.Get("WeatherDecoder_Prevision_de_base"), StringComparison.Ordinal) && group.Count == 0)
            lines.RemoveAt(lines.Count - 1);

        string when2 = issued is null ? "" : Loc.F("WeatherDecoder_emis", issued);
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
                lines.Add(Loc.F("WeatherDecoder_Visibilite_10_km_ou_plus", indent));
            }
            else if (t == "NSW") lines.Add(Loc.F("WeatherDecoder_Fin_des_phenomenes_meteo_significatifs", indent));
            else if ((m = WindRx().Match(t)).Success) lines.Add(indent + Wind(m));
            else if ((m = WindVarRx().Match(t)).Success) lines.Add(Loc.F("WeatherDecoder_Direction_du_vent_variable_entre", indent, m.Groups[1].Value, m.Groups[2].Value));
            else if (VisRx().IsMatch(t))
            {
                int v = int.Parse(t[..4], CultureInfo.InvariantCulture);
                state.VisibilityM = v == 9999 ? 10000 : v;
                lines.Add(Loc.F("WeatherDecoder_Visibilite", indent, Visibility(v)));
            }
            else if ((m = RvrRx().Match(t)).Success)
                lines.Add(Loc.F("WeatherDecoder_Portee_visuelle_de_piste_m", indent, m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            else if ((m = CloudRx().Match(t)).Success) lines.Add(indent + Cloud(m, state));
            else if ((m = VertRx().Match(t)).Success)
            {
                if (int.TryParse(m.Groups[1].Value, out int h))
                {
                    state.CeilingFt = Math.Min(state.CeilingFt ?? int.MaxValue, h * 100);
                    lines.Add(Loc.F("WeatherDecoder_Ciel_invisible_visibilite_verticale_ft", indent, h * 100, Metres(h * 100)));
                }
                else lines.Add(Loc.F("WeatherDecoder_Ciel_invisible_visibilite_verticale_non", indent));
            }
            else if (t is "SKC" or "CLR") lines.Add(Loc.F("WeatherDecoder_Ciel_degage", indent));
            else if (t is "NSC") lines.Add(Loc.F("WeatherDecoder_Pas_de_nuage_significatif", indent));
            else if (t is "NCD") lines.Add(Loc.F("WeatherDecoder_Aucun_nuage_detecte_station_automatique", indent));
            else if ((m = TempRx().Match(t)).Success)
            {
                string temp = Loc.F("WeatherDecoder_temperature_C", Temp(m.Groups[1].Value));
                lines.Add(m.Groups[2].Success && m.Groups[2].Value.Length > 0
                    ? Loc.F("WeatherDecoder_Temperature_C_point_de_rosee", indent, Temp(m.Groups[1].Value), Temp(m.Groups[2].Value))
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
                string kind = m.Groups[1].Value == "TX" ? Loc.Get("WeatherDecoder_maximale") : Loc.Get("WeatherDecoder_minimale");
                lines.Add(Loc.F("WeatherDecoder_Temperature_C_le_a_h", indent, kind, Temp(m.Groups[2].Value), m.Groups[3].Value, m.Groups[4].Value));
            }
            else if (Weather(t) is { } wx) lines.Add($"{indent}{wx}");
            else unknown.Add(t);
        }
        if (unknown.Count > 0) lines.Add(Loc.F("WeatherDecoder_Non_decode", indent, string.Join(" ", unknown)));
    }

    private static string Wind(Match m)
    {
        string dir = m.Groups[1].Value;
        int speed = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        bool mps = m.Groups[5].Value == "MPS";
        double toKt = mps ? 1.94384 : 1;
        int kt = (int)Math.Round(speed * toKt);
        if (kt == 0) return Loc.Get("WeatherDecoder_Vent_calme");
        string gust = m.Groups[4].Success && m.Groups[4].Value.Length > 0
            ? Loc.F("WeatherDecoder_rafales_a_kt", (int)Math.Round(int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * toKt))
            : "";
        string from = dir == "VRB" ? Loc.Get("WeatherDecoder_de_direction_variable") : Loc.F("WeatherDecoder_Wind_from", dir, Compass(int.Parse(dir, CultureInfo.InvariantCulture)));
        return Loc.F("WeatherDecoder_Vent_kt_km_h", from, kt, (int)Math.Round(kt * 1.852), gust);
    }

    private static string Compass(int degrees)
    {
        string[] names = [Loc.Get("WeatherDecoder_nord"), Loc.Get("WeatherDecoder_nord_nord_est"), Loc.Get("WeatherDecoder_nord_est"), Loc.Get("WeatherDecoder_est_nord_est"), Loc.Get("WeatherDecoder_est"), Loc.Get("WeatherDecoder_est_sud_est"), Loc.Get("WeatherDecoder_sud_est"), Loc.Get("WeatherDecoder_sud_sud_est"),
            Loc.Get("WeatherDecoder_sud"), Loc.Get("WeatherDecoder_sud_sud_ouest"), Loc.Get("WeatherDecoder_sud_ouest"), Loc.Get("WeatherDecoder_ouest_sud_ouest"), Loc.Get("WeatherDecoder_ouest"), Loc.Get("WeatherDecoder_ouest_nord_ouest"), Loc.Get("WeatherDecoder_nord_ouest"), Loc.Get("WeatherDecoder_nord_nord_ouest")];
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
        9999 => Loc.Get("WeatherDecoder_10_km_ou_plus"),
        >= 1000 => $"{(meters / 1000.0).ToString("0.##", Fr)} km",
        _ => $"{meters} m"
    };

    private static string Cloud(Match m, Elements state)
    {
        string amount = m.Groups[1].Value switch
        {
            "FEW" => Loc.Get("WeatherDecoder_Quelques_nuages_1_a_2"),
            "SCT" => Loc.Get("WeatherDecoder_Nuages_epars_3_a_4"),
            "BKN" => Loc.Get("WeatherDecoder_Nuages_fragmentes_5_a_7"),
            _ => Loc.Get("WeatherDecoder_Ciel_couvert_8_8")
        };
        int ft = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 100;
        if (m.Groups[1].Value is "BKN" or "OVC") state.CeilingFt = Math.Min(state.CeilingFt ?? int.MaxValue, ft);
        string type = m.Groups[3].Value switch { "CB" => Loc.Get("WeatherDecoder_cumulonimbus"), "TCU" => Loc.Get("WeatherDecoder_cumulus_bourgeonnants"), _ => "" };
        return Loc.F("WeatherDecoder_a_ft_m_au_dessus", amount, ft, Metres(ft), type);
    }

    private static int Metres(int feet) => (int)Math.Round(feet * 0.3048 / 10) * 10;

    private static string Temp(string value) =>
        value.StartsWith('M') ? "-" + int.Parse(value[1..], CultureInfo.InvariantCulture) : int.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static readonly Localized<Dictionary<string, string>> DescriptorsByLanguage = new(() => new()
    {
        ["MI"] = Loc.Get("WeatherDecoder_mince_nappe_de"), ["BC"] = Loc.Get("WeatherDecoder_bancs_de"), ["PR"] = Loc.Get("WeatherDecoder_partiel"), ["DR"] = Loc.Get("WeatherDecoder_chasse_basse_de"),
        ["BL"] = Loc.Get("WeatherDecoder_chasse_haute_de"), ["SH"] = Loc.Get("WeatherDecoder_averses_de"), ["TS"] = Loc.Get("WeatherDecoder_orage"), ["FZ"] = Loc.Get("WeatherDecoder_verglacant")
    });
    private static Dictionary<string, string> Descriptors => DescriptorsByLanguage.Value;

    private static readonly Localized<Dictionary<string, string>> PhenomenaByLanguage = new(() => new()
    {
        ["DZ"] = Loc.Get("WeatherDecoder_bruine"), ["RA"] = Loc.Get("WeatherDecoder_pluie"), ["SN"] = Loc.Get("WeatherDecoder_neige"), ["SG"] = Loc.Get("WeatherDecoder_neige_en_grains"), ["PL"] = Loc.Get("WeatherDecoder_granules_de_glace"),
        ["GR"] = Loc.Get("WeatherDecoder_hail"), ["GS"] = Loc.Get("WeatherDecoder_gresil"), ["UP"] = Loc.Get("WeatherDecoder_precipitations_de_type_inconnu"), ["BR"] = Loc.Get("WeatherDecoder_brume"),
        ["FG"] = Loc.Get("WeatherDecoder_brouillard"), ["FU"] = Loc.Get("WeatherDecoder_fumee"), ["VA"] = Loc.Get("WeatherDecoder_cendres_volcaniques"), ["DU"] = Loc.Get("WeatherDecoder_poussiere"), ["SA"] = Loc.Get("WeatherDecoder_sable"),
        ["HZ"] = Loc.Get("WeatherDecoder_brume_seche"), ["PO"] = Loc.Get("WeatherDecoder_tourbillons_de_poussiere_ou_de"), ["SQ"] = Loc.Get("WeatherDecoder_grains"), ["FC"] = Loc.Get("WeatherDecoder_trombe_ou_tornade"),
        ["SS"] = Loc.Get("WeatherDecoder_tempete_de_sable"), ["DS"] = Loc.Get("WeatherDecoder_tempete_de_poussiere")
    });
    private static Dictionary<string, string> Phenomena => PhenomenaByLanguage.Value;

    /// <summary>Phénomène météo : intensité, descripteur, puis un ou plusieurs phénomènes (+TSRA, -SHRA, FZFG, VCSH...).</summary>
    private static string? Weather(string token)
    {
        string rest = token;
        string intensity = "";
        if (rest.StartsWith('+')) { intensity = Loc.Get("WeatherDecoder_forte"); rest = rest[1..]; }
        else if (rest.StartsWith('-')) { intensity = Loc.Get("WeatherDecoder_faible"); rest = rest[1..]; }
        else if (rest.StartsWith("VC", StringComparison.Ordinal)) { intensity = Loc.Get("WeatherDecoder_a_proximite"); rest = rest[2..]; }
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
        string names = string.Join(Loc.Get("Common_And"), phenomena.Select(p => Phenomena[p]));
        if (thunder) parts.Add(phenomena.Count > 0 ? Loc.F("WeatherDecoder_orage_avec", names) : Loc.Get("WeatherDecoder_orage"));
        else if (showers) parts.Add(names.Length > 0 ? Loc.F("WeatherDecoder_averses_de_2", names) : Loc.Get("WeatherDecoder_averses"));
        else if (names.Length > 0) parts.Add(names);
        foreach (string d in descriptors.Where(d => d is not ("TS" or "SH" or "FZ")))
            parts.Insert(0, Descriptors[d] + (d is "MI" or "BC" or "DR" or "BL" ? $" {names}" : ""));
        string text = string.Join(", ", parts);
        if (freezing) text = Loc.F(phenomena.Contains("FG") ? "WeatherDecoder_Freezing_fog" : "WeatherDecoder_Freezing", text);
        if (intensity.Length > 0) text = Loc.F("WeatherDecoder_With_intensity", text, intensity);
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
        "VFR" => Loc.Get("WeatherDecoder_VFR_bonnes_conditions_de_vol"),
        "MVFR" => Loc.Get("WeatherDecoder_MVFR_vol_a_vue_possible"),
        "IFR" => Loc.Get("WeatherDecoder_IFR_vol_aux_instruments_vol"),
        _ => Loc.Get("WeatherDecoder_LIFR_tres_mauvaises_conditions_vol")
    };

    // ---------- dates ----------

    private static string Day(string token) =>
        Loc.F("WeatherDecoder_le_a_UTC", token[..2], token[2..4], token[4..6]);

    private static string Period(string token)
    {
        static string Hour(string s) => s[2..] == "24" ? "24h" : $"{s[2..]}h";
        string a = token[..4], b = token[5..];
        return Loc.F("WeatherDecoder_du_a_au_a_UTC", a[..2], Hour(a), b[..2], Hour(b));
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
