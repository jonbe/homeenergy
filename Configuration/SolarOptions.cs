namespace HomeEnergy.Configuration;

public class SolarOptions
{
    public double Latitude { get; set; } = 57.8;
    public double Longitude { get; set; } = 14.2;
    public int Tilt { get; set; } = 15;
    public int AzimuthSouth { get; set; } = -55;   // 125 compass (SE)
    public double Kwp { get; set; } = 12;
    public double DefaultPr { get; set; } = 0.90;
}
