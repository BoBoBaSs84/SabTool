using System.Text;

namespace SabTool.CLI.Commands;

using SabTool.CLI.Base;
using SabTool.Data.Packs;
using SabTool.Depot;
using SabTool.Serializers.Megapacks;
using SabTool.Serializers.Packs;

public sealed class MegapackCategory : BaseCategory
{
    public override string Key => "megapack";
    public override string Shortcut => "m";
    public override string Usage => "<sub command>";

    /// <summary>
    /// The relative file name of a megapack entry, as written by <c>megapack unpack</c>.
    /// </summary>
    private static string GetEntryFileName(FileEntry fileEntry)
    {
        var realFilePath = fileEntry.Path.GetString();
        if (string.IsNullOrEmpty(realFilePath))
            realFilePath = $"{fileEntry.Name.GetStringOrHexString()}.pack"; // TODO: based on the streamblock, can be dynpack, palettepack or pack

        return realFilePath;
    }

    public sealed class ListCommand : BaseCommand
    {
        public override string Key { get; } = "list";
        public override string Shortcut { get; } = "l";
        public override string Usage { get; } = "<game base path>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (!arguments.Any())
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Megapacks);

            var writer = Console.Out;

            if (arguments.Count() == 2)
                writer = new StreamWriter(arguments.ElementAt(1), false, Encoding.UTF8);

            foreach (var megapackFile in ResourceDepot.Instance.GetMegapacks())
            {
                writer.WriteLine($"Megapack: {megapackFile}");

                foreach (var fileEntry in ResourceDepot.Instance.GetFileEntries(megapackFile))
                    writer.WriteLine(fileEntry.ToPrettyString());

                writer.WriteLine();
                writer.WriteLine($"Mappings:");

                foreach (var (crc1, crc2) in ResourceDepot.Instance.GetBlockPathToNameCrcs(megapackFile))
                    writer.WriteLine($"{crc1} -> {crc2}");

                writer.WriteLine();
            }
            
            writer.Flush();

            if (writer != Console.Out)
                writer.Close();

            return true;
        }
    }

    public sealed class UnpackCommand : BaseCommand
    {
        public override string Key { get; } = "unpack";
        public override string Shortcut { get; } = "u";
        public override string Usage { get; } = "<game base path> <output directory> [relative megapack file path]";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 2)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            ResourceDepot.Instance.Initialize(arguments.ElementAt(0));
            ResourceDepot.Instance.Load(Resource.Megapacks);

            var outputDir = arguments.ElementAt(1);

            Directory.CreateDirectory(outputDir);

            string? fileToUnpack = null;

            if (arguments.Count() == 3)
                fileToUnpack = arguments.ElementAt(2);

            foreach (var megapackFile in ResourceDepot.Instance.GetMegapacks())
            {
                if (!string.IsNullOrEmpty(fileToUnpack) && megapackFile != fileToUnpack)
                    continue;

                Console.WriteLine($"Unpacking {megapackFile}...");
                
                var megapackOutputDir = Path.Combine(outputDir, megapackFile);

                var i = 0;

                foreach (var fileEntry in ResourceDepot.Instance.GetFileEntries(megapackFile))
                {
                    var entryStream = ResourceDepot.Instance.GetPackStream(fileEntry.Path);
                    if (entryStream == null)
                    {
                        Console.WriteLine($"Unable to unpack {fileEntry.Path}, no data fround for it!");
                        continue;
                    }

                    var outputFilePath = Path.Combine(megapackOutputDir, GetEntryFileName(fileEntry));

                    var outputFileDirectory = Path.GetDirectoryName(outputFilePath);
                    if (outputFileDirectory == null)
                    {
                        Console.WriteLine("ERROR: Output directory is invalid!");
                        return false;
                    }

                    Directory.CreateDirectory(outputFileDirectory);

                    using var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

                    entryStream.CopyTo(fs);

                    ++i;
                }

                Console.WriteLine($"Successfully unpacked {megapackFile} to {megapackOutputDir} ({i} files)!");
            }

            return true;
        }
    }

    public sealed class AnalyzeCommand : BaseCommand
    {
        public override string Key { get; } = "analyze";
        public override string Shortcut { get; } = "a";
        public override string Usage { get; } = "<game base path> <output csv>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 2)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            var gamePath = arguments.ElementAt(0);

            ResourceDepot.Instance.Initialize(gamePath);
            ResourceDepot.Instance.Load(Resource.Megapacks);

            using var writer = new StreamWriter(arguments.ElementAt(1), false, Encoding.UTF8);

            writer.WriteLine("megapack,tableIndex,dataIndex,path,name,offset,size,gapBefore,gapNonZeroBytes,offsetAlignment");

            foreach (var megapackFile in ResourceDepot.Instance.GetMegapacks())
            {
                using var fs = new FileStream(Path.Combine(gamePath, megapackFile), FileMode.Open, FileAccess.Read, FileShare.Read);

                var megapack = MegapackSerializer.DeserializeRaw(fs);
                var entries = megapack.EntriesInFileOrder;
                var dataOrder = entries.OrderBy(e => e.Offset).ToList();

                var headerSize = MegapackSerializer.GetHeaderSize(entries.Count);
                var previousEnd = headerSize;
                var overlaps = 0;
                var nonZeroGaps = 0;

                for (var i = 0; i < dataOrder.Count; ++i)
                {
                    var entry = dataOrder[i];
                    var gap = entry.Offset - previousEnd;

                    if (gap < 0)
                        ++overlaps;

                    var gapNonZeroBytes = gap > 0 ? CountNonZeroBytes(fs, previousEnd, gap) : 0;
                    if (gapNonZeroBytes > 0)
                        ++nonZeroGaps;

                    writer.WriteLine(string.Join(',',
                        $"\"{megapackFile}\"",
                        entries.IndexOf(entry),
                        i,
                        $"\"{entry.Path.GetStringOrHexString()}\"",
                        $"\"{entry.Name.GetStringOrHexString()}\"",
                        entry.Offset,
                        entry.Size,
                        gap,
                        gapNonZeroBytes,
                        MegapackSerializer.DetectAlignment(new[] { entry.Offset })));

                    previousEnd = Math.Max(previousEnd, entry.Offset + entry.Size);
                }

                var tableSorted = entries.SequenceEqual(dataOrder);
                var alignment = MegapackSerializer.DetectAlignment(entries.Select(e => e.Offset));
                var firstOffset = dataOrder.Count > 0 ? dataOrder[0].Offset : headerSize;

                Console.WriteLine($"{megapackFile}: {entries.Count} entries, header size 0x{headerSize:X}, first offset 0x{firstOffset:X}, alignment 0x{alignment:X}, table sorted by offset: {tableSorted}, overlaps: {overlaps}, gaps with non zero bytes: {nonZeroGaps}, trailing bytes: {fs.Length - previousEnd}");
            }

            return true;
        }

        private static long CountNonZeroBytes(Stream stream, long position, long length)
        {
            stream.Position = position;

            var buffer = new byte[Math.Min(length, 0x10000)];
            var count = 0L;

            while (length > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read <= 0)
                    break;

                for (var i = 0; i < read; ++i)
                    if (buffer[i] != 0)
                        ++count;

                length -= read;
            }

            return count;
        }
    }

    public sealed class ReplaceCommand : BaseCommand
    {
        public override string Key { get; } = "replace";
        public override string Shortcut { get; } = "r";
        public override string Usage { get; } = "<game base path> <relative megapack file path> <replacement directory> <output megapack file>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            if (arguments.Count() < 4)
            {
                Console.WriteLine("ERROR: Not enough arguments given!");
                return false;
            }

            var gamePath = arguments.ElementAt(0);
            var megapackPath = Path.Combine(gamePath, arguments.ElementAt(1));
            var replacementDir = arguments.ElementAt(2);
            var outputPath = arguments.ElementAt(3);

            if (!File.Exists(megapackPath))
            {
                Console.WriteLine($"ERROR: The megapack file {megapackPath} doesn't exist!");
                return false;
            }

            if (!Directory.Exists(replacementDir))
            {
                Console.WriteLine("ERROR: The replacement directory doesn't exist!");
                return false;
            }

            if (string.Equals(Path.GetFullPath(megapackPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("ERROR: The output file must differ from the original megapack file!");
                return false;
            }

            // Loading every megapack registers the same name hashes that "megapack unpack" used for the file names
            ResourceDepot.Instance.Initialize(gamePath);
            ResourceDepot.Instance.Load(Resource.Megapacks);

            using var input = new FileStream(megapackPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var megapack = MegapackSerializer.DeserializeRaw(input);

            var replacements = new Dictionary<FileEntry, string>();

            foreach (var entry in megapack.EntriesInFileOrder)
            {
                var replacementFile = Path.GetFullPath(Path.Combine(replacementDir, GetEntryFileName(entry)));
                if (File.Exists(replacementFile))
                    replacements.Add(entry, replacementFile);
            }

            var usedFiles = new HashSet<string>(replacements.Values, StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(replacementDir, "*.*", SearchOption.AllDirectories))
                if (!usedFiles.Contains(Path.GetFullPath(file)))
                    Console.WriteLine($"Unmatched: {file} has no entry in the megapack!");

            byte[] GetData(FileEntry entry)
            {
                if (replacements.TryGetValue(entry, out var replacementFile))
                {
                    Console.WriteLine($"Replacing {entry.Path} with {replacementFile}");
                    return File.ReadAllBytes(replacementFile);
                }

                var data = new byte[entry.Size];

                input.Position = entry.Offset;
                input.ReadExactly(data);

                return data;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                MegapackSerializer.SerializeRaw(megapack, GetData, output);

            Console.WriteLine($"Written {outputPath} ({replacements.Count} of {megapack.EntriesInFileOrder.Count} entries taken from {replacementDir})!");

            return true;
        }
    }

    public sealed class PackCommand : BaseCommand
    {
        public override string Key { get; } = "pack";
        public override string Shortcut { get; } = "p";
        public override string Usage { get; } = "<game base path> <input directory path>";

        public override bool Execute(IEnumerable<string> arguments)
        {
            return true;
        }
    }
}
