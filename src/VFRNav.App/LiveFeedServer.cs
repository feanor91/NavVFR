using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using VFRNav.Core.Services;

namespace VFRNav.App;

/// <summary>
/// Petit serveur HTTP sur l'interface locale (127.0.0.1) qui partage la route, l'avion et les zones avec StormATC,
/// pour son panneau dans le simulateur. Lecture seule : rien ici ne modifie la route.
/// <c>GET /api/state</c> : route et avion. <c>GET /api/map?minLat=&amp;minLon=&amp;maxLat=&amp;maxLon=</c> : zones et terrains.
/// </summary>
public sealed class LiveFeedServer : IDisposable
{
    public const int DefaultPort = 47731;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<LiveFeedState> _state;
    private readonly Func<double, double, double, double, LiveFeedMap> _map;
    private readonly Func<int, int, int, int, CancellationToken, Task<(byte[] Bytes, string ContentType)?>>? _tile;
    private HttpListener? _listener;
    private CancellationTokenSource? _stop;

    public LiveFeedServer(Func<LiveFeedState> state, Func<double, double, double, double, LiveFeedMap> map,
        Func<int, int, int, int, CancellationToken, Task<(byte[] Bytes, string ContentType)?>>? tile = null)
    {
        _tile = tile;
        _state = state;
        _map = map;
    }

    /// <summary>Vrai si le port a pu être ouvert.</summary>
    public bool Running => _listener?.IsListening == true;

    public void Start(int port = DefaultPort)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        try { listener.Start(); }
        catch (HttpListenerException) { listener.Close(); return; } // port occupé : le partage est facultatif
        _listener = listener;
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => Serve(listener, _stop.Token));
    }

    private async Task Serve(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context), ct);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (!request.IsLocal || request.HttpMethod != "GET") { Reply(context, 403, new { error = "refused" }); return; }
            string path = request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/api/tile/", StringComparison.Ordinal))
            {
                // /api/tile/{layer}/{z}/{x}/{y}
                var p = path["/api/tile/".Length..].Split('/');
                if (_tile is null || p.Length != 4 || !int.TryParse(p[0], out int layer) || !int.TryParse(p[1], out int z)
                    || !int.TryParse(p[2], out int x) || !int.TryParse(p[3], out int y))
                {
                    Reply(context, 404, new { error = "not found" });
                    return;
                }
                var tile = await _tile(layer, z, x, y, CancellationToken.None);
                if (tile is null) { Reply(context, 404, new { error = "no tile" }); return; }
                context.Response.StatusCode = 200;
                context.Response.ContentType = tile.Value.ContentType;
                context.Response.ContentLength64 = tile.Value.Bytes.Length;
                await context.Response.OutputStream.WriteAsync(tile.Value.Bytes);
                context.Response.Close();
                return;
            }
            switch (path)
            {
                case "/api/state":
                    Reply(context, 200, _state());
                    break;
                case "/api/map":
                    var q = request.QueryString;
                    if (!TryNumber(q["minLat"], out double minLat) || !TryNumber(q["minLon"], out double minLon) ||
                        !TryNumber(q["maxLat"], out double maxLat) || !TryNumber(q["maxLon"], out double maxLon) ||
                        minLat > maxLat || minLon > maxLon)
                    {
                        Reply(context, 400, new { error = "bad rectangle" });
                        break;
                    }
                    Reply(context, 200, _map(minLat, minLon, maxLat, maxLon));
                    break;
                default:
                    Reply(context, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or System.IO.IOException)
        {
            // le client est parti avant la réponse
        }
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static void Reply(HttpListenerContext context, int status, object body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stop?.Cancel();
        try { _listener?.Close(); } catch (ObjectDisposedException) { }
    }
}
