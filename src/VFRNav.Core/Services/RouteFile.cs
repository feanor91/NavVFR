using System.Text.Json;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public static class RouteFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(IEnumerable<Waypoint> route) => JsonSerializer.Serialize(route, Options);
    public static List<Waypoint> Deserialize(string json) => JsonSerializer.Deserialize<List<Waypoint>>(json) ?? [];
}
