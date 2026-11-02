namespace VFRNav.Core.Ifr;

/// <summary>
/// Données de DÉMONSTRATION pour essayer l'interface IFR avant d'avoir importé de vrais fichiers AIRAC.
/// Les terrains sont réels mais les points, routes et procédures (préfixe « TST ») sont inventés :
/// ils ne correspondent à rien d'officiel et ne doivent servir à aucune navigation.
/// </summary>
public static class IfrSampleData
{
    public const string Label = "DONNÉES DE TEST (fictives)";

    public static IfrNavData Create()
    {
        List<IfrFix> fixes =
        [
            new("LFPG", 49.0097, 2.5479, FixKind.Airport),
            new("LFMN", 43.6584, 7.2159, FixKind.Airport),
            new("LFLL", 45.7256, 5.0811, FixKind.Airport),
            new("LFBO", 43.6291, 1.3638, FixKind.Airport),

            // Départ de Paris
            new("TSTP1", 49.02, 2.15), new("TSTP3", 49.00, 2.95),
            new("TSTPA", 48.60, 2.40), new("TSTPB", 48.50, 3.40),
            // Axe nord-sud vers Lyon et la Côte d'Azur
            new("TSTDI", 46.50, 3.60), new("TSTLY", 45.40, 4.60),
            new("TSTNI", 44.20, 6.20), new("TSTAL", 43.90, 6.80), new("TSTM1", 43.75, 7.00),
            new("TSTLL", 45.70, 4.95),
            // Axe vers Toulouse
            new("TSTBO", 46.00, 1.60), new("TSTCH", 44.80, 1.40), new("TSTTL", 44.00, 1.30), new("TSTTO", 43.70, 1.35),
        ];

        List<AirwaySegment> segments =
        [
            new("UTA1", "TSTPA", "TSTDI"), new("UTA1", "TSTDI", "TSTLY"),
            new("UTA2", "TSTPB", "TSTDI"),
            new("UTA3", "TSTLY", "TSTNI"), new("UTA3", "TSTNI", "TSTAL"),
            new("UTA5", "TSTPA", "TSTBO"), new("UTA5", "TSTBO", "TSTCH"), new("UTA5", "TSTCH", "TSTTL"),
        ];

        ProcedureVariant Sid(string[] runways, params string[] legs) =>
            new(runways, legs.Select(l => new ProcedureLeg(l)).ToList());

        string[] west = ["26L", "26R"], east = ["08L", "08R"];

        List<IfrProcedure> procedures =
        [
            new(ProcedureKind.Sid, "LFPG", "TST1A", [Sid(west, "TSTP1", "TSTPA"), Sid(east, "TSTP3", "TSTPA")]),
            new(ProcedureKind.Sid, "LFPG", "TST2B", [Sid(west, "TSTP1", "TSTPB"), Sid(east, "TSTP3", "TSTPB")]),

            new(ProcedureKind.Star, "LFMN", "TST3N",
                [new ProcedureVariant(["04L", "04R"], [new ProcedureLeg("TSTAL", MaxAltFt: 9000), new ProcedureLeg("TSTM1", MaxAltFt: 4000)])]),
            new(ProcedureKind.Star, "LFLL", "TST1L",
                [new ProcedureVariant([], [new ProcedureLeg("TSTLY", MaxAltFt: 8000), new ProcedureLeg("TSTLL", MaxAltFt: 5000)])]),
            new(ProcedureKind.Star, "LFBO", "TST1T",
                [new ProcedureVariant([], [new ProcedureLeg("TSTTL", MaxAltFt: 7000), new ProcedureLeg("TSTTO", MaxAltFt: 4000)])]),
        ];

        // Date de cycle fictive : pas de faux avertissement de péremption pour des données qui n'en ont pas.
        return new IfrNavData(fixes, segments, procedures, "TEST");
    }
}
