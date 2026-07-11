namespace HomeEnergy.Models;

public record PricePoint(DateTimeOffset StartsAt, double Total, string Level);
