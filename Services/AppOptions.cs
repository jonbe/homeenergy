namespace HomeEnergy.Services;

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

public class InverterOptions
{
    public string Ip { get; set; } = "10.30.51.91";
    public int Port { get; set; } = 502;
    public byte Unit { get; set; } = 255;
}

public class SolarOptions
{
    public double Latitude { get; set; } = 57.8;
    public double Longitude { get; set; } = 14.2;
    public int Tilt { get; set; } = 15;
    public int AzimuthSouth { get; set; } = -55;   // 125 compass (SE)
    public double Kwp { get; set; } = 12;
    public double DefaultPr { get; set; } = 0.90;
}

public class ChargeAmpsOptions
{
    public string Origin { get; set; } = "https://my.charge.space";
    public string BasePath { get; set; } = "/userapp/api";
}
