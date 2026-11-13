namespace HomeEnergy.Configuration;

public class SolarOptions
{
    // Rounded to about 6 km on purpose: this repository is public. The exact place comes from SOLAR_LATITUDE / SOLAR_LONGITUDE (docker-compose.yml, .env).
    public double Latitude { get; set; } = 57.8;
    public double Longitude { get; set; } = 14.2;
    public int Tilt { get; set; } = 15;
    public int AzimuthSouth { get; set; } = -55;   // 125 compass (SE)
    public double Kwp { get; set; } = 12;
    public double DefaultPr { get; set; } = 0.90;
}
