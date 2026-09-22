using System;
using System.Collections.Generic;
using System.IO;

namespace Csl.Tool;

/// <summary>The .sdsl files the Stride packages of a version ship with, from the NuGet cache.</summary>
public static class EngineShaderFiles
{
    public static string PackagesRoot =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>Every .sdsl of every stride.* package at this version, sorted by path.</summary>
    public static List<string> Find(string strideVersion)
    {
        var result = new List<string>();
        if (!Directory.Exists(PackagesRoot))
            return result;
        foreach (var package in Directory.GetDirectories(PackagesRoot, "stride.*"))
        {
            var root = Path.Combine(package, strideVersion);
            if (Directory.Exists(root))
                result.AddRange(Directory.GetFiles(root, "*.sdsl", SearchOption.AllDirectories));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
