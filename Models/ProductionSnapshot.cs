namespace HomeEnergy.Models;

public record ProductionSnapshot(
    double? TodayKwh,
    double? TodayProfit,
    string Currency,
    DateTimeOffset FetchedAt);
