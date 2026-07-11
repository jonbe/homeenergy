namespace HomeEnergy.Models;

public record InverterStats(
    double PvPowerW,
    double Pv1PowerW,
    double Pv2PowerW,
    double InverterAcW,
    double GridPowerW,      // + import from grid, - export to grid
    double BatteryPowerW,   // + charging, - discharging
    double BatterySoc,      // %
    double GenTodayKwh,
    string ModeRaw,         // hex of register 50000
    string ModeLabel,
    DateTimeOffset ReadAt);
