namespace Csl.TestApp;

/// <summary>The .sdsl files the Stride packages this app is built against ship with.</summary>
internal static class EngineShaders
{
    public static string PackagesRoot =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>The engine version, read from the Stride.Engine assembly this app loads.</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(Stride.Engine.Game).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var version = informational?.Split('+')[0];
            return version ?? throw new InvalidOperationException("No version on Stride.Engine");
        }
    }

    /// <summary>Every .sdsl of every stride.* package at this version, sorted by path.</summary>
    public static List<string> Files()
    {
        var version = Version;
        var result = new List<string>();
        foreach (var package in Directory.GetDirectories(PackagesRoot, "stride.*"))
        {
            var root = Path.Combine(package, version);
            if (Directory.Exists(root))
                result.AddRange(Directory.GetFiles(root, "*.sdsl", SearchOption.AllDirectories));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
