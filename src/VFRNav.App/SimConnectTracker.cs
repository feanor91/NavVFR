using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Position et état de l'avion du joueur, lus dans MSFS une fois par seconde.</summary>
public sealed record AircraftState(double Lat, double Lon, double AltitudeFt, double HeadingTrue, double GroundSpeedKt, bool OnGround);

/// <summary>
/// Suit l'avion du joueur par SimConnect, en appelant directement la DLL native <c>SimConnect.dll</c> : aucune référence
/// au SDK n'est nécessaire à la compilation. La DLL est cherchée à côté de l'application, puis dans le SDK de MSFS
/// (variable MSFS_SIMCONNECT_PATH, MSFS_SDK, C:\MSFS 2024 SDK, C:\MSFS SDK). MSFS peut être lancé avant ou après
/// MSFS Rhumb : la connexion est retentée toutes les 5 secondes.
/// </summary>
public sealed class SimConnectTracker : IDisposable
{
    public event Action<AircraftState>? Updated;
    public event Action<string>? StatusChanged;

    private const uint DefAircraft = 0, ReqAircraft = 0;
    private const uint DataTypeFloat64 = 4, PeriodSecond = 4, ObjectIdUser = 0;
    private const uint RecvException = 1, RecvOpen = 2, RecvQuit = 3, RecvSimObjectData = 8;
    private const int DataOffset = 40; // taille de l'en-tête SIMCONNECT_RECV_SIMOBJECT_DATA

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Data
    {
        public double Lat, Lon, AltFt, Heading, GroundSpeedKt, OnGround;
    }

    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _handle;
    private bool _libraryMissingReported;

    static SimConnectTracker() => NativeLibrary.SetDllImportResolver(typeof(SimConnectTracker).Assembly, Resolve);

    /// <summary>Vrai si la DLL native a été trouvée sur ce PC.</summary>
    public static bool Available => FindLibrary() is not null;

    public bool Connected { get; private set; }

    public void Start(IntPtr windowHandle)
    {
        _retry.Tick += (_, _) => TryConnect();
        _poll.Tick += (_, _) => Drain();
        TryConnect();
        _retry.Start();
    }

    /// <summary>Conservé pour la fenêtre : la lecture se fait par sondage, aucun message Windows n'est utilisé.</summary>
    public bool HandleMessage(int message) => false;

    private void TryConnect()
    {
        if (_handle != IntPtr.Zero) return;
        if (!Available)
        {
            if (!_libraryMissingReported)
                StatusChanged?.Invoke(Loc.Get("SimConnectTracker_Suivi_indisponible_SimConnect_dll_introuva"));
            _libraryMissingReported = true;
            return;
        }

        if (SimConnect_Open(out var handle, "MSFS Rhumb", IntPtr.Zero, 0, IntPtr.Zero, 0) != 0 || handle == IntPtr.Zero)
        {
            StatusChanged?.Invoke(Loc.Get("SimConnectTracker_MSFS_n_est_pas_lance"));
            return;
        }

        _handle = handle;
        bool ok = Add("PLANE LATITUDE", "degrees", 0) && Add("PLANE LONGITUDE", "degrees", 1) && Add("PLANE ALTITUDE", "feet", 2)
                  && Add("PLANE HEADING DEGREES TRUE", "degrees", 3) && Add("GROUND VELOCITY", "knots", 4) && Add("SIM ON GROUND", "bool", 5)
                  && SimConnect_RequestDataOnSimObject(_handle, ReqAircraft, DefAircraft, ObjectIdUser, PeriodSecond, 0, 0, 0, 0) == 0;
        if (!ok)
        {
            Disconnect(Loc.Get("SimConnectTracker_Connexion_a_MSFS_impossible_definition"));
            return;
        }
        _poll.Start();
    }

    private bool Add(string name, string units, uint id) =>
        SimConnect_AddToDataDefinition(_handle, DefAircraft, name, units, DataTypeFloat64, 0f, id) == 0;

    private void Drain()
    {
        while (_handle != IntPtr.Zero && SimConnect_GetNextDispatch(_handle, out var data, out var size) == 0 && data != IntPtr.Zero)
        {
            uint id = (uint)Marshal.ReadInt32(data, 8);
            switch (id)
            {
                case RecvOpen:
                    Connected = true;
                    StatusChanged?.Invoke(Loc.Get("SimConnectTracker_Connecte_a_MSFS"));
                    break;
                case RecvQuit:
                    Disconnect(Loc.Get("SimConnectTracker_MSFS_s_est_ferme"));
                    return;
                case RecvException:
                    StatusChanged?.Invoke(Loc.F("SimConnectTracker_Erreur_SimConnect_code", Marshal.ReadInt32(data, 12)));
                    break;
                case RecvSimObjectData when size >= DataOffset + Marshal.SizeOf<Data>() && (uint)Marshal.ReadInt32(data, 12) == ReqAircraft:
                    var d = Marshal.PtrToStructure<Data>(data + DataOffset);
                    Updated?.Invoke(new AircraftState(d.Lat, d.Lon, d.AltFt, d.Heading, d.GroundSpeedKt, d.OnGround > 0.5));
                    break;
            }
        }
    }

    private void Disconnect(string reason)
    {
        _poll.Stop();
        if (_handle != IntPtr.Zero) SimConnect_Close(_handle);
        _handle = IntPtr.Zero;
        Connected = false;
        StatusChanged?.Invoke(reason);
    }

    public void Dispose()
    {
        _retry.Stop();
        Disconnect(Loc.Get("SimConnectTracker_Suivi_arrete"));
    }

    // ---------- DLL native ----------
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name == "SimConnect" && FindLibrary() is { } file && NativeLibrary.TryLoad(file, out var lib) ? lib : IntPtr.Zero;

    private static string? FindLibrary()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "SimConnect.dll") };
        foreach (var variable in new[] { "MSFS_SIMCONNECT_PATH", "MSFS_SIMCONNECT_NATIVE_PATH" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } p)
            {
                string dir = Path.GetDirectoryName(p) ?? "";
                candidates.AddRange([Path.Combine(dir, "SimConnect.dll"), Path.Combine(dir, "..", "SimConnect.dll")]);
            }
        if (Environment.GetEnvironmentVariable("MSFS_SDK") is { Length: > 0 } sdk)
            candidates.Add(Path.Combine(sdk, "SimConnect SDK", "lib", "SimConnect.dll"));
        candidates.Add(@"C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll");
        candidates.Add(@"C:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll");
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd, uint userEvent, IntPtr eventHandle, uint configIndex);

    [DllImport("SimConnect")]
    private static extern int SimConnect_Close(IntPtr handle);

    [DllImport("SimConnect", CharSet = CharSet.Ansi)]
    private static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, string datum, string units, uint datumType, float epsilon, uint datumId);

    [DllImport("SimConnect")]
    private static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [DllImport("SimConnect")]
    private static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);
}
