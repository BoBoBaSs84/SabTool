using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using ShellProgressBar;

namespace SabTool.CLI.Commands;

using SabTool.CLI.Base;
using SabTool.Data.Packs;
using SabTool.Data.Packs.Assets;
using SabTool.Depot;
using SabTool.Serializers.Megapacks;
using SabTool.Serializers.Packs;
using SabTool.Serializers.Packs.Assets;
using SabTool.Utils;

public sealed class PackCategory : BaseCategory
{
    public override string Key { get; } = "pack";
    public override string Shortcut { get; } = "p";
    public override string Usage { get; } = "<sub command>";

    private static readonly Regex ChunkRegex = new(@"[fF]rance\\(?:(?:\d\d\\\d+)|(?:\d+))\.[pP]ack$", RegexOptions.Compiled);

    /// <summary>
    /// Finds the StreamBlock of a pack file: the France grid id, a 0x hex name or the hashed name.
    /// </summary>
    private static StreamBlock? ResolveStreamBlock(string packPath, out Crc crc)
    {
        var fileName = Path.GetFileNameWithoutExtension(packPath);

        if (ChunkRegex.IsMatch(packPath.ToLowerInvariant()))
            crc = new Crc(uint.Parse(fileName));
        else if (fileName.StartsWith("0x"))
            crc = new Crc(uint.Parse(fileName[2..], NumberStyles.HexNumber));
        else
            crc = new Crc(Hash.StringToHash(fileName));

        return ResourceDepot.Instance.GetStreamBlock(crc);
    }

    /// <summary>
    /// Loads a .dds file with its .texture.json metadata (if present) and converts it to a raw texture payload.
    /// </summary>
    private static TextureAsset LoadTextureAsset(string ddsPath)
    {
        var textureAsset = TextureAsset.Import(ddsPath);

        var metadataPath = Path.Combine(Path.GetDirectoryName(ddsPath)!, $"{textureAsset.Name}.texture.json");
        if (File.Exists(metadataPath))
        {
            using var metadataStream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            textureAsset.Metadata = TextureAssetSerializer.DeserializeMetadataJSON(metadataStream);
        }

        return textureAsset;
    }

    public sealed class UnpackCommand : BaseCommand
    {
        public override string Key { get; } = "unpack";
        public override string Shortcut { get; } = "u";
        public override string Usage { get; } = "<game base path> <packs file path> <output directory>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 3)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Maps);

            var packsBaseDir = arguments.ElementAt(1);
            var outputDir = arguments.ElementAt(2);
            if (outputDir == null)
            {
                Console.WriteLine("ERROR: No output directory is given!");
                return false;
            }

            Directory.CreateDirectory(outputDir);

            var consoleError = Console.Error;

            using var logWriter = new StreamWriter(new FileStream(Path.Combine(outputDir, "packlog.txt"), FileMode.Create, FileAccess.Write, FileShare.Read));

            Console.SetError(logWriter);

            var files = Directory.GetFiles(packsBaseDir, "*.*", SearchOption.AllDirectories);

            var options = new ProgressBarOptions
            {
                EnableTaskBarProgress = true,
                ForegroundColor = ConsoleColor.White,
                CollapseWhenFinished = true,
                ProgressBarOnBottom = true
            };

            var childOptions = new ProgressBarOptions
            {
                ForegroundColor = ConsoleColor.White,
                CollapseWhenFinished = true,
                ProgressBarOnBottom = true
            };

            using var progressBar = new ProgressBar(files.Length, "Unpacking packs...", options);

            foreach (var pack in files)
            {
                ProcessPack(pack, Path.Combine(outputDir, Path.GetDirectoryName(pack)![(packsBaseDir.Length + 1)..]), progressBar, childOptions);

                progressBar.Tick();
            }

            Console.SetError(consoleError);

            return true;
        }

        private static void ProcessPack(string filePath, string outputDir, ProgressBar progressBar, ProgressBarOptions childOptions)
        {
            var streamBlock = ResolveStreamBlock(filePath, out var crc);
            if (streamBlock == null)
            {
                Console.WriteLine($"Could not find StreamBlock for {crc} ({filePath})");
                return;
            }

            outputDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(filePath));
            outputDir = outputDir.Replace(" ", "");

            Directory.CreateDirectory(outputDir);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            using var child = progressBar.Spawn(streamBlock.EntryCounts.Select(c => (int)c).Sum(), filePath, childOptions);

            PackSerializer.DeserializeRaw(fs, streamBlock);

            StreamBlockSerializer.ReadPayloads(streamBlock, fs);
            StreamBlockSerializer.Export(streamBlock, outputDir, child.AsProgress<string>(), ResourceDepot.Instance);

            streamBlock.FreePayloads();
        }
    }

    public sealed class TextureImportCommand : BaseCommand
    {
        public override string Key { get; } = "texture-import";
        public override string Shortcut { get; } = "ti";
        public override string Usage { get; } = "<input .dds file or directory> <output directory>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 2)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            var inputPath = arguments.ElementAt(0);
            var outputDir = arguments.ElementAt(1);

            string[] files;
            if (File.Exists(inputPath))
                files = new[] { inputPath };
            else if (Directory.Exists(inputPath))
                files = Directory.GetFiles(inputPath, "*.dds", SearchOption.AllDirectories);
            else
            {
                Console.WriteLine("ERROR: The input path doesn't exist!");
                return false;
            }

            Directory.CreateDirectory(outputDir);

            foreach (var file in files)
            {
                var textureAsset = LoadTextureAsset(file);

                try
                {
                    using var fs = new FileStream(Path.Combine(outputDir, $"{textureAsset.Name}.texture-bin"), FileMode.Create, FileAccess.Write, FileShare.None);
                    TextureAssetSerializer.SerializeRaw(textureAsset, fs);

                    Console.WriteLine($"Imported {file}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Unable to import {file}: {ex.Message}");
                }
            }

            return true;
        }
    }

    public sealed class AnalyzeCommand : BaseCommand
    {
        public override string Key { get; } = "analyze";
        public override string Shortcut { get; } = "a";
        public override string Usage { get; } = "<game base path> <packs directory> <output csv>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 3)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            var packsDir = arguments.ElementAt(1);
            var outputCsv = arguments.ElementAt(2);
            var texturesCsv = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputCsv))!, $"{Path.GetFileNameWithoutExtension(outputCsv)}.textures.csv");

            if (!Directory.Exists(packsDir))
            {
                Console.WriteLine("ERROR: The packs directory doesn't exist!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Maps);

            using var packWriter = new StreamWriter(outputCsv, false, Encoding.UTF8);
            using var textureWriter = new StreamWriter(texturesCsv, false, Encoding.UTF8);

            packWriter.WriteLine("file,crc,headerDataSize,headerEnd,entries,firstOffset,contiguous,maxGap,offsetAlignment,trailingBytes,fenceTrees,duplicateFenceCrcs,roundTripIdentical,firstDifference");
            textureWriter.WriteLine("file,crc,mapBacked,offset,compressedSize,uncompressedSize,payloadLength,dataSize,int10,textureInfoSizes,uncompressedIsPayloadLength,uncompressedMinusDataSize");

            int analyzed = 0, contiguous = 0, identical = 0;

            foreach (var file in Directory.GetFiles(packsDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    var result = AnalyzePack(file, packWriter, textureWriter);
                    if (result == null)
                        continue;

                    ++analyzed;

                    if (result.Value.Contiguous)
                        ++contiguous;

                    if (result.Value.Identical)
                        ++identical;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Unable to analyze {file}: {ex.Message}");
                }
            }

            Console.WriteLine($"Analyzed {analyzed} packs: {contiguous} with contiguous offsets, {identical} byte identical after a round trip.");
            Console.WriteLine($"Written {outputCsv} and {texturesCsv}");

            return true;
        }

        private static (bool Contiguous, bool Identical)? AnalyzePack(string file, TextWriter packWriter, TextWriter textureWriter)
        {
            var streamBlock = ResolveStreamBlock(file, out var crc);
            if (streamBlock == null)
            {
                Console.WriteLine($"Could not find StreamBlock for {crc} ({file})");
                return null;
            }

            var original = File.ReadAllBytes(file);

            using (var ms = new MemoryStream(original, false))
            {
                PackSerializer.DeserializeRaw(ms, streamBlock);
                StreamBlockSerializer.ReadPayloads(streamBlock, ms);
            }

            var entries = StreamBlockSerializer.EnumerateEntries(streamBlock).ToList();

            var firstOffset = entries.Count > 0 ? entries[0].Offset : 0;
            var contiguous = true;
            var maxGap = 0;
            var expectedOffset = firstOffset;

            foreach (var entry in entries)
            {
                if (entry.Offset != expectedOffset)
                {
                    contiguous = false;
                    maxGap = Math.Max(maxGap, Math.Abs(entry.Offset - expectedOffset));
                }

                expectedOffset = entry.Offset + entry.CompressedSize;
            }

            var alignment = MegapackSerializer.DetectAlignment(entries.Select(e => (long)e.Offset));
            var duplicateFenceCrcs = streamBlock.FenceTreeList.Count - streamBlock.FenceTreeList.Select(p => p.Key).Distinct().Count();
            var mapBacked = streamBlock.HeaderDataSize == 0;
            var headerDataSize = streamBlock.HeaderDataSize;
            var headerEnd = streamBlock.HeaderEnd;

            foreach (var entry in streamBlock.Entries[1] ?? Array.Empty<StreamBlock.Entry>())
            {
                var dataSize = TextureAssetSerializer.PeekDataSize(entry.Payload);

                var infoSizes = (streamBlock.TextureInfoArray ?? Array.Empty<StreamBlock.TextureInfo>())
                    .Concat(streamBlock.TextureInfoArray2 ?? Array.Empty<StreamBlock.TextureInfo>())
                    .Where(ti => ti.Crc == entry.Crc)
                    .Select(ti => ti.UncompressedSize.ToString(CultureInfo.InvariantCulture));

                textureWriter.WriteLine(string.Join(',',
                    Csv(file),
                    entry.Crc.GetHexString(),
                    mapBacked,
                    entry.Offset,
                    entry.CompressedSize,
                    entry.UncompressedSize,
                    entry.Payload.Length,
                    dataSize?.ToString(CultureInfo.InvariantCulture) ?? "",
                    entry.Int10,
                    string.Join('|', infoSizes),
                    entry.UncompressedSize == entry.Payload.Length,
                    dataSize.HasValue ? (entry.UncompressedSize - dataSize.Value).ToString(CultureInfo.InvariantCulture) : ""));
            }

            byte[] rewritten;

            using (var ms = new MemoryStream())
            {
                PackSerializer.SerializeRaw(streamBlock, ms);
                rewritten = ms.ToArray();
            }

            streamBlock.FreePayloads();

            var firstDifference = FindFirstDifference(original, rewritten);
            var identical = firstDifference == -1;

            packWriter.WriteLine(string.Join(',',
                Csv(file),
                crc.GetHexString(),
                headerDataSize,
                headerEnd,
                entries.Count,
                firstOffset,
                contiguous,
                maxGap,
                alignment,
                streamBlock.TrailingData?.Length ?? 0,
                streamBlock.FenceTreeList.Count,
                duplicateFenceCrcs,
                identical,
                identical ? "" : $"0x{firstDifference:X}"));

            return (contiguous, identical);
        }

        private static long FindFirstDifference(byte[] left, byte[] right)
        {
            var length = Math.Min(left.Length, right.Length);

            for (var i = 0; i < length; ++i)
                if (left[i] != right[i])
                    return i;

            return left.Length == right.Length ? -1 : length;
        }

        private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    }

    public sealed class ReplaceCommand : BaseCommand
    {
        public override string Key { get; } = "replace";
        public override string Shortcut { get; } = "r";
        public override string Usage { get; } = "<game base path> <original pack file> <replacement directory> <output pack file>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 4)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            var packPath = arguments.ElementAt(1);
            var replacementDir = arguments.ElementAt(2);
            var outputPath = arguments.ElementAt(3);

            if (!File.Exists(packPath))
            {
                Console.WriteLine("ERROR: The original pack file doesn't exist!");
                return false;
            }

            if (!Directory.Exists(replacementDir))
            {
                Console.WriteLine("ERROR: The replacement directory doesn't exist!");
                return false;
            }

            if (string.Equals(Path.GetFullPath(packPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("ERROR: The output file must differ from the original pack file!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Maps);

            var streamBlock = ResolveStreamBlock(packPath, out var crc);
            if (streamBlock == null)
            {
                Console.WriteLine($"ERROR: Could not find StreamBlock for {crc} ({packPath})");
                return true;
            }

            using (var fs = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PackSerializer.DeserializeRaw(fs, streamBlock);
                StreamBlockSerializer.ReadPayloads(streamBlock, fs);
            }

            var textureEntries = streamBlock.Entries[1] ?? Array.Empty<StreamBlock.Entry>();
            var replacedEntries = new HashSet<StreamBlock.Entry>();
            var unmatched = new List<string>();
            var failed = false;

            foreach (var file in Directory.GetFiles(replacementDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var extension = Path.GetExtension(file).ToLowerInvariant();

                var candidates = new HashSet<uint>();

                if (name.StartsWith("0x") && uint.TryParse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexCrc))
                    candidates.Add(hexCrc);
                else
                    candidates.Add(Hash.FNV32string(name, addToLookup: false));

                byte[] payload;

                try
                {
                    switch (extension)
                    {
                        case ".texture-bin":
                            payload = File.ReadAllBytes(file);
                            break;

                        case ".dds":
                            var textureAsset = LoadTextureAsset(file);

                            if (!string.IsNullOrEmpty(textureAsset.Metadata?.OriginalName))
                                candidates.Add(Hash.FNV32string(textureAsset.Metadata.OriginalName, addToLookup: false));

                            using (var ms = new MemoryStream())
                            {
                                TextureAssetSerializer.SerializeRaw(textureAsset, ms);
                                payload = ms.ToArray();
                            }
                            break;

                        case ".json" when file.EndsWith(".texture.json", StringComparison.OrdinalIgnoreCase):
                            continue;

                        default:
                            Console.WriteLine($"Skipping {file}, only .dds and .texture-bin files are supported.");
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Unable to load {file}: {ex.Message}");
                    failed = true;
                    continue;
                }

                var entry = textureEntries.FirstOrDefault(e => candidates.Contains(e.Crc.Value));
                if (entry == null)
                {
                    unmatched.Add(file);
                    continue;
                }

                if (!replacedEntries.Add(entry))
                    Console.WriteLine($"WARNING: {entry.Crc} is replaced more than once, {file} wins!");

                try
                {
                    StreamBlockSerializer.ReplaceTexturePayload(streamBlock, entry, payload);

                    Console.WriteLine($"Replaced {entry.Crc} with {file}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"ERROR: Unable to replace {entry.Crc} with {file}: {ex.Message}");
                    failed = true;
                }
            }

            foreach (var file in unmatched)
                Console.WriteLine($"Unmatched: {file} has no texture entry in the pack!");

            if (failed)
            {
                Console.WriteLine($"ERROR: Not writing {outputPath} because of the errors above!");
                streamBlock.FreePayloads();
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                PackSerializer.SerializeRaw(streamBlock, fs);

            streamBlock.FreePayloads();

            Console.WriteLine($"Written {outputPath} ({replacedEntries.Count} replaced, {unmatched.Count} unmatched)!");

            return true;
        }
    }

    public sealed class DumpEditNodesCommand : BaseCommand
    {
        public override string Key { get; } = "dump-editnodes";
        public override string Shortcut { get; } = "de";
        public override string Usage { get; } = "<game base path> <output directory path>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (!arguments.Any())
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Maps);

            var outputDir = arguments.ElementAt(1);

            Directory.CreateDirectory(outputDir);

            using var fs = new FileStream(Path.Combine(outputDir, "editnodes-dump.json"), FileMode.Create, FileAccess.Write, FileShare.None);
            EditNodesSerializer.SerializeJSON(ResourceDepot.Instance.EditNodes, fs);

            using var fs2 = new FileStream(Path.Combine(outputDir, "editnodes-dlc-dump.json"), FileMode.Create, FileAccess.Write, FileShare.None);
            EditNodesSerializer.SerializeJSON(ResourceDepot.Instance.DLCEditNodes, fs2);

            return true;
        }
    }
}
