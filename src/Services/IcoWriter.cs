using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FluentIconStudio.Services;

/// <summary>
/// Writes a Windows ICO containing PNG-compressed image entries. Standard sizes are capped at
/// 256px for broad compatibility. A 512px master is exported separately as PNG.
/// </summary>
public static class IcoWriter
{
    public static async Task WriteAsync(string outputPath, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        var entries = images
            .Where(x => x.Size is >= 1 and <= 256)
            .OrderBy(x => x.Size)
            .ToArray();

        if (entries.Length == 0)
            throw new InvalidOperationException("No supported ICO entries were supplied.");

        await using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Length);

        int offset = 6 + entries.Length * 16;
        foreach (var item in entries)
        {
            writer.Write((byte)(item.Size == 256 ? 0 : item.Size));
            writer.Write((byte)(item.Size == 256 ? 0 : item.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)item.Png.Length);
            writer.Write((uint)offset);
            offset += item.Png.Length;
        }

        foreach (var item in entries)
            writer.Write(item.Png);

        writer.Flush();
    }
}
