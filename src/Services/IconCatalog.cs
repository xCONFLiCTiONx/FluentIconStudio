using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentIconStudio.Models;

namespace FluentIconStudio.Services;

public static class IconCatalog
{
    public static IReadOnlyList<IconFile> Scan(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<IconFile>();

        return Directory
            .EnumerateFiles(folder, "*.svg", SearchOption.AllDirectories)
            .Select(path =>
            {
                var rawName = Path.GetFileNameWithoutExtension(path);
                var cleanName = rawName.StartsWith("ic_fluent_", StringComparison.OrdinalIgnoreCase)
                    ? rawName[10..]
                    : rawName;

                return new IconFile
                {
                    Name = cleanName.Replace('_', ' ').Replace('-', ' '),
                    FullPath = path,
                    Style = GuessStyle(path)
                };
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GuessStyle(string path)
    {
        string p = path.ToLowerInvariant();
        if (p.Contains("filled") || p.Contains("solid")) return "Filled";
        if (p.Contains("color")) return "Color";
        if (p.Contains("light")) return "Light";
        return "Regular";
    }
}
