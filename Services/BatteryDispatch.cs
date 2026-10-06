namespace HomeEnergy.Services;

/// <summary>What the battery does during one slot.</summary>
public enum BatteryMode
{
    /// <summary>General/self-consume: PV first, then the battery covers the house;
    /// PV surplus charges the battery.</summary>
    Discharge,
    /// <summary>Backup: the battery is left untouched — the grid (or PV) runs the
    /// house and any PV surplus is exported.</summary>
    Hold,
    /// <summary>Economic window: draw from the grid into the battery.</summary>
    GridCharge,
}

/// <summary>One forward slot: all-in import price (per kWh), forecast PV and
/// estimated house load, both in kW.</summary>
public readonly record struct DispatchSlot(double Price, double PvKw, double LoadKw);

/// <param name="UsableKwh">Energy the battery may swing between empty and full.</param>
/// <param name="StartKwh">Usable energy in the battery right now.</param>
/// <param name="LegEfficiency">Efficiency of one leg (charge or discharge); a full
/// cycle keeps LegEfficiency².</param>
/// <param name="MaxGridChargeKw">Power drawn into the battery while grid-charging.</param>
/// <param name="ExportFactor">Fraction of the import price received for exported energy.</param>
/// <param name="TerminalPrice">Value per kWh of energy still stored when the horizon
/// ends — stops the optimizer dumping the battery just because the known prices run out.</param>
/// <param name="WearPerKwh">Cost charged for every kWh delivered from the battery. Without it
/// the optimizer is indifferent to cycling the battery through nearly-free hours (it can
/// always recharge for nothing); with it, it holds the battery instead.</param>
/// <param name="SwitchCost">Cost charged each time the mode changes between slots. Every
/// change is a set of register writes on the inverter, so a change has to earn more
/// than this to be worth making; it removes one-slot islands of a different mode.</param>
/// <param name="StartMode">The mode the inverter is in now, so leaving it counts as a switch.</param>
public readonly record struct DispatchSettings(
    double UsableKwh, double StartKwh, double LegEfficiency,
    double MaxGridChargeKw, double ExportFactor, double TerminalPrice,
    double WearPerKwh = 0, double SwitchCost = 0, BatteryMode StartMode = BatteryMode.Discharge);

/// <param name="BatteryKwh">Change in stored energy over the slot (+ charge, − discharge).</param>
public readonly record struct DispatchStep(BatteryMode Mode, double BatteryKwh);

/// <summary>
/// Chooses, for every slot of a price/PV forecast, whether to discharge, hold or
/// grid-charge so the total electricity cost over the horizon is minimal.
///
/// Dynamic programming over the battery's state of charge and the mode of the
/// previous slot: the value of a kWh left in the battery is not fixed, it is whatever
/// the best later use of it is. That is what makes it hold the battery through cheap
/// hours and spend it in the priciest ones — the cheap hours are served from the grid
/// instead. The previous mode is in the state so that changing mode can carry a cost.
/// </summary>
public static class BatteryDispatch
{
    public const double SlotHours = 0.25;

    // Resolution of the state-of-charge axis. Fine enough that a quarter-hour of
    // typical house load spans several steps, so rounding barely skews the result.
    private const double StepKwh = 0.05;

    private static readonly int ModeCount = Enum.GetValues<BatteryMode>().Length;

    // Grid-charging may run at a fraction of full power (in fifths): the inverter stops
    // at its target SOC, so the last slot of a charge run is usually only partly used.
    private const int ChargeFifths = 5;

    private readonly record struct Option(BatteryMode Mode, int Fifths);

    // In order of preference: on an exact tie the earlier option wins, so plain
    // General (Discharge) is the default and grid-charging the last resort.
    private static readonly Option[] Options =
        new[] { new Option(BatteryMode.Discharge, 0), new Option(BatteryMode.Hold, 0) }
            .Concat(Enumerable.Range(1, ChargeFifths).Select(f => new Option(BatteryMode.GridCharge, f)))
            .ToArray();

    public static DispatchStep[] Optimize(IReadOnlyList<DispatchSlot> slots, DispatchSettings s)
    {
        int n = slots.Count;
        int top = Math.Max(1, (int)Math.Round(s.UsableKwh / StepKwh));
        int level = Math.Clamp((int)Math.Round(s.StartKwh / StepKwh), 0, top);

        // cost[t][m][l] = cheapest total cost from slot t on, starting at SOC level l
        // with the previous slot having run in mode m.
        // Energy still stored at the end is worth what it will later displace, less the
        // wear it will incur when delivered — otherwise keeping it always beats using it.
        double terminalValue = Math.Max(0, s.LegEfficiency * s.TerminalPrice - s.WearPerKwh);
        var cost = new double[n + 1][][];
        cost[n] = new double[ModeCount][];
        for (int m = 0; m < ModeCount; m++)
        {
            cost[n][m] = new double[top + 1];
            for (int l = 0; l <= top; l++)
                cost[n][m][l] = -l * StepKwh * terminalValue;
        }

        for (int t = n - 1; t >= 0; t--)
        {
            cost[t] = new double[ModeCount][];
            for (int m = 0; m < ModeCount; m++)
            {
                cost[t][m] = new double[top + 1];
                for (int l = 0; l <= top; l++)
                {
                    double best = double.MaxValue;
                    foreach (var option in Options)
                    {
                        var (c, next) = Apply(slots[t], option, l, top, s);
                        if (option.Mode != (BatteryMode)m) c += s.SwitchCost;
                        best = Math.Min(best, c + cost[t + 1][(int)option.Mode][next]);
                    }
                    cost[t][m][l] = best;
                }
            }
        }

        // Walk forward from the real SOC and mode, taking the best action in each slot.
        var plan = new DispatchStep[n];
        var prev = s.StartMode;
        for (int t = 0; t < n; t++)
        {
            var chosen = BatteryMode.Discharge;
            int chosenNext = level;
            double bestQ = double.MaxValue;
            foreach (var option in Options)
            {
                var (c, next) = Apply(slots[t], option, level, top, s);
                if (option.Mode != prev) c += s.SwitchCost;
                double q = c + cost[t + 1][(int)option.Mode][next];
                if (q < bestQ)
                {
                    bestQ = q;
                    chosen = option.Mode;
                    chosenNext = next;
                }
            }
            plan[t] = new DispatchStep(chosen, (chosenNext - level) * StepKwh);
            level = chosenNext;
            prev = chosen;
        }
        return plan;
    }

    // Cost of running one slot with the given option from SOC level `level`, and the
    // level it ends at. Cost is money paid for grid import, minus money earned for
    // export (export earns ExportFactor of the import price), plus battery wear.
    // The level change is decided first and the energy flows derived from it, so what
    // the house is credited with always matches what the battery actually gave.
    private static (double Cost, int Next) Apply(
        DispatchSlot slot, Option option, int level, int top, DispatchSettings s)
    {
        double eta = s.LegEfficiency;
        double net = (slot.LoadKw - slot.PvKw) * SlotHours;   // + house needs energy, − PV surplus

        double grid;        // + import, − export
        double wear = 0;
        int next = level;
        switch (option.Mode)
        {
            case BatteryMode.Discharge when net > 0:
            {
                int steps = Math.Min(level, (int)Math.Round(net / eta / StepKwh));
                double delivered = Math.Min(net, steps * StepKwh * eta);
                next = level - steps;
                grid = net - delivered;
                wear = delivered * s.WearPerKwh;
                break;
            }
            case BatteryMode.Discharge:
            {
                // PV surplus goes into the battery as far as it fits; the rest is exported.
                int steps = Math.Min(top - level, (int)Math.Round(-net * eta / StepKwh));
                next = level + steps;
                grid = net + steps * StepKwh / eta;
                break;
            }
            case BatteryMode.Hold:
                grid = net;
                break;
            default:   // GridCharge: PV surplus is stored as usual, plus extra bought from the grid
            {
                double fromPv = Math.Max(0, -net);
                double bought = s.MaxGridChargeKw * SlotHours * option.Fifths / ChargeFifths;
                int steps = Math.Min(top - level, (int)Math.Round((fromPv + bought) * eta / StepKwh));
                next = level + steps;
                grid = net + steps * StepKwh / eta;   // whatever was stored came in through the meter
                break;
            }
        }

        double c = (grid >= 0 ? grid * slot.Price : grid * slot.Price * s.ExportFactor) + wear;
        return (c, next);
    }
}
