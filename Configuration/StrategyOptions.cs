namespace HomeEnergy.Configuration;

/// <summary>Tunables for the price optimizer: it decides, per 15-minute slot,
/// whether to discharge the battery, hold it for pricier slots, or grid-charge
/// it, so as to minimise the electricity bill.</summary>
public class StrategyOptions
{
    /// <summary>Rough average house load (kWh per hour), used to size how much
    /// energy the battery must carry to the slots worth spending it on. Summer is
    /// low; winter far higher. Summer = Apr–Sep, winter = Oct–Mar.</summary>
    public double SummerLoadKwhPerHour { get; set; } = 1.0;
    public double WinterLoadKwhPerHour { get; set; } = 3.0;

    /// <summary>Round-trip loss margin (%): a kWh that goes through the battery is
    /// only worth storing if it later displaces a kWh at least this much more
    /// expensive. Sets the battery's efficiency in the optimizer.</summary>
    public double MinArbitrageGapPct { get; set; } = 15;

    /// <summary>Assumed usable grid-charge power (kW) when estimating how much a
    /// single cheap slot can add to the battery.</summary>
    public double MaxGridChargeKw { get; set; } = 5;

    /// <summary>Wear cost (in the price currency) attributed to every kWh drawn from
    /// the battery. Keeps the optimizer from cycling the battery for a negligible
    /// gain — e.g. running it down through nearly-free hours and recharging it —
    /// so it holds the battery for the slots that are really worth spending it on.</summary>
    public double BatteryWearPerKwh { get; set; } = 0.10;

    /// <summary>Rough fraction of the all-in import price you get paid when selling
    /// to the grid (the import price includes tax and fees that export doesn't).
    /// Low values make the optimizer prefer storing PV over exporting it.</summary>
    public double ExportPriceFactor { get; set; } = 0.6;
}
