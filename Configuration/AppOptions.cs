namespace HomeEnergy.Configuration;

public class AppOptions
{
    public InverterOptions Inverter { get; set; } = new();
    public SolarOptions Solar { get; set; } = new();
    public ChargeAmpsOptions ChargeAmps { get; set; } = new();
    public BatteryOptions Battery { get; set; } = new();

    /// <summary>When true, the charging poller writes PV-Charging mode to the
    /// inverter while a car charges. Off by default so it never fights the
    /// Solinteg app/cloud schema unless explicitly enabled.</summary>
    public bool AutomationEnabled { get; set; } = false;

    /// <summary>Directory for persistent files (SQLite DB, Tibber token, PR
    /// calibration history). Empty = the app's content root (local dev). In the
    /// container set this to /data, a bind-mounted volume, so the data survives
    /// image updates.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Shared secret that gates the whole app (dashboard + control
    /// endpoints). Empty = auth disabled (local dev). When set, requests must
    /// present it via the <c>he_auth</c> cookie, an <c>X-Api-Key</c> header, or
    /// a one-time <c>?key=</c> query that then sets the cookie.</summary>
    public string AuthToken { get; set; } = "";
}
