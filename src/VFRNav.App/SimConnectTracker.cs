using System.Runtime.InteropServices;
using System.Windows.Threading;
#if HAS_SIMCONNECT
using Microsoft.FlightSimulator.SimConnect;
#endif

namespace VFRNav.App;

/// <summary>Position et état de l'avion du joueur, lus dans MSFS une fois par seconde.</summary>
public sealed record AircraftState(double Lat, double Lon, double AltitudeFt, double HeadingTrue, double GroundSpeedKt, bool OnGround);

/// <summary>
/// Suit l'avion du joueur par SimConnect. MSFS peut être lancé avant ou après VFRNav : la connexion est retentée
/// toutes les 5 secondes. Sans le SDK à la compilation, <see cref="Available"/> est faux et rien ne se passe.
/// </summary>
public sealed class SimConnectTracker : IDisposable
{
    /// <summary>Message Windows que SimConnect envoie à la fenêtre quand des données sont prêtes.</summary>
    public const int WmUserSimConnect = 0x0402;

    public event Action<AircraftState>? Updated;
    public event Action<string>? StatusChanged;

#if HAS_SIMCONNECT
    public static bool Available => true;

    private enum Def { Aircraft }
    private enum Req { Aircraft }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Data
    {
        public double Lat, Lon, AltFt, Heading, GroundSpeedKt, OnGround;
    }

    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    private SimConnect? _sim;
    private IntPtr _hwnd;

    public bool Connected { get; private set; }

    public void Start(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
        _retry.Tick += (_, _) => TryConnect();
        TryConnect();
        _retry.Start();
    }

    /// <summary>À appeler depuis le WndProc de la fenêtre ; vrai si le message était pour SimConnect.</summary>
    public bool HandleMessage(int message)
    {
        if (message != WmUserSimConnect || _sim is null) return false;
        try { _sim.ReceiveMessage(); }
        catch (COMException) { Disconnect("Connexion à MSFS perdue."); }
        return true;
    }

    private void TryConnect()
    {
        if (_sim is not null) return;
        try
        {
            _sim = new SimConnect("VFRNav", _hwnd, WmUserSimConnect, null, 0);
        }
        catch (COMException)
        {
            _sim = null;
            StatusChanged?.Invoke("MSFS n'est pas lancé : nouvelle tentative toutes les 5 s.");
            return;
        }

        _sim.OnRecvOpen += (_, _) => { Connected = true; StatusChanged?.Invoke("Connecté à MSFS."); };
        _sim.OnRecvQuit += (_, _) => Disconnect("MSFS s'est fermé.");
        _sim.OnRecvException += (_, e) => StatusChanged?.Invoke($"Erreur SimConnect : {(SIMCONNECT_EXCEPTION)e.dwException}");
        _sim.OnRecvSimobjectData += OnData;

        _sim.AddToDataDefinition(Def.Aircraft, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Def.Aircraft, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Def.Aircraft, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Def.Aircraft, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Def.Aircraft, "GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Def.Aircraft, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        _sim.RegisterDataDefineStruct<Data>(Def.Aircraft);
        _sim.RequestDataOnSimObject(Req.Aircraft, Def.Aircraft, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    private void OnData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID != (uint)Req.Aircraft || data.dwData.Length == 0) return;
        var d = (Data)data.dwData[0];
        Updated?.Invoke(new AircraftState(d.Lat, d.Lon, d.AltFt, d.Heading, d.GroundSpeedKt, d.OnGround > 0.5));
    }

    private void Disconnect(string reason)
    {
        try { _sim?.Dispose(); }
        catch (COMException) { /* déjà fermé */ }
        _sim = null;
        Connected = false;
        StatusChanged?.Invoke(reason);
    }

    public void Dispose()
    {
        _retry.Stop();
        Disconnect("Suivi arrêté.");
    }
#else
    public static bool Available => false;
    public bool Connected => false;

    public void Start(IntPtr windowHandle) =>
        StatusChanged?.Invoke("Suivi indisponible : SDK SimConnect de MSFS introuvable à la compilation (variable MSFS_SIMCONNECT_PATH).");

    public bool HandleMessage(int message) => false;

    public void Dispose()
    {
    }
#endif
}
