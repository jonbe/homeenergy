using System.Runtime.InteropServices;

namespace HomeEnergy.Configuration;

/// <summary>
/// Resolves secrets from the process environment, falling back to Windows
/// User-scope environment variables (where TIBBER_TOKEN / CHARGEAMPS_* live).
/// A process launched from git-bash does not inherit User-scope vars, so the
/// explicit User target is required.
/// </summary>
public class EnvironmentSecretProvider : ISecretProvider
{
    public string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }
}
