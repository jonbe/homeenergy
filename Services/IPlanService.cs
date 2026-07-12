using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface IPlanService
{
    PlanSnapshot? Build(PriceSnapshot? prices, SolarSnapshot? solar, InverterStats? inverter);
}
