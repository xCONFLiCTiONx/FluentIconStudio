using System;
using System.Collections.Generic;
using System.Linq;
using FluentIconStudio.Models;

namespace FluentIconStudio.Services;

public static class FastCatalogIndex
{
    private struct IndexedIcon
    {
        public IconFile Icon;
        public string LowerSearchText;
        public string Style;
    }

    private static IndexedIcon[] _items = Array.Empty<IndexedIcon>();

    public static void Index(IEnumerable<IconFile> icons)
    {
        _items = icons.Select(x => new IndexedIcon
        {
            Icon = x,
            LowerSearchText = x.SearchText.ToLowerInvariant(),
            Style = x.Style
        }).ToArray();
    }

    public static (IReadOnlyList<IconFile> Matches, int TotalMatchCount) Search(string query, string styleFilter, int offset = 0, int limit = 500)
    {
        if (_items.Length == 0)
            return (Array.Empty<IconFile>(), 0);

        query = query.Trim().ToLowerInvariant();
        bool hasQuery = query.Length > 0;
        bool hasStyle = !string.IsNullOrWhiteSpace(styleFilter) && !styleFilter.Equals("All styles", StringComparison.OrdinalIgnoreCase);

        var matches = new List<IconFile>();
        int matchCount = 0;

        for (int i = 0; i < _items.Length; i++)
        {
            ref readonly var item = ref _items[i];

            if (hasStyle && !item.Style.Equals(styleFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (hasQuery && !item.LowerSearchText.Contains(query, StringComparison.Ordinal))
                continue;

            if (matchCount >= offset && matches.Count < limit)
                matches.Add(item.Icon);

            matchCount++;
        }

        return (matches, matchCount);
    }
}
