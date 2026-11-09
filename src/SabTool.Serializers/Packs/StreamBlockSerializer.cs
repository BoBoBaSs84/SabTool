using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

namespace SabTool.Serializers.Packs;

using SabTool.Data.Interfaces;
using SabTool.Data.Packs;
using SabTool.Serializers.Json.Converters;
using SabTool.Serializers.Misc;
using SabTool.Serializers.Packs.Assets;
using SabTool.Utils;
using SabTool.Utils.Extensions;

public static class StreamBlockSerializer
{
    [Flags]
    public enum SerializationFlags
    {
        None = 0,
        EntriesForOnlyIndex2 = 1
    }

    public static StreamBlock DeserializeFromMapRaw(Stream stream, SerializationFlags flags = SerializationFlags.None)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        var streamBlock = DeserializeBaseBlock(stream);

        if (streamBlock.Index == 2 || (flags & SerializationFlags.EntriesForOnlyIndex2) == SerializationFlags.None)
        {
            ReadTextureInfo(streamBlock, reader);
            ReadEntries(streamBlock, reader);
        }

        return streamBlock;
    }

    public static StreamBlock DeserializeBaseBlock(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        var streamBlock = new StreamBlock
        {
            Id = new(reader.ReadUInt32()),
            FileName = reader.ReadStringWithMaxLength(reader.ReadInt16())
        };

        streamBlock.Extents[0] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        streamBlock.Extents[1] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        streamBlock.UnkShort = reader.ReadInt16();
        streamBlock.Index = reader.ReadUInt16();

        return streamBlock;
    }

    public static void DeserializeHeader(StreamBlock streamBlock, Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        streamBlock.HeaderEnd = reader.ReadUInt32();
        streamBlock.HeaderDataSize = streamBlock.HeaderEnd;
        if (streamBlock.HeaderEnd > 0)
            ReadHeaderData(streamBlock, reader);

        streamBlock.HeaderEnd += 4;

        var offInd = 0;

        do
        {
            var off = StreamBlock.OffIndices[offInd];

            if (streamBlock.EntryCounts[off] == 0)
            {
                ++offInd;
                continue;
            }

            streamBlock.Entries[off] = new StreamBlock.Entry[streamBlock.EntryCounts[off]];

            for (var i = 0; i < streamBlock.EntryCounts[off]; ++i)
                streamBlock.Entries[off][i] = new StreamBlock.Entry(reader);

            streamBlock.HeaderEnd += 24 * streamBlock.EntryCounts[off];

            ++offInd;
        }
        while (offInd < 9);

        streamBlock.HeaderEnd += 4;
    }

    public static void ReadPayloads(StreamBlock streamBlock, Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        var offInd = 0;

        do
        {
            var off = StreamBlock.OffIndices[offInd];

            if (streamBlock.EntryCounts[off] == 0)
            {
                ++offInd;
                continue;
            }

            for (var i = 0; i < streamBlock.EntryCounts[off]; ++i)
            {
                var entry = streamBlock.Entries[off][i];

                // TODO: should be align up after each other, but who knows...
                //reader.BaseStream.Position = streamBlock.HeaderEnd + entry.Offset;

                entry.Payload = reader.ReadBytes(entry.CompressedSize);
            }

            ++offInd;
        }
        while (offInd < 9);

        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        streamBlock.TrailingData = remaining > 0 ? reader.ReadBytes((int)remaining) : Array.Empty<byte>();
    }

    public static void ReadHeaderData(StreamBlock streamBlock, BinaryReader reader)
    {
        streamBlock.HeightRangeMax = 1.0f;

        if ((streamBlock.Flags & 0x1C00) == 0x400)
        {
            if (!reader.CheckHeaderString("HEI1", reversed: true))
                throw new Exception("Invalid StreamBlock header found!");

            streamBlock.PointCountX = reader.ReadInt32();
            streamBlock.PointCountY = reader.ReadInt32();
            streamBlock.HeightRangeMin = reader.ReadSingle();
            streamBlock.HeightRangeMax = reader.ReadSingle();
            streamBlock.HeightMapData = reader.ReadBytes(streamBlock.PointCountX * streamBlock.PointCountY);
        }

        ReadTextureInfo(streamBlock, reader);
        ReadEntries(streamBlock, reader);
    }

    public static void ReadTextureInfo(StreamBlock streamBlock, BinaryReader reader)
    {
        // The same StreamBlock instance can be read more than once (map, then pack)
        streamBlock.TotalTextureSize = 0;

        streamBlock.TextureCount = reader.ReadUInt32();
        if (streamBlock.TextureCount > 0)
        {
            streamBlock.TextureInfoArray = new StreamBlock.TextureInfo[streamBlock.TextureCount];

            for (var i = 0; i < streamBlock.TextureCount; ++i)
            {
                streamBlock.TextureInfoArray[i] = new StreamBlock.TextureInfo
                {
                    Crc = new(reader.ReadUInt32()),
                    UncompressedSize = reader.ReadUInt32()
                };

                streamBlock.TotalTextureSize += streamBlock.TextureInfoArray[i].UncompressedSize;
            }
        }

        streamBlock.TextureCount2 = reader.ReadUInt32();
        if (streamBlock.TextureCount2 > 0)
        {
            streamBlock.TextureInfoArray2 = new StreamBlock.TextureInfo[streamBlock.TextureCount2];

            for (var i = 0; i < streamBlock.TextureCount2; ++i)
            {
                streamBlock.TextureInfoArray2[i] = new StreamBlock.TextureInfo
                {
                    Crc = new(reader.ReadUInt32()),
                    UncompressedSize = reader.ReadUInt32()
                };

                streamBlock.TotalTextureSize += streamBlock.TextureInfoArray2[i].UncompressedSize;
            }
        }
    }

    public static void ReadEntries(StreamBlock streamBlock, BinaryReader reader)
    {
        streamBlock.EntriesUnk = reader.ReadInt32();

        for (var i = 0; i < streamBlock.EntryCounts.Length; ++i)
            streamBlock.EntryCounts[i] = reader.ReadUInt32();

        streamBlock.Count1ACFor1B0And1B4_1AC = reader.ReadUInt32();
        if (streamBlock.Count1ACFor1B0And1B4_1AC > 0)
        {
            streamBlock.Array1B0 = new Crc[streamBlock.Count1ACFor1B0And1B4_1AC];
            streamBlock.Array1B4 = new byte[streamBlock.Count1ACFor1B0And1B4_1AC];

            for (var i = 0; i < streamBlock.Count1ACFor1B0And1B4_1AC; ++i)
            {
                streamBlock.Array1B0[i] = new(reader.ReadUInt32());
                streamBlock.Array1B4[i] = 0;
            }
        }

        streamBlock.PaletteCount = (ushort)reader.ReadInt32();
        for (var i = 0; i < streamBlock.PaletteCount; ++i)
            streamBlock.Palettes[i] = new Crc(reader.ReadUInt32());

        // The same StreamBlock instance can be read more than once (map, then pack)
        streamBlock.FenceTree.Clear();
        streamBlock.FenceTreeList.Clear();

        streamBlock.FenceTreeCount = reader.ReadInt32();
        for (var i = 0; i < streamBlock.FenceTreeCount; ++i)
        {
            var crc = new Crc(reader.ReadUInt32());
            var subCnt = reader.ReadInt32();
            var children = reader.ReadConstArray(subCnt, () => new Crc(reader.ReadUInt32()));

            streamBlock.FenceTreeList.Add(new(crc, children));

            if (!streamBlock.FenceTree.TryAdd(crc, children))
                Console.WriteLine("Trying to add multiple fence tree by the same id!");
        }
    }

    public static void WriteHeaderData(StreamBlock streamBlock, BinaryWriter writer)
    {
        if ((streamBlock.Flags & 0x1C00) == 0x400)
        {
            writer.WriteHeaderString("HEI1", reversed: true);
            writer.Write(streamBlock.PointCountX);
            writer.Write(streamBlock.PointCountY);
            writer.Write(streamBlock.HeightRangeMin);
            writer.Write(streamBlock.HeightRangeMax);
            writer.Write(streamBlock.HeightMapData, 0, streamBlock.PointCountX * streamBlock.PointCountY);
        }

        WriteTextureInfo(streamBlock, writer);
        WriteEntries(streamBlock, writer);
    }

    public static void WriteTextureInfo(StreamBlock streamBlock, BinaryWriter writer)
    {
        writer.Write(streamBlock.TextureCount);
        for (var i = 0; i < streamBlock.TextureCount; ++i)
        {
            writer.Write(streamBlock.TextureInfoArray[i].Crc.Value);
            writer.Write(streamBlock.TextureInfoArray[i].UncompressedSize);
        }

        writer.Write(streamBlock.TextureCount2);
        for (var i = 0; i < streamBlock.TextureCount2; ++i)
        {
            writer.Write(streamBlock.TextureInfoArray2[i].Crc.Value);
            writer.Write(streamBlock.TextureInfoArray2[i].UncompressedSize);
        }
    }

    public static void WriteEntries(StreamBlock streamBlock, BinaryWriter writer)
    {
        writer.Write(streamBlock.EntriesUnk);

        for (var i = 0; i < streamBlock.EntryCounts.Length; ++i)
            writer.Write(streamBlock.EntryCounts[i]);

        writer.Write(streamBlock.Count1ACFor1B0And1B4_1AC);
        for (var i = 0; i < streamBlock.Count1ACFor1B0And1B4_1AC; ++i)
            writer.Write(streamBlock.Array1B0[i].Value);

        writer.Write((int)streamBlock.PaletteCount);
        for (var i = 0; i < streamBlock.PaletteCount; ++i)
            writer.Write(streamBlock.Palettes[i].Value);

        writer.Write(streamBlock.FenceTreeList.Count);
        foreach (var (crc, children) in streamBlock.FenceTreeList)
        {
            writer.Write(crc.Value);
            writer.Write(children.Length);

            foreach (var child in children)
                writer.Write(child.Value);
        }
    }

    /// <summary>
    /// Writes the 24 byte entries in <see cref="StreamBlock.OffIndices"/> order.
    /// </summary>
    public static void WriteEntryTable(StreamBlock streamBlock, BinaryWriter writer)
    {
        foreach (var entry in EnumerateEntries(streamBlock))
            entry.Write(writer);
    }

    /// <summary>
    /// Writes the payloads back to back, in the same order as <see cref="ReadPayloads"/> reads them.
    /// </summary>
    public static void WritePayloads(StreamBlock streamBlock, BinaryWriter writer)
    {
        foreach (var entry in EnumerateEntries(streamBlock))
            writer.Write(entry.Payload);
    }

    /// <summary>
    /// Recomputes the offsets and compressed sizes from the loaded payloads. The payloads are laid out back to back,
    /// starting at the offset of the first entry, the same way <see cref="ReadPayloads"/> reads them.
    /// </summary>
    public static void UpdateEntryOffsets(StreamBlock streamBlock)
    {
        int? offset = null;

        foreach (var entry in EnumerateEntries(streamBlock))
        {
            if (entry.Payload == null)
                throw new InvalidOperationException($"The payload of {entry.Crc} is not loaded!");

            offset ??= entry.Offset;

            entry.Offset = offset.Value;
            entry.CompressedSize = entry.Payload.Length;

            offset += entry.Payload.Length;
        }
    }

    /// <summary>
    /// Enumerates the entries in file order (<see cref="StreamBlock.OffIndices"/>).
    /// </summary>
    public static IEnumerable<StreamBlock.Entry> EnumerateEntries(StreamBlock streamBlock)
    {
        foreach (var off in StreamBlock.OffIndices)
        {
            if (streamBlock.EntryCounts[off] == 0)
                continue;

            for (var i = 0; i < streamBlock.EntryCounts[off]; ++i)
                yield return streamBlock.Entries[off][i];
        }
    }

    /// <summary>
    /// Replaces the payload of a texture entry and updates the dependent sizes.
    /// </summary>
    /// <remarks>
    /// The size rules are inferred from the original values until <c>pack analyze</c> confirms them:
    /// a size that equals the payload length follows the payload length, any other size moves by the change of the texture's data size.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The block's texture info comes from the map and would have to change.</exception>
    public static void ReplaceTexturePayload(StreamBlock streamBlock, StreamBlock.Entry entry, byte[] payload)
    {
        var oldPayload = entry.Payload ?? throw new InvalidOperationException($"The payload of {entry.Crc} is not loaded!");

        var oldDataSize = TextureAssetSerializer.PeekDataSize(oldPayload) ?? throw new InvalidOperationException($"The original payload of {entry.Crc} is not a texture!");
        var newDataSize = TextureAssetSerializer.PeekDataSize(payload) ?? throw new InvalidOperationException($"The replacement payload of {entry.Crc} is not a texture!");

        var oldUncompressedSize = entry.UncompressedSize;
        var newUncompressedSize = oldUncompressedSize == oldPayload.Length
            ? payload.Length
            : oldUncompressedSize + newDataSize - oldDataSize;

        var textureInfos = (streamBlock.TextureInfoArray ?? Array.Empty<StreamBlock.TextureInfo>())
            .Concat(streamBlock.TextureInfoArray2 ?? Array.Empty<StreamBlock.TextureInfo>())
            .Where(ti => ti.Crc == entry.Crc)
            .ToArray();

        var newInfoSizes = textureInfos
            .Select(ti => ti.UncompressedSize == (uint)oldUncompressedSize
                ? (uint)newUncompressedSize
                : (uint)(ti.UncompressedSize + newDataSize - oldDataSize))
            .ToArray();

        var infoChanges = textureInfos.Where((ti, i) => ti.UncompressedSize != newInfoSizes[i]).Any();
        if (infoChanges && streamBlock.HeaderDataSize == 0)
            throw new InvalidOperationException($"The texture info of {entry.Crc} lives in the map and its size would change, writing maps is not supported!");

        for (var i = 0; i < textureInfos.Length; ++i)
        {
            streamBlock.TotalTextureSize = streamBlock.TotalTextureSize - textureInfos[i].UncompressedSize + newInfoSizes[i];
            textureInfos[i].UncompressedSize = newInfoSizes[i];
        }

        entry.UncompressedSize = newUncompressedSize;
        entry.Payload = payload;
    }

    /// <summary>
    /// Writes the header data part only (the counterpart of <see cref="ReadHeaderData"/>).
    /// </summary>
    public static void SerializeRaw(StreamBlock streamBlock, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        WriteHeaderData(streamBlock, writer);
    }

    public static StreamBlock? DeserializeJSON(Stream stream)
    {
        return null;
    }

    public static void SerializeJSON(StreamBlock streamBlock, Stream stream)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(JsonConvert.SerializeObject(streamBlock, Formatting.Indented, new CrcConverter()));
    }

    public static void Export(StreamBlock streamBlock, string outputPath, IProgress<string> progress, IBlueprintDepot depot)
    {
        Directory.CreateDirectory(outputPath);

        var offInd = 0;

        do
        {
            var off = StreamBlock.OffIndices[offInd];

            if (streamBlock.EntryCounts[off] == 0)
            {
                ++offInd;
                continue;
            }

            for (var i = 0; i < streamBlock.EntryCounts[off]; ++i)
            {
                var entry = streamBlock.Entries[off][i];

                var extension = "bin";

                try
                {
                    switch (off)
                    {
                        case 0:
                            extension = "mesh-bin";

                            var meshAsset = MeshAssetSerializer.DeserializeRaw(new MemoryStream(entry.Payload, false));

                            meshAsset.Export(outputPath);

                            progress.Report(meshAsset.ModelName);
                            continue;

                        case 1:
                            extension = "texture-bin";

                            var textureAsset = TextureAssetSerializer.DeserializeRaw(new MemoryStream(entry.Payload, false), entry.Crc);
                            if (textureAsset is not null)
                            {
                                textureAsset.Export(outputPath);

                                using (var fs = new FileStream(Path.Combine(outputPath, $"{textureAsset.Name}.texture.json"), FileMode.Create, FileAccess.Write, FileShare.Read))
                                    TextureAssetSerializer.SerializeMetadataJSON(textureAsset.Metadata, fs);

                                progress.Report(textureAsset.Name ?? "");

                                continue;
                            }
                            
                            break;

                        case 2:
                            extension = "physics-bin";

                            var physicsAsset = PhysicsAssetSerializer.DeserializeRaw(entry);
                            if (physicsAsset is not null)
                            {
                                physicsAsset.Export(outputPath);

                                continue;
                            }
                            
                            break;

                        case 3:
                            extension = "pathgraph";

                            Console.Error.WriteLine("PATHGRAPH! {0}", string.IsNullOrWhiteSpace(entry.Crc.GetString()) ? $"0x{entry.Crc.Value:X8}.{extension}" : $"{entry.Crc.GetString()}.{extension}");
                            break;

                        case 4:
                            extension = "aifence";
                            Console.Error.WriteLine("AIFENCE! {0}", string.IsNullOrWhiteSpace(entry.Crc.GetString()) ? $"0x{entry.Crc.Value:X8}.{extension}" : $"{entry.Crc.GetString()}.{extension}");
                            break;

                        case 5:
                            extension = "unknown";
                            Console.Error.WriteLine("UNK! {0}", string.IsNullOrWhiteSpace(entry.Crc.GetString()) ? $"0x{entry.Crc.Value:X8}.{extension}" : $"{entry.Crc.GetString()}.{extension}");
                            break;

                        case 6:
                            extension = "soundbank";
                            Console.Error.WriteLine("SOUNDBANK! {0}", string.IsNullOrWhiteSpace(entry.Crc.GetString()) ? $"0x{entry.Crc.Value:X8}.{extension}" : $"{entry.Crc.GetString()}.{extension}");
                            break;

                        case 7:
                            extension = "gfx";
                            break;

                        case 8:
                            extension = "wsd-bin";

                            var dictOutPath = Path.Combine(outputPath, entry.SpawnTag.Value == 0 ? "nodes.wsd.json" : $"{entry.SpawnTag.GetStringOrHexString()}.wsd.json");

                            var dict = EditNodesSerializer.DeserializeRaw(new MemoryStream(entry.Payload, false), depot);

                            using (var fs = new FileStream(dictOutPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                                EditNodesSerializer.SerializeJSON(dict, fs);

                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Unable to export {entry.Crc.GetStringOrHexString()}! Exporting as raw! Exception:");
                    Console.Error.WriteLine(ex.ToString());
                }

                var outputFileName = $"{entry.Crc.GetStringOrHexString()}.{extension}";
                var outputFilePath = Path.Combine(outputPath, outputFileName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

                File.WriteAllBytes(outputFilePath, entry.Payload);

                progress.Report(outputFileName);
            }

            ++offInd;
        }
        while (offInd < 9);
    }

}
