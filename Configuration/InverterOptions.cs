namespace HomeEnergy.Configuration;

public class InverterOptions
{
    public string Ip { get; set; } = "10.30.51.91";
    public int Port { get; set; } = 502;
    public byte Unit { get; set; } = 255;
}
