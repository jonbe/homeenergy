namespace HomeEnergy.Configuration;

public interface ISecretProvider
{
    string? Get(string name);
}
