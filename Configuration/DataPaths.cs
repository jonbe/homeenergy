namespace HomeEnergy.Configuration;

public static class DataPaths
{
    /// <summary>Resolves a persistent file path under <see cref="AppOptions.DataDir"/>
    /// (falling back to the content root when unset) and ensures the directory exists.</summary>
    public static string Resolve(AppOptions opt, string contentRoot, string fileName)
    {
        var dir = string.IsNullOrWhiteSpace(opt.DataDir) ? contentRoot : opt.DataDir;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}
