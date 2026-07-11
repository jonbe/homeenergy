namespace HomeEnergy.Configuration;

public class AppOptions
{
    public InverterOptions Inverter { get; set; } = new();
    public SolarOptions Solar { get; set; } = new();
    public ChargeAmpsOptions ChargeAmps { get; set; } = new();

    /// <summary>When true, the charging poller writes PV-Charging mode to the
    /// inverter while a car charges. Off by default so it never fights the
    /// Solinteg app/cloud schema unless explicitly enabled.</summary>
    public bool AutomationEnabled { get; set; } = false;
}
