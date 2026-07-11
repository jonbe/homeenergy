namespace HomeEnergy.Models;

public record PriceSnapshot(
    string Currency,
    PricePoint? Current,
    IReadOnlyList<PricePoint> Today,
    IReadOnlyList<PricePoint> Tomorrow,
    DateTimeOffset FetchedAt);
