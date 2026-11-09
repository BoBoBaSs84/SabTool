using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

namespace SabTool.Serializers.Megapacks;

using SabTool.Data.Packs;
using SabTool.Utils;
using SabTool.Utils.Extensions;

public static class MegapackSerializer
{
    public static Megapack DeserializeRaw(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        if (!reader.CheckHeaderString("MP00", reversed: true))
            throw new Exception("Invalid MegaPack header found!");

        var megapack = new Megapack
        {
            FileCount = reader.ReadUInt32()
        };

        for (var i = 0; i < megapack.FileCount; ++i)
        {
            var entry = DeserializeFileEntryRaw(reader);

            // Store the hashes
            if (!string.IsNullOrEmpty(entry.Name.GetString()))
            {
                Hash.StringToHash($"global\\{entry.Name.GetString()}.dynpack");
                Hash.StringToHash($"global\\{entry.Name.GetString()}.palettepack");
            }

            megapack.FileEntries.Add(entry.Path, entry);
            megapack.EntriesInFileOrder.Add(entry);
        }

        megapack.BlockPathToNameCrcs = new Tuple<Crc, Crc>[megapack.FileCount];

        for (var i = 0; i < megapack.FileCount; ++i)
        {
            megapack.BlockPathToNameCrcs[i] = new(new(reader.ReadUInt32()), new(reader.ReadUInt32()));

            if (!megapack.FileEntries.ContainsKey(megapack.BlockPathToNameCrcs[i].Item1))
            {
                Console.WriteLine($"ERROR: {megapack.BlockPathToNameCrcs[i].Item1} => {megapack.BlockPathToNameCrcs[i].Item2} is not a valid fileentry!");
                continue;
            }

            var entry = megapack.FileEntries[megapack.BlockPathToNameCrcs[i].Item1];
            if (entry.Path == megapack.BlockPathToNameCrcs[i].Item1 && entry.Name == megapack.BlockPathToNameCrcs[i].Item2)
            {
                continue;
            }

            Console.WriteLine($"BlockPathToNameCrcs Crc mismatch! {entry.Path} != {megapack.BlockPathToNameCrcs[i].Item1} || {entry.Name} != {megapack.BlockPathToNameCrcs[i].Item2}");
        }

        return megapack;
    }

    private static FileEntry DeserializeFileEntryRaw(BinaryReader reader)
    {
        return new FileEntry
        {
            Path = new(reader.ReadUInt32()),
            Name = new(reader.ReadUInt32()),
            Size = reader.ReadInt32(),
            Offset = reader.ReadInt64()
        };
    }

    /// <summary>
    /// The size of the header, the file table and the path to name table.
    /// </summary>
    public static long GetHeaderSize(int fileCount) => 4 + 4 + fileCount * (4 + 4 + 4 + 8) + fileCount * (4 + 4);

    /// <summary>
    /// The largest power of two (up to <paramref name="maxAlignment"/>) that divides every offset.
    /// </summary>
    public static int DetectAlignment(IEnumerable<long> offsets, int maxAlignment = 0x800)
    {
        var alignment = maxAlignment;

        foreach (var offset in offsets)
            while (alignment > 1 && offset % alignment != 0)
                alignment >>= 1;

        return alignment;
    }

    /// <summary>
    /// Writes a megapack. The file data is written in the original offset order, aligned the same way as the original file.
    /// Sizes and offsets of the entries are recomputed.
    /// </summary>
    /// <param name="getData">Returns the data of an entry. It is called before the entry's size and offset are updated.</param>
    public static void SerializeRaw(Megapack megapack, Func<FileEntry, byte[]> getData, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        var entries = megapack.EntriesInFileOrder;

        var startPosition = writer.BaseStream.Position;
        var headerSize = GetHeaderSize(entries.Count);

        var dataOrder = entries.OrderBy(e => e.Offset).ToList();
        var alignment = DetectAlignment(entries.Select(e => e.Offset));
        var firstOffset = dataOrder.Count > 0 ? Math.Max(dataOrder[0].Offset, headerSize) : headerSize;

        // Data first, the tables are written after the sizes and offsets are known
        writer.BaseStream.Position = startPosition + headerSize;

        WritePadding(writer, startPosition + firstOffset);

        foreach (var entry in dataOrder)
        {
            WritePadding(writer, startPosition + Align(writer.BaseStream.Position - startPosition, alignment));

            var data = getData(entry);

            entry.Offset = writer.BaseStream.Position - startPosition;
            entry.Size = data.Length;

            writer.Write(data);
        }

        var endPosition = writer.BaseStream.Position;

        writer.BaseStream.Position = startPosition;

        writer.WriteHeaderString("MP00", reversed: true);
        writer.Write(entries.Count);

        foreach (var entry in entries)
        {
            writer.Write(entry.Path.Value);
            writer.Write(entry.Name.Value);
            writer.Write(entry.Size);
            writer.Write(entry.Offset);
        }

        foreach (var (path, name) in megapack.BlockPathToNameCrcs)
        {
            writer.Write(path.Value);
            writer.Write(name.Value);
        }

        writer.BaseStream.Position = endPosition;

        megapack.FileCount = (uint)entries.Count;
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void WritePadding(BinaryWriter writer, long targetPosition)
    {
        while (writer.BaseStream.Position < targetPosition)
            writer.Write((byte)0);
    }

    public static Megapack? DeserializeJSON(Stream stream)
    {
        return null;
    }

    public static void SerializeJSON(Megapack globalMegaFile, Stream stream)
    {
        var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(JsonConvert.SerializeObject(globalMegaFile));
    }
}
