namespace VFRNav.Core.Ifr;

/// <summary>
/// Texte d'une SID ou d'une STAR dans les listes de choix : l'identifiant suivi du point de sortie d'une SID (« AGOP6H → AGOPA »)
/// ou d'entrée d'une STAR (« NISA7R ← NISAR »), pour savoir où elle mène sans la choisir.
/// </summary>
public static class ProcedureLabel
{
    private const int MaxPoints = 3;
    private const string ExitArrow = " → ";
    private const string EntryArrow = " ← ";

    /// <summary>« IDENT → SORTIE » (SID) ou « IDENT ← ENTRÉE » (STAR), pour les variantes qui desservent la piste donnée (toutes si null).</summary>
    public static string For(IfrProcedure procedure, string? runway = null)
    {
        bool sid = procedure.Kind == ProcedureKind.Sid;
        var points = procedure.Variants
            .Where(v => v.AppliesTo(runway) && v.Legs.Count > 0)
            .Select(v => FixKey.Display(sid ? v.Legs[^1].Fix : v.Legs[0].Fix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (points.Count == 0) return procedure.Ident;
        string list = string.Join(", ", points.Take(MaxPoints)) + (points.Count > MaxPoints ? $" (+{points.Count - MaxPoints})" : "");
        return procedure.Ident + (sid ? ExitArrow : EntryArrow) + list;
    }

    /// <summary>Identifiant d'une entrée de liste (ce qui précède la flèche) ; le texte tel quel s'il n'en a pas.</summary>
    public static string Ident(string label)
    {
        int cut = label.IndexOfAny(['→', '←']);
        return (cut < 0 ? label : label[..cut]).Trim();
    }
}
