namespace HomeEnergy.Configuration;

public class AppOptions
{
    public InverterOptions Inverter { get; set; } = new();
    public SolarOptions Solar { get; set; } = new();
    public ChargeAmpsOptions ChargeAmps { get; set; } = new();
    public BatteryOptions Battery { get; set; } = new();
    public StrategyOptions Strategy { get; set; } = new();

    /// <summary>When true, the charging poller writes PV-Charging mode to the
    /// inverter while a car charges. Off by default so it never fights the
    /// Solinteg app/cloud schema unless explicitly enabled.</summary>
    public bool AutomationEnabled { get; set; } = false;

    /// <summary>Directory for persistent files (SQLite DB, Tibber token, PR
    /// calibration history). Empty = the app's content root (local dev). In the
    /// container set this to /data, a bind-mounted volume, so the data survives
    /// image updates.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Password for the dashboard login screen, gating the whole app
    /// (dashboard + control endpoints). Empty = auth disabled (local dev). A browser
    /// enters it once and gets an encrypted 30-day session cookie; scripts may
    /// send it as an <c>X-Api-Key</c> header instead. See <c>LoginGate</c>.</summary>
    public string AuthToken { get; set; } = "";
}
