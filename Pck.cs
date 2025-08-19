using OggVorbisEncoder;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Encoding = System.Text.Encoding;

namespace HoyoversePckPlayer;

public class Wwise(string fullPath)
{
    public string? Path;
    public long Position;
    public long Size;
    public (long, long?) Id;
    public Pck.ExtensionKind Extension = Pck.ExtensionKind.Wem;
    public string? LanguageData;
    public string PckName = "";
    public List<string> Folders = [];
    public string? Name;
    public string? SongHash;
    //public Mutex? Mutex;
    public string FullPath = fullPath;
    public double Duration = -1;
    public bool IsWem;

    public string? GetPossibleName()
    {
        return null; // todo, use SongHash?
    }

    public byte[] GetRaw()
    {
        //Mutex!.WaitOne();
        var reader = new BinaryReader(File.OpenRead(FullPath));
        var currentOffset = reader.BaseStream.Position;
        reader.BaseStream.Seek(Position, SeekOrigin.Begin);
        var data = reader.ReadBytes((int)Size);
        reader.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
        reader.Close();
        reader.Dispose();
        //Mutex!.ReleaseMutex();
        return data;
    }

    public MemoryStream GetWav(VgmStreamWasm wasm)
    {
        wasm.FlushStdout();
        wasm.FlushStderr();
        var targetFilename = $"/{System.IO.Path.GetRandomFileName()}.{Extension.ToString().ToLower()}";
        var outputFilename = $"/{System.IO.Path.GetRandomFileName()}.{Extension.ToString().ToLower()}";
        wasm.Fs.InsertResource(targetFilename, GetRaw());
        /*{
            var f = File.Open($".{targetFilename}", FileMode.Create);
            f.Write(GetRaw());
            f.Flush();
            f.Dispose();
        }*/
        var lastIgnore = wasm.Fs.IgnoreInternalPipes; 
        wasm.Fs.IgnoreInternalPipes = true;
        //var t = new Stopwatch();
        //t.Start();
        //Console.WriteLine($"Out: {wasm.CallMain("-I", "-o", outputFilename, "-i", targetFilename)}");
        wasm.CallMain("-I", "-o", outputFilename, "-i", targetFilename);
        //t.Stop();
        //Console.WriteLine($"Took: {t.Elapsed.TotalMilliseconds}ms");
        wasm.FlushLogToConsole();
        wasm.Fs.IgnoreInternalPipes = lastIgnore;
        wasm.Fs.ClearHandles();
        wasm.Fs.RemoveResource(targetFilename);
        var final = new MemoryStream();
        final.Write(wasm.Fs.FetchResource(outputFilename));
        wasm.Fs.RemoveResource(outputFilename);
        return final;
    }

    const int WriteBufferSize = 4096;

    public MemoryStream GetOgg(VgmStreamWasm wasm)
    {
        using var data = new BinaryReader(GetWav(wasm));
        var format = PcmSample.EightBit;
        var sampleRate = 0;
        var channels = 0;
        var samples = new MemoryStream();

        for (var i = 0; i < data.BaseStream.Length - 4; i++)
        {
            data.BaseStream.Seek(i, SeekOrigin.Begin);
            switch (Encoding.ASCII.GetString(data.ReadBytes(4)))
            {
                case "fmt ":
                    data.BaseStream.Seek(4, SeekOrigin.Current);
                    if (data.ReadUInt16() == 1)
                        format = PcmSample.SixteenBit;
                    channels = data.ReadUInt16();
                    sampleRate = data.ReadInt32();
                    break;
                case "data":
                    samples.Write(data.ReadBytes(data.ReadInt32()));
                    break;
            }
            if (samples.Length > 0)
                break;
        }

        return ConvertRawPcmFile(sampleRate, channels, samples, format, sampleRate, channels);
    }

    static MemoryStream ConvertRawPcmFile(int outputSampleRate, int outputChannels, MemoryStream pcmSamples, PcmSample pcmSampleSize, int pcmSampleRate, int pcmChannels)
    {
        var numPcmSamples = (pcmSampleSize == 0 || pcmChannels == 0) ? 0 : ((int)pcmSamples.Length / (int)pcmSampleSize / pcmChannels);
        var pcmDuration = (pcmSampleSize == 0 || pcmChannels == 0) ? 0 : (numPcmSamples / (float)pcmSampleRate);
        var numOutputSamples = (int)(pcmDuration * outputSampleRate) / WriteBufferSize * WriteBufferSize;
        var outSamples = new float[outputChannels][];

        for (var ch = 0; ch < outputChannels; ch++)
            outSamples[ch] = new float[numOutputSamples];

        for (var sampleNumber = 0; sampleNumber < numOutputSamples; sampleNumber++)
        {
            for (var ch = 0; ch < outputChannels; ch++)
            {
                var sampleIndex = sampleNumber * pcmChannels * (int)pcmSampleSize;
                if (ch < pcmChannels)
                    sampleIndex += ch * (int)pcmSampleSize;
                pcmSamples.Position = sampleIndex;
                var b = pcmSamples.ReadByte();
                outSamples[ch][sampleNumber] = pcmSampleSize switch // Raw sample
                {
                    PcmSample.EightBit => b / 128f,
                    PcmSample.SixteenBit => ((short)(pcmSamples.ReadByte() << 8 | b)) / 32768f,
                    _ => throw new NotImplementedException(),
                };
            }
        }
        return GenerateFile(outSamples, outputSampleRate, outputChannels);
    }

    static MemoryStream GenerateFile(float[][] floatSamples, int sampleRate, int channels)
    {
        var outputData = new MemoryStream();

        // Stores all the static vorbis bitstream settings
        var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, 0.5f);

        // set up our packet->stream encoder
        var oggStream = new OggStream(new Random().Next());

        // =========================================================
        // HEADER
        // =========================================================
        // Vorbis streams begin with three headers; the initial header (with
        // most of the codec setup parameters) which is mandated by the Ogg
        // bitstream spec.  The second header holds any comment fields.  The
        // third header holds the bitstream codebook.

        var comments = new Comments();
        comments.AddTag("ARTIST", "GenshinFilePlayer");

        var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
        var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
        var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

        oggStream.PacketIn(infoPacket);
        oggStream.PacketIn(commentsPacket);
        oggStream.PacketIn(booksPacket);

        // Flush to force audio data onto its own page per the spec
        FlushPages(oggStream, outputData, true);

        // =========================================================
        // BODY (Audio Data)
        // =========================================================
        var processingState = ProcessingState.Create(info);
        for (var readIndex = 0; readIndex <= floatSamples[0].Length; readIndex += WriteBufferSize)
        {
            if (readIndex == floatSamples[0].Length)
                processingState.WriteEndOfStream();
            else
                processingState.WriteData(floatSamples, WriteBufferSize, readIndex);
            while (!oggStream.Finished && processingState.PacketOut(out var packet))
            {
                oggStream.PacketIn(packet);
                FlushPages(oggStream, outputData, false);
            }
        }
        FlushPages(oggStream, outputData, true);

        return outputData;
    }

    static void FlushPages(OggStream oggStream, Stream output, bool force)
    {
        while (oggStream.PageOut(out OggPage page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }

    enum PcmSample
    {
        EightBit = 1,
        SixteenBit = 2,
    }
}

public class Pck(string fullPath)
{
    public enum ExtensionKind
    {
        Xma,
        Ogg,
        Wav,
        Bnk,
        Ext,
        Wem,
    }

    public bool IsParsed;

    public uint BankVersion;
    public uint HeaderSize;
    public uint Flag;

    public uint LanguageSize;
    public uint BankSize;
    public uint SoundSize;
    public uint ExternalSize;

    public Dictionary<uint, string> LanguageList = [];
    // ReSharper disable once CollectionNeverQueried.Global
    public Dictionary<string, Wwise> FileSystem = [];
    
    public string Name = "";
    public string FullPath = fullPath;
    public string LocalPath = "";
    public bool IsLoaded;
    public long PckSize;

    //public Mutex Mutex = new();
    static readonly HashSet<string> HashCollisions = [];
    static readonly Mutex GlobalMutex = new();

    public static void ClearHashes()
    {
        HashCollisions.Clear();
    }

    public void Read(string? name = null)
    {
        if (name != null)
            Name = name;

        FileSystem.Clear();
        LanguageList.Clear();

        var reader = new BinaryReader(File.OpenRead(FullPath));

        reader.BaseStream.Seek(0, SeekOrigin.Begin);

        var headerIdentifier = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (headerIdentifier != "AKPK")
            throw new($"Expected .pck file header (AKPK) but found {headerIdentifier}");

        PckSize = reader.BaseStream.Length;
        HeaderSize = reader.ReadUInt32();
        Flag = reader.ReadUInt32();

        if (BinaryPrimitives.ReverseEndianness(Flag) < Flag)
            throw new("Expected little-endian file, but found big-endian!");
        LanguageSize = reader.ReadUInt32();
        BankSize = reader.ReadUInt32();
        SoundSize = reader.ReadUInt32();
        if (LanguageSize + BankSize + SoundSize + 0x10 < HeaderSize)
            ExternalSize = reader.ReadUInt32();
        ParseLanguages(reader);

        // Extract banks
        ParseTable(reader, ExtensionKind.Bnk, BankSize, false, false);

        if (BankVersion == 0)
        {
            if (ExternalSize == 0)
                Console.WriteLine("Can't detect bank version, assuming 62.");
            BankVersion = 62;
        }

        // Extract sounds
        ParseTable(reader, ExtensionKind.Wem, SoundSize, true, false);

        // Extract externals
        ParseTable(reader, ExtensionKind.Wem, ExternalSize, true, true);

        // Last sound may be padding
        reader.Close();
        reader.Dispose();
        
        IsParsed = true;
    }

    void ParseLanguages(BinaryReader reader)
    {
        var startOffset = (uint)reader.BaseStream.Position;
        var languageAmount = reader.ReadUInt32();
        for (var i = 0; i < languageAmount; i++)
        {
            var languageOffset = reader.ReadUInt32() + startOffset;
            var languageId = reader.ReadUInt32();
            var currentOffset = (uint)reader.BaseStream.Position;
            reader.BaseStream.Seek(languageOffset, SeekOrigin.Begin);
            string languageName;
            var testUnicode = reader.ReadUInt16();
            reader.BaseStream.Seek(-2, SeekOrigin.Current);
            if (((testUnicode & 0xff00) >> 8) == 0 || (testUnicode & 0x00ff) == 0) // Read Unicode
            {
                var charList = new List<char>();
                while (true)
                {
                    var unicodeByte = reader.ReadUInt16();
                    if (unicodeByte == 0)
                        break;
                    charList.Add((char)unicodeByte);
                }
                languageName = new([.. charList]);
            }
            else // Read UTF-8
            {
                var charList = new List<byte>();
                while (true)
                {
                    var c = reader.ReadByte();
                    if (c == 0)
                        break;
                    charList.Add(c);
                }
                languageName = Encoding.UTF8.GetString([.. charList]);
            }
            LanguageList[languageId] = languageName;
            reader.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
        }
        reader.BaseStream.Seek(startOffset + LanguageSize, SeekOrigin.Begin);
    }

    void DetectBankVersion(BinaryReader reader, long offset)
    {
        var currentOffset = reader.BaseStream.Position;
        // Skip BKHD<chunkSize:uint>
        reader.BaseStream.Seek(offset + 8, SeekOrigin.Begin);
        BankVersion = reader.ReadUInt32();
        if (BankVersion > 0x1000)
        {
            Console.WriteLine("Wrong bank version, assuming 62.");
            BankVersion = 62;
        }
        reader.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
    }

    void ParseTable(BinaryReader reader, ExtensionKind extension, uint sectionSize, bool isSounds, bool isExternals)
    {
        if (sectionSize != 0)
        {
            var files = reader.ReadUInt32();
            if (files != 0)
                ParseTableInternal(reader, extension, files, sectionSize, isSounds, isExternals);
        }
    }

    void ParseTableInternal(BinaryReader reader, ExtensionKind extension, uint files, uint sectionSize, bool isSounds, bool isExternals)
    {
        var entrySize = (sectionSize - 0x04) / files;
        var altMode = entrySize == 0x18;
        for (var i = 0; i < files; i++)
        {
            var id = altMode && isExternals ? (reader.ReadUInt32(), reader.ReadUInt32()) : (reader.ReadUInt32(), (uint?)null);
            var blockSize = reader.ReadUInt32();
            var size = altMode && !isExternals ? reader.ReadUInt64() : reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var languageId = reader.ReadUInt32();
            if (blockSize != 0)
                offset *= blockSize;
            switch (isSounds)
            {
                case false when BankVersion == 0:
                    DetectBankVersion(reader, offset);
                    break;
                case true when BankVersion < 62:
                    {
                        var currentOffset = reader.BaseStream.Position;
                        // Maybe it should find the "fmt " chunk first
                        reader.BaseStream.Seek(offset + 0x14, SeekOrigin.Begin); // Codec offset
                        extension = reader.ReadUInt16() switch // Codec
                        {
                            0x0401 or 0x0166 => ExtensionKind.Xma,
                            0xffff => ExtensionKind.Ogg,
                            _ => ExtensionKind.Wav,
                        };
                        reader.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
                        break;
                    }
            }
            //var path = Name.Length > 0 ? $"{Name}/{LanguageList[languageId]}" : LanguageList[languageId];
            var path = altMode && isExternals ? $"externals/{LanguageList[languageId]}/{id.Item1}+{id.Item2}.{extension.ToString().ToLower()}" : $"{LanguageList[languageId]}/{id.Item1}.{extension.ToString().ToLower()}";
            {
                var folders = new List<string>();

                if (altMode && isExternals)
                    folders.Add("externals");
                //folders.Add(Name);
                folders.Add(LanguageList[languageId]);
                var currentOffset = reader.BaseStream.Position;
                reader.BaseStream.Position = offset;
                var songHash = Convert.ToHexStringLower(SHA256.HashData((reader.ReadBytes((int)size))));
                reader.BaseStream.Position = currentOffset;
                var header = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var isWem = header is "RIFF" or "RIFX";
                reader.BaseStream.Position = currentOffset;
                GlobalMutex.WaitOne();
                try
                {
                    if (HashCollisions.Contains(songHash))
                    {
                        //Console.WriteLine($"{songHash} was already in a collision, changing...");
                        var indexer = 0;
                        while (HashCollisions.Contains($"{songHash}+{indexer}"))
                            indexer++;
                        songHash += $"+{indexer}";
                    }
                    HashCollisions.Add(songHash);
                }
                finally
                {
                    GlobalMutex.ReleaseMutex();
                }

                var data = new Wwise(FullPath)
                {
                    Id = id,
                    Path = path,
                    Extension = extension,
                    Position = offset,
                    Size = (long)size,
                    LanguageData = LanguageList[languageId],
                    PckName = Name,
                    Folders = folders,
                    Name = altMode && isExternals ? $"{id.Item1}+{id.Item2}.{extension.ToString().ToLower()}" : $"{id.Item1}.{extension.ToString().ToLower()}",
                    SongHash = songHash,
                    IsWem = isWem,
                    //Mutex = Mutex,
                };
                //reader.BaseStream.Position = offset;
                //data.ReadWwiseHeader(reader);
                //reader.BaseStream.Position = currentOffset;
                FileSystem.Add(path, data);
            }
        }
    }
}