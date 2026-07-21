namespace HomeEnergy.Configuration;

public class BatteryOptions
{
    public double CapacityKwh { get; set; } = 10;

    /// <summary>Energy we allow ourselves to draw from a full pack (kWh). Below
    /// this the reserve (CapacityKwh - UsableKwh) is left untouched. E.g. a 19
    /// kWh pack with UsableKwh 15 keeps ~4 kWh (~21% SOC) in reserve.</summary>
    public double UsableKwh { get; set; } = 8;
}
