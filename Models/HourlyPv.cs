namespace HomeEnergy.Models;

public record HourlyPv(int Hour, double EstKw, double? CloudPct);
