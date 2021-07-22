﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SabTool.Data.Packs
{
    using Utils;
    using Utils.Extensions;

    public class GlobalMegaFile
    {
        public Dictionary<Crc, FileEntry> FileEntries { get; set; } = new();
        public uint FileCount { get; set; }
        public uint[] Array3D8 { get; set; }
        public GlobalMap Map { get; }

        public GlobalMegaFile(GlobalMap map)
        {
            Map = map;
        }

        public bool Read(BinaryReader br)
        {
            if (!br.CheckHeaderString("MP00", reversed: true))
                return false;

            FileCount = br.ReadUInt32();

            for (var i = 0; i < FileCount; ++i)
            {
                var entry = new FileEntry(br);

                // Store the hashes
                if (!string.IsNullOrEmpty(entry.Crc2.GetString()))
                {
                    Hash.StringToHash($"global\\{entry.Crc2.GetString()}.dynpack");
                    Hash.StringToHash($"global\\{entry.Crc2.GetString()}.palettepack");
                }

                FileEntries.Add(entry.Crc, entry);

                var crc = entry.Crc;
                var crc2 = entry.Crc2;

                /*var block = StreamingManager.GetStreamBlockByCRC(crc, out string source);
                if (block != null)
                    Console.WriteLine($"Adding entry: 0x{entry.Crc:X8} => {block.FileName,-45} through {source}");

                block = StreamingManager.GetStreamBlockByCRC(crc2, out string source2);
                if (block != null)
                    Console.WriteLine($"Adding entry: 0x{crc:X8} => 0x{crc2:X8} => {block.FileName,-45} through {source2}");
                else
                    Console.WriteLine($"Adding unknown entry: 0x{crc:X8} => 0x{crc2:X8}");*/
            }

            Array3D8 = new uint[2 * FileCount];

            for (var i = 0; i < FileCount; ++i)
            {
                Array3D8[2 * i] = br.ReadUInt32();
                Array3D8[2 * i + 1] = br.ReadUInt32();

                var entryCrc = new Crc(Array3D8[2 * i]);
                var crc2 = new Crc(Array3D8[2 * i + 1]);

                if (!FileEntries.ContainsKey(entryCrc))
                {
                    Console.WriteLine($"ERROR: {entryCrc} => {crc2} is not a valid fileentry!");
                    continue;
                }

                var entry = FileEntries[entryCrc];
                if (entry.Crc == entryCrc && entry.Crc2 == crc2)
                    continue;

                /*string source;
                var block = StreamingManager.GetStreamBlockByCRC(entryCrc, out source);
                if (block != null)
                    Console.WriteLine($"Map entry: 0x{entryCrc:X8} => {block.FileName,-45} through {source}");

                block = StreamingManager.GetStreamBlockByCRC(crc2, out source);
                if (block != null)
                    Console.WriteLine($"Map entry: 0x{entryCrc:X8} 0x{crc2:X8} => {block.FileName,-45} through {source}");
                else
                    Console.WriteLine($"Map unknown entry: 0x{entryCrc:X8} => 0x{crc2:X8}");*/
            }

            return true;
        }

        public void ReadStreamBlocks()
        {

        }

        public void Export()
        {

        }
    }

    public enum FileEntryType
    {
        Mesh = 0,
        Texture = 1,
        Physics = 2,
        PathGraph = 3,
        AIFence = 4,
        Unk5 = 5,
        SoundBank = 6,
        FlashMovie = 7,
        WSD = 8
    }

    public enum FileReadMethod : uint
    {
        Mesh = 0xFE5E3A56u,
        Texture = 0xA40D777Du,
        Physics = 0x4445EA18u,
        PathGraph = 0xE1087B27u,
        AIFence = 0xD3098461u,
        SoundBank = 0xDD62BA1Au,
        FlashMovie = 0xB5D2FE96u,
        WSD = 0x9AB5A351u,
        Unk5 = 0x00000001u,
        Typ2 = 0x00000002u, // SBLA (not yet finished)
        Typ3 = 0x00000003u  // SBLA (finished)
    }

    public class FileEntry
    {
        public Crc Crc { get; set; }
        public Crc Crc2 { get; set; }
        public uint Size { get; set; }
        public ulong Offset { get; set; }

        public FileEntry(BinaryReader br)
        {
            Crc = new(br.ReadUInt32());
            Crc2 = new(br.ReadUInt32());
            Size = br.ReadUInt32();
            Offset = br.ReadUInt64();
        }
    }
}
