using System.Globalization;

namespace VFRNav.Core.Models;

public enum NavaidKind { Vor, Ndb }

/// <summary>Radiobalise : VOR (VOR, VOR-DME, VORTAC) ou NDB / ADF (NDB, NDB-DME).</summary>
/// <param name="FrequencyKhz">Fréquence en kHz telle que publiée (113500 pour un VOR, 373 pour un NDB).</param>
public sealed record Navaid(string Ident, string Name, NavaidKind Kind, string Type, double FrequencyKhz, double Lat, double Lon, double ElevationFt)
{
    /// <summary>« 113,500 MHz » pour un VOR, « 373 kHz » pour un NDB.</summary>
    public string FrequencyText => Kind == NavaidKind.Vor
        ? $"{(FrequencyKhz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)} MHz"
        : $"{FrequencyKhz.ToString("0.#", CultureInfo.InvariantCulture)} kHz";

    public string Label => Kind == NavaidKind.Vor ? "VOR" : "NDB";

    /// <summary>Point de route portant le nom de la balise.</summary>
    public Waypoint ToWaypoint() => new() { Name = Ident, FullName = $"{Name} ({Type} {FrequencyText})", ElevationFt = ElevationFt, Lat = Lat, Lon = Lon };
}
