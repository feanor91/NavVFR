using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VFRNav.Core.StormAtc;

/// <summary>Point de report VFR d'un aérodrome (relevé sur sa carte VAC par StormATC).</summary>
public sealed record ReportingPoint(string Name, string? SpokenFr, double? Lat, double? Lon, string? Location, string? Purpose)
{
    public string Label => SpokenFr is { Length: > 0 } s && !string.Equals(s, Name, StringComparison.OrdinalIgnoreCase) ? $"{Name} ({s})" : Name;
}

public sealed record ProcedureSection(string Title, IReadOnlyList<string> Lines);

/// <summary>Procédures VFR connues d'un aérodrome : points de report, circuit, arrivées et départs, notes.</summary>
public sealed record VfrProcedures(IReadOnlyList<ReportingPoint> ReportingPoints, IReadOnlyList<ProcedureSection> Sections)
{
    public bool Any => ReportingPoints.Count > 0 || Sections.Count > 0;

    public const string Source = "Texte du profil StormATC, relevé sur la carte VAC (colonne française quand elle a été relevée, sinon anglais). À vérifier sur la carte.";

    /// <summary>Lignes d'affichage : points de report puis sections.</summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string>();
        if (ReportingPoints.Count > 0)
        {
            lines.Add("Points de report VFR");
            foreach (var p in ReportingPoints)
            {
                var detail = new[] { p.Location, p.Purpose }.Where(x => !string.IsNullOrWhiteSpace(x));
                lines.Add($"  • {p.Label}" + (detail.Any() ? " : " + string.Join(" — ", detail) : ""));
            }
        }
        foreach (var s in Sections)
        {
            lines.Add(s.Title);
            lines.AddRange(s.Lines.Select(l => "  " + l));
        }
        return lines;
    }

    /// <summary>Résumé sur une ligne pour une fiche compacte (points de report et hauteur du circuit).</summary>
    public string? Summary
    {
        get
        {
            var parts = new List<string>();
            if (ReportingPoints.Count > 0) parts.Add("Points de report : " + string.Join(", ", ReportingPoints.Select(p => p.Name)));
            var circuit = Sections.FirstOrDefault(s => s.Title.StartsWith("Circuit", StringComparison.Ordinal))
                ?.Lines.FirstOrDefault(l => l.StartsWith("Hauteur du circuit (ft AMSL)", StringComparison.Ordinal));
            if (circuit is not null) parts.Add(circuit.Replace(" :", "").Replace("Hauteur du circuit (ft AMSL)", "Circuit à") + " ft AMSL");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>Lit les procédures VFR d'un profil StormATC. Les clés connues reçoivent un intitulé français, les autres sont affichées telles quelles.</summary>
public static partial class VfrProcedureReader
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        // circuit
        ["heightFeetAmsl"] = "Hauteur du circuit (ft AMSL)",
        ["heightFeetAgl"] = "Hauteur du circuit (ft AGL)",
        ["heightNote"] = "Hauteur",
        ["sideOfRunways"] = "Côté du circuit",
        ["sideBearingDegrees"] = "Direction du côté (°)",
        ["handNote"] = "Main et trajectoire",
        ["lowHeight"] = "Basse hauteur",
        ["restrictedWeekendsAndHolidays"] = "Week-ends et jours fériés",
        ["ulmCircuitsProhibited"] = "Circuits ULM interdits",
        ["landmark"] = "Repère visuel",
        ["description"] = "Description",
        ["use"] = "Usage",
        ["trainingPatterns"] = "Tours de piste d'entraînement",
        ["overheadTransit"] = "Transit à la verticale",
        ["restrictions"] = "Restrictions",
        ["silencePeriods"] = "Périodes de silence",
        ["baseAndCrosswind"] = "Étape de base et vent traversier",
        ["listenBeforeJoining"] = "Avant d'intégrer le circuit",
        // arrivées et départs
        ["arrivals"] = "Arrivées",
        ["departures"] = "Départs",
        ["radio"] = "Radio",
        ["arrival"] = "Arrivée",
        ["departure"] = "Départ",
        ["from"] = "Depuis",
        ["via"] = "Par le point",
        ["altitudeFeetAmsl"] = "Altitude (ft AMSL)",
        ["instruction"] = "Consigne",
        ["note"] = "Note",
        ["runway"] = "Piste",
        ["climbToFeetAmsl"] = "Monter à (ft AMSL)",
        ["inCircuit"] = "Dans le circuit",
        ["overheadDepartureOnRequest"] = "Départ par la verticale sur demande",
        ["arrivalUnlessImpossible"] = "Arrivée, sauf impossibilité",
        ["compulsoryPoint"] = "Point obligatoire",
        ["runwayUnknownAltitudeFeetAmsl"] = "Altitude si la piste en service est inconnue (ft AMSL)",
        ["particularTrainingProhibited"] = "Entraînement particulier interdit",
        ["localFlightsUnder20MinutesProhibited"] = "Vols locaux de moins de 20 min interdits",
        ["contactAirAirMinutesBefore"] = "Contact air-air (minutes avant)",
        ["contactBefore"] = "Contact avant",
        ["enterFrom"] = "Entrée par",
        ["maxAircraftInCircuit"] = "Avions maximum dans le circuit",
        ["rmzActive"] = "RMZ active",
        ["afterHalfDownwind"] = "Après la mi-vent arrière",
        // grands aérodromes (StormATC 0.26 et suivantes)
        ["routes"] = "Itinéraires VFR",
        ["nightVfr"] = "VFR de nuit",
        ["specialVfr"] = "VFR spécial",
        ["transit"] = "Transit",
        ["waiting"] = "Attente",
        ["southCircuit"] = "Circuit sud",
        ["axisCrossing"] = "Traversée des axes de piste",
        ["radioFailureOnArrival"] = "Panne radio à l'arrivée",
        ["weekendsAndHolidays"] = "Week-ends et jours fériés",
        ["lowHeightPattern"] = "Circuit à basse hauteur",
        ["lowHeightTraining"] = "Basse hauteur, entraînement",
        ["specialExercises"] = "Exercices particuliers",
        ["entry"] = "Entrée",
        ["exit"] = "Sortie",
        ["contact"] = "Contact",
        ["route"] = "Itinéraire",
        ["tracks"] = "Tracés",
        ["legend"] = "Légende",
        ["usage"] = "Usage",
        ["altitudeNote"] = "Note d'altitude",
    };

    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = "nord", ["south"] = "sud", ["east"] = "est", ["west"] = "ouest",
        ["northeast"] = "nord-est", ["northwest"] = "nord-ouest", ["southeast"] = "sud-est", ["southwest"] = "sud-ouest",
    };

    /// <summary>Procédures du profil, ou null s'il n'y en a aucune.</summary>
    public static VfrProcedures? Read(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object) return null;
        profile = WithFrench(profile);

        var points = new List<ReportingPoint>();
        if (profile.TryGetProperty("reportingPoints", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                string? name = Str(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string? spoken = item.TryGetProperty("spoken", out var sp) && sp.ValueKind == JsonValueKind.Object ? Str(sp, "fr") : null;
                points.Add(new ReportingPoint(name.Trim(), spoken, Num(item, "latitude"), Num(item, "longitude"), Str(item, "location"), Str(item, "purpose")));
            }

        var sections = new List<ProcedureSection>();
        if (profile.TryGetProperty("preferredQfu", out var qfu) && qfu.ValueKind == JsonValueKind.Number)
        {
            string reason = Str(profile, "preferredQfuReason") is { Length: > 0 } r ? $" — {r}" : "";
            sections.Add(new("Piste préférentielle", [$"QFU {qfu.GetInt32():000}{reason}"]));
        }
        AddObjectSection(sections, profile, "circuit", "Circuit d'aérodrome");
        AddObjectSection(sections, profile, "withAts", "Arrivées et départs, avec contrôle (ATS)");
        AddObjectSection(sections, profile, "withoutAts", "Arrivées et départs, sans contrôle");
        if (profile.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
        {
            var lines = notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => "• " + n.GetString()).ToList();
            if (lines.Count > 0) sections.Add(new("Notes du profil", lines));
        }

        var result = new VfrProcedures(points, sections);
        return result.Any ? result : null;
    }

    /// <summary>
    /// Le profil avec son français par-dessus l'anglais : l'objet <c>fr</c> reprend l'arborescence de <c>circuit</c>, <c>withAts</c> et
    /// <c>withoutAts</c> (textes seulement) et la liste <c>notes</c> (une chaîne vide = pas de français pour cette note). Ce qui n'a pas de
    /// français reste en anglais.
    /// </summary>
    public static JsonElement WithFrench(JsonElement profile)
    {
        if (!profile.TryGetProperty("fr", out var fr) || fr.ValueKind != JsonValueKind.Object) return profile;
        if (JsonNode.Parse(profile.GetRawText()) is not JsonObject root) return profile;
        foreach (var key in new[] { "circuit", "withAts", "withoutAts" })
            if (fr.TryGetProperty(key, out var part) && root[key] is JsonNode english && JsonNode.Parse(part.GetRawText()) is JsonNode french)
                (Overlay(english, french) is { } replacement ? () => root[key] = replacement : (Action)(() => { }))();
        if (fr.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array && root["notes"] is JsonArray englishNotes)
        {
            int i = 0;
            foreach (var note in notes.EnumerateArray())
            {
                if (i < englishNotes.Count && note.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(note.GetString()))
                    englishNotes[i] = note.GetString();
                i++;
            }
        }
        root.Remove("fr");
        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Superpose le français : les objets et les listes sont modifiés sur place (retourne null), un texte est remplacé (retourne le nouveau).</summary>
    private static JsonNode? Overlay(JsonNode english, JsonNode french)
    {
        switch (english, french)
        {
            case (JsonObject e, JsonObject f):
                foreach (var (key, value) in f.ToList())
                {
                    if (value is null) continue;
                    if (e[key] is JsonNode existing)
                    {
                        if (Overlay(existing, value) is { } replacement) e[key] = replacement;
                    }
                    else e[key] = value.DeepClone();
                }
                return null;
            case (JsonArray e, JsonArray f):
                for (int i = 0; i < f.Count && i < e.Count; i++)
                    if (f[i] is JsonNode value && e[i] is JsonNode existing && Overlay(existing, value) is { } replacement)
                        e[i] = replacement;
                return null;
            case (JsonValue e, JsonValue f) when e.GetValueKind() == JsonValueKind.String && f.GetValueKind() == JsonValueKind.String:
                return f.DeepClone();
            default:
                return null;
        }
    }

    private static void AddObjectSection(List<ProcedureSection> sections, JsonElement profile, string key, string title)
    {
        if (!profile.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return;
        var lines = new List<string>();
        Render(value, lines, 0);
        if (lines.Count > 0) sections.Add(new(title, lines));
    }

    private static void Render(JsonElement obj, List<string> lines, int depth)
    {
        string pad = new(' ', depth * 2);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.StartsWith('$')) continue;
            string label = Label(prop.Name);
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    lines.Add($"{pad}{label} :");
                    Render(prop.Value, lines, depth + 1);
                    break;
                case JsonValueKind.Array:
                    RenderArray(label, prop.Value, lines, pad);
                    break;
                default:
                    lines.Add($"{pad}{label} : {Scalar(prop.Value)}");
                    break;
            }
        }
    }

    private static void RenderArray(string label, JsonElement array, List<string> lines, string pad)
    {
        var items = array.EnumerateArray().ToList();
        if (items.Count == 0) return;
        if (items.All(i => i.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
        {
            lines.Add($"{pad}{label} : {string.Join(", ", items.Select(Scalar))}");
            return;
        }
        lines.Add($"{pad}{label} :");
        foreach (var item in items)
            lines.Add($"{pad}  • " + (item.ValueKind == JsonValueKind.Object ? Inline(item) : Scalar(item)));
    }

    /// <summary>Un élément de liste sur une seule ligne : « Par le point : S · Altitude (ft AMSL) : 1300 · Consigne : ... ».</summary>
    private static string Inline(JsonElement obj) =>
        string.Join(" · ", obj.EnumerateObject().Where(p => !p.Name.StartsWith('$')).Select(p =>
            $"{Label(p.Name)} : " + p.Value.ValueKind switch
            {
                JsonValueKind.Object => "(" + Inline(p.Value) + ")",
                JsonValueKind.Array => string.Join(p.Value.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object) ? " ; " : ", ",
                    p.Value.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.Object ? "(" + Inline(i) + ")" : Scalar(i))),
                _ => Scalar(p.Value)
            }));

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => "oui",
        JsonValueKind.False => "non",
        JsonValueKind.Number => e.GetDouble().ToString("0.##", CultureInfo.InvariantCulture),
        JsonValueKind.String => Values.TryGetValue(e.GetString() ?? "", out var v) ? v : e.GetString() ?? "",
        JsonValueKind.Object => "(" + Inline(e) + ")",
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Scalar)),
        _ => e.ToString()
    };

    /// <summary>Intitulé français, sinon la clé découpée en mots (« runway26 » devient « runway 26 »).</summary>
    public static string Label(string key) =>
        Labels.TryGetValue(key, out var label) ? label : CamelRx().Replace(key, " ").Trim().ToLowerInvariant() is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : key;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])")]
    private static partial Regex CamelRx();
}
