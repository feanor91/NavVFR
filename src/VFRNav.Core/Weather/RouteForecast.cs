using System.Globalization;
using System.Text.Json;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Weather;

/// <summary>Une demande de prévision : un point de la route à l'heure où l'on doit y passer.</summary>
public sealed record ForecastRequest(double Lat, double Lon, DateTimeOffset Eta);

/// <summary>Vent et température à un niveau de pression, avec l'altitude (au-dessus du niveau de la mer) de ce niveau.</summary>
public sealed record ForecastLevel(int Hpa, double HeightFt, double WindDir, double WindKt, double TempC);

/// <summary>Vent et température à une altitude.</summary>
public sealed record AltitudeWeather(double WindDir, double WindKt, double TempC);

/// <summary>Prévision d'Open-Meteo pour un point de la route, à l'heure la plus proche de l'heure de passage.</summary>
public sealed record PointForecast(
    DateTimeOffset Time, double ElevationFt,
    double SurfaceWindDir, double SurfaceWindKt, double GustKt, double SurfaceTempC,
    double PrecipMm, int CloudLow, int CloudMid, int CloudHigh,
    double? VisibilityM, double? FreezingLevelFt, double? Cape, int WeatherCode,
    IReadOnlyList<ForecastLevel> Levels)
{
    /// <summary>
    /// Vent et température à l'altitude donnée : interpolés entre les niveaux de pression qui l'encadrent (le vent est interpolé par composantes,
    /// pas par angle), avec le vent de surface comme niveau le plus bas.
    /// </summary>
    public AltitudeWeather At(double altitudeFt)
    {
        var points = new List<(double Height, double U, double V, double Temp)> { Vector(ElevationFt + 33, SurfaceWindDir, SurfaceWindKt, SurfaceTempC) };
        foreach (var l in Levels.OrderBy(l => l.HeightFt))
            if (l.HeightFt > points[0].Height + 1) points.Add(Vector(l.HeightFt, l.WindDir, l.WindKt, l.TempC));
        points.Sort((a, b) => a.Height.CompareTo(b.Height));

        if (altitudeFt <= points[0].Height) return ToWeather(points[0].U, points[0].V, points[0].Temp);
        if (altitudeFt >= points[^1].Height) return ToWeather(points[^1].U, points[^1].V, points[^1].Temp);
        for (int i = 1; i < points.Count; i++)
        {
            if (altitudeFt > points[i].Height) continue;
            var (h0, u0, v0, t0) = points[i - 1];
            var (h1, u1, v1, t1) = points[i];
            double f = (altitudeFt - h0) / (h1 - h0);
            return ToWeather(u0 + (u1 - u0) * f, v0 + (v1 - v0) * f, t0 + (t1 - t0) * f);
        }
        return ToWeather(points[^1].U, points[^1].V, points[^1].Temp);
    }

    private static (double Height, double U, double V, double Temp) Vector(double height, double dir, double kt, double temp)
    {
        double rad = dir * Math.PI / 180;
        return (height, -kt * Math.Sin(rad), -kt * Math.Cos(rad), temp);   // le vent vient de « dir » : il souffle vers dir + 180°
    }

    private static AltitudeWeather ToWeather(double u, double v, double temp)
    {
        double kt = Math.Sqrt(u * u + v * v);
        double dir = kt < 0.01 ? 0 : (Math.Atan2(-u, -v) * 180 / Math.PI + 360) % 360;
        return new AltitudeWeather(dir, kt, temp);
    }
}

public enum ForecastAdvisoryKind { Thunderstorm, Icing, HeavyPrecipitation, LowVisibility, Fog, StrongWind }

public sealed record ForecastAdvisory(ForecastAdvisoryKind Kind)
{
    public string Text => Loc.Get(Kind switch
    {
        ForecastAdvisoryKind.Thunderstorm => "Forecast_Advisory_Thunderstorm",
        ForecastAdvisoryKind.Icing => "Forecast_Advisory_Icing",
        ForecastAdvisoryKind.HeavyPrecipitation => "Forecast_Advisory_HeavyPrecipitation",
        ForecastAdvisoryKind.LowVisibility => "Forecast_Advisory_LowVisibility",
        ForecastAdvisoryKind.Fog => "Forecast_Advisory_Fog",
        _ => "Forecast_Advisory_StrongWind",
    });
}

/// <summary>Décode un point de prévision : texte du temps, alertes pour la navigation.</summary>
public static class ForecastAssessment
{
    public const double ThunderstormCape = 800;      // J/kg : convection assez forte pour des orages
    public const double HeavyPrecipMm = 2.5;         // mm par heure
    public const double LowVisibilityM = 5000;
    public const double StrongGustKt = 30;

    /// <summary>Alertes pour un vol à <paramref name="altitudeFt"/> : orage, givrage, pluie forte, visibilité, brouillard, rafales.</summary>
    public static IReadOnlyList<ForecastAdvisory> Advisories(PointForecast f, double altitudeFt)
    {
        var list = new List<ForecastAdvisory>();
        if (f.Cape >= ThunderstormCape || f.WeatherCode is >= 95 and <= 99) list.Add(new(ForecastAdvisoryKind.Thunderstorm));

        // Givrage : en nuage ou sous la pluie avec une température proche de zéro ou négative (jusqu'à -20 °C) à l'altitude du vol.
        double temp = f.At(altitudeFt).TempC;
        bool inCloudOrPrecip = f.PrecipMm > 0.1 || f.CloudLow >= 50 || f.CloudMid >= 50;
        bool freezingLevelAbove = f.FreezingLevelFt is { } z && z < altitudeFt + 3000;
        if (inCloudOrPrecip && temp is <= 2 and >= -20 && (temp <= 0 || freezingLevelAbove)) list.Add(new(ForecastAdvisoryKind.Icing));

        if (f.PrecipMm >= HeavyPrecipMm) list.Add(new(ForecastAdvisoryKind.HeavyPrecipitation));
        if (f.VisibilityM is < LowVisibilityM) list.Add(new(ForecastAdvisoryKind.LowVisibility));
        if (f.WeatherCode is 45 or 48) list.Add(new(ForecastAdvisoryKind.Fog));
        if (f.GustKt >= StrongGustKt) list.Add(new(ForecastAdvisoryKind.StrongWind));
        return list;
    }

    /// <summary>Texte du code « temps présent » de l'OMM, regroupé par famille.</summary>
    public static string WeatherText(int code) => Loc.Get(code switch
    {
        0 => "Forecast_Code_Clear",
        1 or 2 => "Forecast_Code_PartlyCloudy",
        3 => "Forecast_Code_Overcast",
        45 or 48 => "Forecast_Code_Fog",
        >= 51 and <= 57 => "Forecast_Code_Drizzle",
        >= 61 and <= 67 => "Forecast_Code_Rain",
        >= 71 and <= 77 => "Forecast_Code_Snow",
        >= 80 and <= 82 => "Forecast_Code_Showers",
        85 or 86 => "Forecast_Code_SnowShowers",
        >= 95 and <= 99 => "Forecast_Code_Thunderstorm",
        _ => "Forecast_Code_Unknown",
    });
}

/// <summary>Lecture de la réponse d'Open-Meteo (API de prévision, plusieurs points à la fois).</summary>
public static class OpenMeteoForecast
{
    public const string Endpoint = "https://api.open-meteo.com/v1/forecast";
    public const string AttributionText = "Weather data by Open-Meteo.com";
    public const string AttributionUrl = "https://open-meteo.com";

    /// <summary>Niveaux de pression lus (hPa), du plus bas au plus haut : environ 2 500 ft à FL390.</summary>
    public static readonly int[] PressureLevels = [925, 850, 700, 600, 500, 400, 300, 250, 200];

    private const double MetresToFeet = 3.28084;

    public static string HourlyVariables { get; } = string.Join(",",
        new[]
        {
            "temperature_2m", "precipitation", "weather_code", "cloud_cover_low", "cloud_cover_mid", "cloud_cover_high", "visibility",
            "wind_speed_10m", "wind_direction_10m", "wind_gusts_10m", "freezing_level_height", "cape",
        }.Concat(PressureLevels.SelectMany(p => new[]
        {
            $"wind_speed_{p}hPa", $"wind_direction_{p}hPa", $"temperature_{p}hPa", $"geopotential_height_{p}hPa",
        })));

    /// <summary>Adresse de la requête pour des points dont on connaît les heures de passage (dates UTC min à max).</summary>
    public static string Url(IReadOnlyList<ForecastRequest> points)
    {
        var inv = CultureInfo.InvariantCulture;
        var first = points.Min(p => p.Eta).UtcDateTime.Date;
        var last = points.Max(p => p.Eta).UtcDateTime.Date;
        return Endpoint
            + "?latitude=" + string.Join(",", points.Select(p => p.Lat.ToString("0.####", inv)))
            + "&longitude=" + string.Join(",", points.Select(p => p.Lon.ToString("0.####", inv)))
            + "&hourly=" + HourlyVariables
            + "&wind_speed_unit=kn&timeformat=unixtime&timezone=GMT"
            + $"&start_date={first:yyyy-MM-dd}&end_date={last:yyyy-MM-dd}";
    }

    /// <summary>Prévisions de chaque point (dans l'ordre des demandes) à l'heure la plus proche de leur heure de passage ; null si l'heure est hors de la réponse.</summary>
    public static IReadOnlyList<PointForecast?> Parse(string json, IReadOnlyList<ForecastRequest> points)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
                throw new InvalidDataException(root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "Open-Meteo");

            var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
            if (entries.Count != points.Count) throw new InvalidDataException(Loc.F("Forecast_Reponse_inattendue", entries.Count, points.Count));
            return [.. points.Select((p, i) => ParsePoint(entries[i], p.Eta))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Loc.Get("Forecast_Reponse_illisible") + ex.Message, ex);
        }
    }

    private static PointForecast? ParsePoint(JsonElement entry, DateTimeOffset eta)
    {
        var hourly = entry.GetProperty("hourly");
        var times = hourly.GetProperty("time").EnumerateArray().Select(t => t.GetInt64()).ToList();
        if (times.Count == 0) return null;
        long wanted = eta.ToUnixTimeSeconds();
        int index = 0;
        for (int i = 1; i < times.Count; i++)
            if (Math.Abs(times[i] - wanted) < Math.Abs(times[index] - wanted)) index = i;
        if (Math.Abs(times[index] - wanted) > 3600) return null;   // l'heure de passage n'est pas dans la réponse

        double? Value(string name) => hourly.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && index < arr.GetArrayLength()
            && arr[index].ValueKind == JsonValueKind.Number ? arr[index].GetDouble() : null;

        double elevationFt = (entry.TryGetProperty("elevation", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0) * MetresToFeet;
        var levels = new List<ForecastLevel>();
        foreach (int hpa in PressureLevels)
            if (Value($"wind_speed_{hpa}hPa") is { } speed && Value($"wind_direction_{hpa}hPa") is { } dir
                && Value($"temperature_{hpa}hPa") is { } temp && Value($"geopotential_height_{hpa}hPa") is { } height)
                levels.Add(new ForecastLevel(hpa, height * MetresToFeet, dir, speed, temp));

        return new PointForecast(
            DateTimeOffset.FromUnixTimeSeconds(times[index]), elevationFt,
            Value("wind_direction_10m") ?? 0, Value("wind_speed_10m") ?? 0, Value("wind_gusts_10m") ?? 0, Value("temperature_2m") ?? 0,
            Value("precipitation") ?? 0, (int)(Value("cloud_cover_low") ?? 0), (int)(Value("cloud_cover_mid") ?? 0), (int)(Value("cloud_cover_high") ?? 0),
            Value("visibility"), Value("freezing_level_height") is { } z ? z * MetresToFeet : null, Value("cape"), (int)(Value("weather_code") ?? 0), levels);
    }
}
