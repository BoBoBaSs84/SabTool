using System;
using System.IO;
using System.Text;

using Newtonsoft.Json;

namespace SabTool.Serializers.Megapacks;

using SabTool.Data.Packs;
using SabTool.Serializers.Packs;
using SabTool.Utils.Extensions;

public static class PackSerializer
{
    public static void DeserializeRaw(Stream stream, StreamBlock streamBlock)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        if (!reader.CheckHeaderString("SBLA", reversed: true))
            throw new Exception("Invalid pack header found!");

        StreamBlockSerializer.DeserializeHeader(streamBlock, stream);
    }

    /// <summary>
    /// Writes a pack. The payloads must be loaded (<see cref="StreamBlockSerializer.ReadPayloads"/>).
    /// Offsets and compressed sizes are recomputed from the payloads.
    /// </summary>
    public static void SerializeRaw(StreamBlock streamBlock, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        var startPosition = writer.BaseStream.Position;

        writer.WriteHeaderString("SBLA", reversed: true);

        var headerDataSizePosition = writer.BaseStream.Position;

        writer.Write(0u);

        if (streamBlock.HeaderDataSize > 0)
        {
            var headerDataStart = writer.BaseStream.Position;

            StreamBlockSerializer.WriteHeaderData(streamBlock, writer);

            var headerDataSize = (uint)(writer.BaseStream.Position - headerDataStart);

            writer.DoAtPosition(headerDataSizePosition, w => w.Write(headerDataSize));

            streamBlock.HeaderDataSize = headerDataSize;
        }

        StreamBlockSerializer.UpdateEntryOffsets(streamBlock);
        StreamBlockSerializer.WriteEntryTable(streamBlock, writer);

        streamBlock.HeaderEnd = (uint)(writer.BaseStream.Position - startPosition);

        StreamBlockSerializer.WritePayloads(streamBlock, writer);

        if (streamBlock.TrailingData != null)
            writer.Write(streamBlock.TrailingData);
    }

    public static void DeserializeJSON(Stream stream)
    {
    }

    public static void SerializeJSON(object value, Stream stream)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
    }
}
