using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using VFRNav.Core.Ifr;

namespace VFRNav.App;

/// <summary>MSFS ne répond pas : simulateur éteint, SimConnect.dll absente ou demande refusée. Inutile d'insister sur les autres terrains.</summary>
public sealed class SimUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Session avec MSFS pour lire les données d'installation de SimConnect (« facility data ») : la liste des terrains du monde et les SID
/// et STAR de chacun. La connexion reste ouverte pour toute la lecture. Elle est indépendante du suivi de l'avion. MSFS doit être lancé.
/// À utiliser depuis un seul thread à la fois.
/// </summary>
public sealed partial class SimFacilitySession : IDisposable
{
    // Numéros des messages et des types de structure, dans l'ordre de SimConnect.h.
    private const uint RecvException = 1, RecvOpen = 2, RecvAirportList = 18, RecvFacilityData = 28, RecvFacilityDataEnd = 29;
    private const uint TypeAirport = 0, TypeDeparture = 10, TypeArrival = 11, TypeRunwayTransition = 12, TypeEnrouteTransition = 13, TypeApproachLeg = 7;
    private const int FacilityDataOffset = 40;                 // en-tête SIMCONNECT_RECV_FACILITY_DATA
    private const int AirportListOffset = 28, AirportEntrySize = 36; // en-tête de liste puis Ident[9] Region[3] et trois doubles, sans alignement
    private const uint DefineId = 41, RouteDefineId = 42, ListRequestId = 40;
    private const uint TypeRoute = 22;
    private static readonly TimeSpan AirportTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] ProcedureFields = ["NAME", "N_RUNWAY_TRANSITIONS", "N_ENROUTE_TRANSITIONS", "N_APPROACH_LEGS"];
    private static readonly string[] RunwayTransitionFields = ["RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "N_APPROACH_LEGS"];
    private static readonly string[] EnrouteTransitionFields = ["NAME", "N_APPROACH_LEGS"];
    private static readonly string[] LegFields =
        ["TYPE", "FIX_ICAO", "FIX_REGION", "FIX_TYPE", "FIX_LATITUDE", "FIX_LONGITUDE", "APPROACH_ALT_DESC", "ALTITUDE1", "ALTITUDE2", "SPEED_LIMIT"];

    private sealed class Node
    {
        public uint Type;
        public uint Parent;
        public string Name = "";
        public string Runway = "";
        public FacilityLeg? Leg;
    }

    private IntPtr _handle;
    private uint _nextRequest = 100;
    private bool _routesDefined;

    private SimFacilitySession(IntPtr handle, string version)
    {
        _handle = handle;
        SimVersion = version;
    }

    /// <summary>Version de MSFS, ex. « MSFS 1.5.4.0 » : sert à savoir si le simulateur a changé depuis la dernière lecture.</summary>
    public string SimVersion { get; }

    /// <summary>Ouvre la session. Lève <see cref="SimUnavailableException"/> si MSFS n'est pas lancé.</summary>
    public static SimFacilitySession Open()
    {
        if (!SimConnectTracker.Available)
            throw new SimUnavailableException("SimConnect.dll introuvable : copiez-la à côté de MSFSRhumb.exe ou installez le SDK de MSFS.");
        if (SimConnect_Open(out var handle, "MSFS Rhumb facilities", IntPtr.Zero, 0, IntPtr.Zero, 0) != 0 || handle == IntPtr.Zero)
            throw new SimUnavailableException("MSFS n'est pas lancé (ou SimConnect est indisponible).");

        try
        {
            string version = WaitForOpen(handle);
            Define(handle);
            return new SimFacilitySession(handle, version);
        }
        catch
        {
            SimConnect_Close(handle);
            throw;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        SimConnect_Close(_handle);
        _handle = IntPtr.Zero;
    }

    // ---------- version du simulateur ----------

    /// <summary>Le premier message de MSFS donne son nom et sa version.</summary>
    private static string WaitForOpen(IntPtr handle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (SimConnect_GetNextDispatch(handle, out var data, out _) != 0 || data == IntPtr.Zero) { Thread.Sleep(10); continue; }
            if ((uint)Marshal.ReadInt32(data, 8) != RecvOpen) continue;
            // szApplicationName[256] puis quatre numéros : version majeure et mineure, build majeur et mineur.
            uint major = (uint)Marshal.ReadInt32(data, 268), minor = (uint)Marshal.ReadInt32(data, 272);
            uint buildMajor = (uint)Marshal.ReadInt32(data, 276), buildMinor = (uint)Marshal.ReadInt32(data, 280);
            return $"MSFS {major}.{minor}.{buildMajor}.{buildMinor}";
        }
        throw new SimUnavailableException("MSFS n'a pas répondu à la connexion SimConnect.");
    }

    // ---------- liste des terrains ----------

    /// <summary>Un terrain de MSFS : code OACI, région et position.</summary>
    public sealed record SimAirport(string Icao, string Region, double Lat, double Lon);

    /// <summary>Codes OACI de quatre lettres de tous les terrains du simulateur (les terrains à code local, comme « K5T6 » ou « FR-0123 », sont écartés : ils n'ont pas de procédures publiées).</summary>
    public IReadOnlyList<string> ListAirports(CancellationToken ct = default) =>
        [.. ListAirportPoints(ct).Select(a => a.Icao)];

    /// <summary>Les mêmes terrains avec leur position : ils servent de départs et d'arrivées pour les routes hors de France.</summary>
    public IReadOnlyList<SimAirport> ListAirportPoints(CancellationToken ct = default)
    {
        uint request = ListRequestId;
        if (SimConnect_RequestFacilitiesList(_handle, 0 /* AIRPORT */, request) != 0)
            throw new SimUnavailableException("MSFS a refusé la demande de la liste des terrains.");

        var airports = new Dictionary<string, SimAirport>(StringComparer.Ordinal);
        int received = 0, expected = int.MaxValue;
        var deadline = DateTime.UtcNow + AirportTimeout;
        while (received < expected && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (SimConnect_GetNextDispatch(_handle, out var data, out var size) != 0 || data == IntPtr.Zero) { Thread.Sleep(10); continue; }
            if ((uint)Marshal.ReadInt32(data, 8) != RecvAirportList || (uint)Marshal.ReadInt32(data, 12) != request) continue;

            int count = Marshal.ReadInt32(data, 16);
            expected = Marshal.ReadInt32(data, 24); // dwOutOf : nombre de messages dans lequel la liste est découpée
            received++;
            for (int i = 0; i < count && AirportListOffset + (i + 1) * AirportEntrySize <= size; i++)
            {
                var e = data + AirportListOffset + i * AirportEntrySize;
                string ident = AsciiZ(e, 0, 9);
                if (IcaoRx().IsMatch(ident))
                    airports[ident] = new SimAirport(ident, AsciiZ(e, 9, 3),
                        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(e, 12)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(e, 20)));
            }
        }
        if (received == 0) throw new SimUnavailableException("MSFS n'a pas envoyé la liste des terrains.");
        return [.. airports.Values.OrderBy(a => a.Icao, StringComparer.Ordinal)];
    }

    [GeneratedRegex("^[A-Z]{4}$")]
    private static partial Regex IcaoRx();

    // ---------- SID et STAR d'un terrain ----------

    /// <summary>SID et STAR d'un terrain. Liste vide si MSFS n'en a aucune pour lui. Lève <see cref="SimUnavailableException"/> si MSFS ne répond plus.</summary>
    public IReadOnlyList<FacilityProcedure> ReadProcedures(string icao, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        icao = icao.Trim().ToUpperInvariant();
        uint request = _nextRequest++;
        if (SimConnect_RequestFacilityData(_handle, DefineId, request, icao, "") != 0)
            throw new SimUnavailableException($"MSFS a refusé la demande de données pour {icao}.");

        var nodes = new Dictionary<uint, Node>();
        var order = new List<uint>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        bool ended = false;
        int? exception = null;

        while (!ended && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (SimConnect_GetNextDispatch(_handle, out var data, out _) != 0 || data == IntPtr.Zero) { Thread.Sleep(2); continue; }
            switch ((uint)Marshal.ReadInt32(data, 8))
            {
                case RecvException: exception = Marshal.ReadInt32(data, 12); ended = true; break;
                case RecvFacilityDataEnd when (uint)Marshal.ReadInt32(data, 12) == request: ended = true; break;
                case RecvFacilityData when (uint)Marshal.ReadInt32(data, 12) == request: Store(data, nodes, order); break;
            }
        }

        if (!ended) throw new SimUnavailableException($"MSFS n'a pas fini de répondre pour {icao}.");
        _ = exception; // un terrain inconnu de MSFS répond par une erreur sans données : liste vide
        return Build(nodes, order);
    }

    /// <summary>
    /// Lit les procédures de plusieurs terrains en gardant jusqu'à <paramref name="window"/> demandes en cours en même temps :
    /// MSFS répond bien plus vite ainsi qu'une demande après l'autre. <paramref name="onResult"/> reçoit chaque terrain dès qu'il est lu.
    /// </summary>
    public void ReadProceduresBatch(IReadOnlyList<string> airports, int window, Action<string, IReadOnlyList<FacilityProcedure>> onResult,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var pending = new Dictionary<uint, (string Icao, Dictionary<uint, Node> Nodes, List<uint> Order, DateTime Deadline)>();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        int next = 0;

        while (next < airports.Count || pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            while (next < airports.Count && pending.Count < window)
            {
                string icao = airports[next++].Trim().ToUpperInvariant();
                uint request = _nextRequest++;
                if (SimConnect_RequestFacilityData(_handle, DefineId, request, icao, "") != 0)
                    throw new SimUnavailableException($"MSFS a refusé la demande de données pour {icao}.");
                pending[request] = (icao, new Dictionary<uint, Node>(), new List<uint>(), DateTime.UtcNow + limit);
            }

            if (SimConnect_GetNextDispatch(_handle, out var data, out _) != 0 || data == IntPtr.Zero)
            {
                var late = pending.Where(p => p.Value.Deadline < DateTime.UtcNow).Select(p => p.Key).ToList();
                foreach (uint id in late) { onResult(pending[id].Icao, []); pending.Remove(id); } // MSFS n'a pas répondu à temps : terrain sans procédures
                Thread.Sleep(1);
                continue;
            }

            uint kind = (uint)Marshal.ReadInt32(data, 8);
            if (kind is not (RecvFacilityData or RecvFacilityDataEnd or RecvException)) continue;
            uint id2 = (uint)Marshal.ReadInt32(data, 12);
            if (kind == RecvException) continue; // l'erreur d'un terrain inconnu n'a pas d'identifiant utile : son délai le clôt
            if (!pending.TryGetValue(id2, out var state)) continue;

            if (kind == RecvFacilityData) Store(data, state.Nodes, state.Order);
            else
            {
                pending.Remove(id2);
                onResult(state.Icao, Build(state.Nodes, state.Order));
            }
        }
    }

    // ---------- routes ATS ----------

    /// <summary>
    /// Lit les routes ATS qui passent par chacun de ces points (plusieurs demandes en même temps). <paramref name="onResult"/> reçoit, pour chaque point,
    /// ses tronçons (vide si aucune route n'y passe).
    /// </summary>
    public void ReadRoutesBatch(IReadOnlyList<SimPoint> points, int window, Action<SimPoint, IReadOnlyList<SimRouteLink>> onResult,
        CancellationToken ct = default, TimeSpan? timeout = null) =>
        CrawlRoutes(points, (point, links) => { onResult(point, links); return []; }, window, ct, timeout);

    /// <summary>
    /// Explore le réseau de routes ATS de proche en proche : <paramref name="onResult"/> reçoit les tronçons de chaque point lu et rend les
    /// points voisins à lire à leur tour (c'est à lui d'écarter ceux déjà vus). Plusieurs demandes en même temps.
    /// <paramref name="progress"/> reçoit le nombre de points lus et le nombre encore à lire.
    /// </summary>
    public void CrawlRoutes(IEnumerable<SimPoint> seeds, Func<SimPoint, IReadOnlyList<SimRouteLink>, IEnumerable<SimPoint>> onResult, int window,
        CancellationToken ct = default, TimeSpan? timeout = null, Action<int, int>? progress = null)
    {
        DefineRoutes();
        var queue = new Queue<SimPoint>(seeds);
        var pending = new Dictionary<uint, (SimPoint Point, List<SimRouteLink> Links, DateTime Deadline)>();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        int done = 0;

        void Finish(uint id)
        {
            var (point, links, _) = pending[id];
            pending.Remove(id);
            foreach (var found in onResult(point, links)) queue.Enqueue(found);
            if (++done % 500 == 0) progress?.Invoke(done, queue.Count + pending.Count);
        }

        while (queue.Count > 0 || pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            while (queue.Count > 0 && pending.Count < window)
            {
                var point = queue.Dequeue();
                uint request = _nextRequest++;
                char type = point.Kind switch { SimPointKind.Vor => 'V', SimPointKind.Ndb => 'N', _ => 'W' };
                if (SimConnect_RequestFacilityData_EX1(_handle, RouteDefineId, request, point.Ident, point.Region, (byte)type) != 0)
                    throw new SimUnavailableException($"MSFS a refusé la demande de routes pour {point.Ident}.");
                pending[request] = (point, [], DateTime.UtcNow + limit);
            }

            if (SimConnect_GetNextDispatch(_handle, out var data, out _) != 0 || data == IntPtr.Zero)
            {
                foreach (uint id in pending.Where(p => p.Value.Deadline < DateTime.UtcNow).Select(p => p.Key).ToList()) Finish(id); // sans réponse : point sans route
                Thread.Sleep(1);
                continue;
            }

            uint kind = (uint)Marshal.ReadInt32(data, 8);
            if (kind is not (RecvFacilityData or RecvFacilityDataEnd)) continue;
            uint id2 = (uint)Marshal.ReadInt32(data, 12);
            if (!pending.TryGetValue(id2, out var state)) continue;

            if (kind == RecvFacilityData)
            {
                if ((uint)Marshal.ReadInt32(data, 24) != TypeRoute) continue;
                var p = data + FacilityDataOffset;
                // NAME(32) TYPE(4) NEXT: ICAO(8) REGION(8) TYPE(4) LAT(8) LON(8) ALT(4)  PREV: ICAO(8) REGION(8) TYPE(4) LAT(8) LON(8) ALT(4), sans alignement.
                state.Links.Add(new SimRouteLink(AsciiZ(p, 0, 32), Marshal.ReadInt32(p, 32),
                    AsciiZ(p, 76, 8), AsciiZ(p, 84, 8), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 96)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 104)),
                    BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 112)),
                    AsciiZ(p, 36, 8), AsciiZ(p, 44, 8), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 56)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 64)),
                    BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 72)),
                    ReadType(p, 92), ReadType(p, 52)));
            }
            else Finish(id2);
        }
        progress?.Invoke(done, 0);
    }

    /// <summary>Le type d'un point voisin sur la route : 86 « V » VOR, 78 « N » NDB, 87 « W » point.</summary>
    private static char ReadType(IntPtr p, int offset)
    {
        int code = Marshal.ReadInt32(p, offset);
        return code is > 32 and < 127 ? (char)code : 'W';
    }

    private void DefineRoutes()
    {
        if (_routesDefined) return;
        void Add(string name)
        {
            int hr = SimConnect_AddToFacilityDefinition(_handle, RouteDefineId, name);
            if (hr != 0) throw new SimUnavailableException($"SimConnect refuse la définition « {name} » (code {hr:X}).");
        }
        Add("OPEN WAYPOINT"); Add("ICAO"); Add("N_ROUTES");
        Add("OPEN ROUTE");
        foreach (var f in new[] { "NAME", "TYPE", "NEXT_ICAO", "NEXT_REGION", "NEXT_TYPE", "NEXT_LATITUDE", "NEXT_LONGITUDE", "NEXT_ALTITUDE",
                                  "PREV_ICAO", "PREV_REGION", "PREV_TYPE", "PREV_LATITUDE", "PREV_LONGITUDE", "PREV_ALTITUDE" }) Add(f);
        Add("CLOSE ROUTE"); Add("CLOSE WAYPOINT");
        _routesDefined = true;
    }

    private static void Define(IntPtr h)
    {
        void Add(string name)
        {
            int hr = SimConnect_AddToFacilityDefinition(h, DefineId, name);
            if (hr != 0) throw new SimUnavailableException($"SimConnect refuse la définition « {name} » (code {hr:X}).");
        }
        void Fields(string[] fields) { foreach (var f in fields) Add(f); }
        void Leg() { Add("OPEN APPROACH_LEG"); Fields(LegFields); Add("CLOSE APPROACH_LEG"); }

        void Procedure(string kind)
        {
            Add("OPEN " + kind); Fields(ProcedureFields);
            Add("OPEN RUNWAY_TRANSITION"); Fields(RunwayTransitionFields); Leg(); Add("CLOSE RUNWAY_TRANSITION");
            Add("OPEN ENROUTE_TRANSITION"); Fields(EnrouteTransitionFields); Leg(); Add("CLOSE ENROUTE_TRANSITION");
            Leg(); // points communs à toutes les transitions
            Add("CLOSE " + kind);
        }

        Add("OPEN AIRPORT"); Add("ICAO");
        Procedure("DEPARTURE");
        Procedure("ARRIVAL");
        Add("CLOSE AIRPORT");
    }

    /// <summary>Range un message : chaque structure arrive avec son identifiant et celui de son parent.</summary>
    private static void Store(IntPtr data, Dictionary<uint, Node> nodes, List<uint> order)
    {
        uint unique = (uint)Marshal.ReadInt32(data, 16), parent = (uint)Marshal.ReadInt32(data, 20), type = (uint)Marshal.ReadInt32(data, 24);
        var p = data + FacilityDataOffset;
        var node = new Node { Type = type, Parent = parent };

        switch (type)
        {
            case TypeDeparture or TypeArrival:
                node.Name = AsciiZ(p, 0, 8);
                break;
            case TypeRunwayTransition:
                node.Runway = SimProcedures.RunwayName(Marshal.ReadInt32(p, 0), Marshal.ReadInt32(p, 4));
                break;
            case TypeEnrouteTransition:
                node.Name = AsciiZ(p, 0, 8);
                break;
            case TypeApproachLeg:
                // Les champs se suivent sans alignement : TYPE(4) FIX_ICAO(8) FIX_REGION(8) FIX_TYPE(4) LAT(8) LON(8) ALT_DESC(4) ALT1(4) ALT2(4) SPEED(4).
                int fixType = Marshal.ReadInt32(p, 20);
                node.Leg = new FacilityLeg(
                    Marshal.ReadInt32(p, 0), AsciiZ(p, 4, 8),
                    BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 24)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 32)),
                    Marshal.ReadInt32(p, 40),
                    BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 44)), BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 48)),
                    BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 52)),
                    AsciiZ(p, 12, 8), fixType is > 32 and < 127 ? (char)fixType : 'W');
                break;
            case TypeAirport:
            default:
                return;
        }
        if (nodes.TryAdd(unique, node)) order.Add(unique);
    }

    private static string AsciiZ(IntPtr p, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(p + offset, bytes, 0, length);
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? length : end).Trim();
    }

    /// <summary>Rattache chaque point à sa transition ou à sa procédure, dans l'ordre d'arrivée des messages.</summary>
    private static List<FacilityProcedure> Build(Dictionary<uint, Node> nodes, List<uint> order)
    {
        var procedures = new Dictionary<uint, FacilityProcedure>();
        var transitions = new Dictionary<uint, FacilityTransition>();
        var result = new List<FacilityProcedure>();

        foreach (uint id in order)
        {
            var n = nodes[id];
            switch (n.Type)
            {
                case TypeDeparture or TypeArrival:
                    var p = new FacilityProcedure { Kind = n.Type == TypeDeparture ? ProcedureKind.Sid : ProcedureKind.Star, Name = n.Name };
                    procedures[id] = p;
                    result.Add(p);
                    break;
                case TypeRunwayTransition when procedures.TryGetValue(n.Parent, out var owner):
                    var rt = new FacilityTransition { Runway = n.Runway, Name = n.Runway };
                    owner.RunwayTransitions.Add(rt);
                    transitions[id] = rt;
                    break;
                case TypeEnrouteTransition when procedures.TryGetValue(n.Parent, out var owner):
                    var et = new FacilityTransition { Name = n.Name };
                    owner.EnrouteTransitions.Add(et);
                    transitions[id] = et;
                    break;
                case TypeApproachLeg when n.Leg is not null:
                    if (transitions.TryGetValue(n.Parent, out var t)) t.Legs.Add(n.Leg);
                    else if (procedures.TryGetValue(n.Parent, out var common)) common.Common.Add(n.Leg);
                    break;
            }
        }
        return result;
    }

    // ---------- SimConnect.dll (résolue par SimConnectTracker) ----------

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd, uint userEvent, IntPtr eventHandle, uint configIndex);

    [DllImport("SimConnect")]
    private static extern int SimConnect_Close(IntPtr handle);

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_AddToFacilityDefinition(IntPtr handle, uint defineId, string fieldName);

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_RequestFacilityData(IntPtr handle, uint defineId, uint requestId, string icao, string region);

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_RequestFacilityData_EX1(IntPtr handle, uint defineId, uint requestId, string icao, string region, byte type);

    [DllImport("SimConnect")]
    private static extern int SimConnect_RequestFacilitiesList(IntPtr handle, uint listType, uint requestId);

    [DllImport("SimConnect")]
    private static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);
}
