namespace HomeEnergy.Configuration;

/// <summary>Tunables for the low-sun price-arbitrage strategy: when the day's
/// PV forecast is poor, grid-charge the battery during the cheapest hours and
/// ride the expensive hours on battery instead of importing.</summary>
public class StrategyOptions
{
    /// <summary>Forecast PV for the day (kWh) at or below which we treat it as a
    /// low-sun day and switch from the solar plan to price arbitrage.</summary>
    public double LowSunKwh { get; set; } = 25;

    /// <summary>Rough average house load (kWh per hour), used to size how much
    /// energy must carry us to the next cheap window. Summer is low; winter far
    /// higher. Summer = Apr–Sep, winter = Oct–Mar.</summary>
    public double SummerLoadKwhPerHour { get; set; } = 1.0;
    public double WinterLoadKwhPerHour { get; set; } = 3.0;

    /// <summary>Minimum price gap (%) between a cheap charging hour and the
    /// expensive hour it offsets before grid arbitrage pays off — covers the
    /// battery round-trip loss.</summary>
    public double MinArbitrageGapPct { get; set; } = 15;

    /// <summary>Assumed usable grid-charge power (kW) when estimating how much a
    /// single cheap hour can add to the battery.</summary>
    public double MaxGridChargeKw { get; set; } = 5;
}
