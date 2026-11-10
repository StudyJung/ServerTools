using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

internal static class HDC
{
    private const string ArchiveMagic = "HDC-ARCHIVE-4";
    private const string LegacyEncryptedPayloadMagic = "HDC-HUFFMAN-2";
    private const string LegacyArchiveMagic = "HDC-HUFFMAN-1";
    private const string LegacyFullEncryptionMagic = "HDC-HUFFMAN-3";
    private const int BufferSize = 128 * 1024;
    private const int MaximumEntries = 1_000_000;
    private static readonly byte[] BinaryArchiveMagic = Encoding.ASCII.GetBytes("HDA6");
    private static readonly byte[] HuffmanContainerMagic = Encoding.ASCII.GetBytes("HDC6");
    private static readonly byte[] BrotliContainerMagic = Encoding.ASCII.GetBytes("HDB7");
    private static readonly byte[] LegacyHuffmanContainerMagic = Encoding.ASCII.GetBytes("HDC5");
    private static readonly FileAttributes StoredAttributes =
        FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private static int Main(string[] args)
    {
        bool interactive = args.Length == 0;

        try
        {
            if (interactive)
            {
                Console.WriteLine("HDC");
                Console.Write("?: ");
                ProcessInput(Console.ReadLine() ?? string.Empty);
            }
            else if (args.Length >= 2 && args[0] == "--p")
            {
                HDCP(args[1]);
            }
            else if (args.Length >= 2 && args[0] == "--e")
            {
                HDCE(args[1]);
            }
            else if (args.Length == 1)
            {
                ProcessInput(args[0]);
            }
            else
            {
                throw new ArgumentException("HDC.exe");
            }

            if (interactive)
            {
                Console.WriteLine("계속하려면 아무 키나 누르세요.");
                Console.ReadKey(true);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException or InvalidOperationException or SecurityException or
                                   OverflowException or FormatException or NotSupportedException)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");

            if (interactive)
            {
                Console.WriteLine("계속하려면 아무 키나 누르세요.");
                Console.ReadKey(true);
            }

            return 1;
        }
    }

    private static void ProcessInput(string input)
    {
        string path = input.Trim().Trim('"');

        if (path.Length == 0)
        {
            throw new ArgumentException("파일 주소가 비어 있습니다.");
        }

        if (path.EndsWith(".hdc", StringComparison.OrdinalIgnoreCase))
        {
            HDCE(path);
        }
        else
        {
            HDCP(path);
        }
    }

    private static void HDCP(string requestedFolderPath)
    {
        if (string.IsNullOrWhiteSpace(requestedFolderPath))
        {
            throw new ArgumentException("폴더 주소가 비어 있습니다.");
        }

        string folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedFolderPath.Trim().Trim('"')));
        string folderName = Path.GetFileName(folderPath);
        string baseDirectory = Directory.GetParent(folderPath)?.FullName ?? string.Empty;

        if (folderName.Length == 0 || baseDirectory.Length == 0 || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {folderPath}");
        }

        string outputPath = Path.Combine(baseDirectory, folderName + ".hdc");

        if (File.Exists(outputPath))
        {
            Console.Write($"이미 존재합니다. 덮어쓰려면 Y 입력 [{outputPath}]: ");

            if (!string.Equals(Console.ReadLine(), "Y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("취소했습니다.");
                return;
            }
        }

        HDCPFolder(folderPath, outputPath);
        Console.WriteLine($"완료: {outputPath}");
    }

    private static void HDCPFolder(string sourceRoot, string outputPath)
    {
        List<Entry> entries = CollectEntries(sourceRoot);
        string temporaryPath = outputPath + ".hdc-tmp";

        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new IOException($"파일이 존재합니다: {temporaryPath}");
        }

        try
        {
            using var archive = new MemoryStream();
            using (var writer = new BinaryWriter(archive, Encoding.UTF8, true))
            {
                writer.Write(BinaryArchiveMagic);
                writer.Write(Path.GetFileName(sourceRoot));
                writer.Write(entries.Count);

                foreach (Entry entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        writer.Write((byte)'D');
                        writer.Write((int)entry.Attributes);
                        writer.Write(entry.LastWriteUtcTicks);
                        writer.Write(entry.RelativePath);
                        continue;
                    }

                    (byte[] data, uint crc32) = ReadFileForArchive(entry);

                    if (archive.Length > int.MaxValue - data.LongLength)
                    {
                        throw new InvalidDataException("파일이 너무 큽니다.");
                    }

                    writer.Write((byte)'F');
                    writer.Write((int)entry.Attributes);
                    writer.Write(entry.LastWriteUtcTicks);
                    writer.Write(entry.RelativePath);
                    writer.Write((long)data.Length);
                    writer.Write(crc32);
                    writer.Write(data);
                }

                writer.Write((byte)0xFF);
            }

            if (archive.Length > int.MaxValue)
            {
                throw new InvalidDataException("데이터가 너무 큽니다.");
            }

            (byte[] transformedArchive, int plainPrefixLength, int encryptedPrefixLength) = EncryptArchivePrefix(archive.ToArray());
            byte[] huffmanContainer = CompressEncryptedArchive(transformedArchive, plainPrefixLength, encryptedPrefixLength);
            byte[] brotliContainer = CompressBrotliLayer(huffmanContainer);
            byte[] finalContainer = brotliContainer.Length < huffmanContainer.Length ? brotliContainer : huffmanContainer;
            string textContainer = EncodeAscii85(finalContainer);

            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteAscii(output, textContainer);
                output.Flush(true);
            }

            File.Move(temporaryPath, outputPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static List<Entry> CollectEntries(string root)
    {
        var entries = new List<Entry>();
        AddDirectory(root, string.Empty, entries);

        if (entries.Count > MaximumEntries)
        {
            throw new InvalidDataException($"항목 수가 제한({MaximumEntries:N0})을 초과했습니다.");
        }

        return entries;
    }

    private static (byte[] Data, uint Crc32) ReadFileForArchive(Entry entry)
    {
        if (entry.Length > int.MaxValue)
        {
            throw new InvalidDataException("파일이 너무 큽니다.");
        }

        byte[] data = File.ReadAllBytes(entry.FullPath);

        if (data.LongLength != entry.Length)
        {
            throw new IOException($"파일 크기가 변경되었습니다: {entry.FullPath}");
        }

        var crc = new Crc32();
        crc.Update(data, 0, data.Length);
        return (data, crc.Value);
    }

    private static void AddDirectory(string fullPath, string relativePath, List<Entry> entries)
    {
        FileAttributes attributes = File.GetAttributes(fullPath);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"지원하지 않습니다: {fullPath}");
        }

        entries.Add(new Entry(
            relativePath,
            true,
            attributes & StoredAttributes,
            Directory.GetLastWriteTimeUtc(fullPath).Ticks,
            0,
            fullPath));

        foreach (string childPath in Directory.GetFileSystemEntries(fullPath)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            string childName = Path.GetFileName(childPath);
            string childRelativePath = relativePath.Length == 0 ? childName : relativePath + "/" + childName;
            FileAttributes childAttributes = File.GetAttributes(childPath);

            if ((childAttributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                if (childName.Length == 0 || !char.IsUpper(childName[0]))
                {
                    continue;
                }

                AddDirectory(childPath, childRelativePath, entries);
            }
            else
            {
                var info = new FileInfo(childPath);
                entries.Add(new Entry(
                    childRelativePath,
                    false,
                    childAttributes & StoredAttributes,
                    info.LastWriteTimeUtc.Ticks,
                    info.Length,
                    childPath));
            }

            if (entries.Count > MaximumEntries)
            {
                throw new InvalidDataException($"항목 수가 제한({MaximumEntries:N0})을 초과했습니다.");
            }
        }
    }

    private static FileAnalysis AnalyzeFile(Entry entry)
    {
        var frequencies = new long[256];
        var crc = new Crc32();
        long rawLength = 0;
        byte[] buffer = new byte[BufferSize];

        using var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            rawLength = checked(rawLength + read);
            crc.Update(buffer, 0, read);

            for (int i = 0; i < read; i++)
            {
                frequencies[buffer[i]]++;
            }
        }

        if (rawLength != entry.Length)
        {
            throw new IOException($"파일 크기가 변경되었습니다: {entry.FullPath}");
        }

        byte[] codeLengths = BuildCodeLengths(frequencies);
        bool[][] codes = BuildCodes(codeLengths);
        long bitLength = 0;

        for (int i = 0; i < frequencies.Length; i++)
        {
            bitLength = checked(bitLength + checked(frequencies[i] * codeLengths[i]));
        }

        long compressedLength = checked((bitLength + 7) / 8);

        return new FileAnalysis(rawLength, compressedLength, crc.Value, codeLengths, codes, frequencies);
    }

    private static string CompressFile(Entry entry, FileAnalysis analysis)
    {
        var crc = new Crc32();
        long rawLength = 0;
        long expectedHuffmanLength = Base64Length(analysis.CompressedLength);

        if (expectedHuffmanLength > int.MaxValue)
        {
            throw new InvalidDataException("데이터가 너무 큽니다.");
        }

        using var encodedData = new MemoryStream();
        var base64 = new Base64WriteStream(encodedData);
        var bitWriter = new HuffmanBitWriter(base64, analysis.Codes);
        byte[] buffer = new byte[BufferSize];

        using var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            rawLength = checked(rawLength + read);
            crc.Update(buffer, 0, read);

            for (int i = 0; i < read; i++)
            {
                bitWriter.WriteSymbol(buffer[i]);
            }
        }

        long compressedLength = bitWriter.Finish();
        base64.Finish();

        if (rawLength != analysis.RawLength || compressedLength != analysis.CompressedLength || crc.Value != analysis.Crc32)
        {
            throw new IOException($"파일 내용이 변경되었습니다: {entry.FullPath}");
        }

        string huffmanData = Encoding.ASCII.GetString(encodedData.ToArray());
        return huffmanData;
    }

    private static byte[] BuildCodeLengths(long[] frequencies)
    {
        var queue = new PriorityQueue<HuffmanNode, (long Frequency, int Order)>();
        int order = 0;

        for (int symbol = 0; symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                var node = new HuffmanNode(symbol, frequencies[symbol], order++);
                queue.Enqueue(node, (node.Frequency, node.Order));
            }
        }

        var lengths = new byte[256];

        if (queue.Count == 0)
        {
            return lengths;
        }

        if (queue.Count == 1)
        {
            lengths[queue.Dequeue().Symbol] = 1;
            return lengths;
        }

        while (queue.Count > 1)
        {
            HuffmanNode left = queue.Dequeue();
            HuffmanNode right = queue.Dequeue();
            long frequency = checked(left.Frequency + right.Frequency);
            var parent = new HuffmanNode(-1, frequency, order++, left, right);
            queue.Enqueue(parent, (parent.Frequency, parent.Order));
        }

        FillLengths(queue.Dequeue(), 0, lengths);
        return lengths;
    }

    private static void FillLengths(HuffmanNode node, int depth, byte[] lengths)
    {
        if (node.Symbol >= 0)
        {
            if (depth > byte.MaxValue)
            {
                throw new InvalidDataException("범위를 초과했습니다.");
            }

            lengths[node.Symbol] = (byte)Math.Max(depth, 1);
            return;
        }

        FillLengths(node.Left!, depth + 1, lengths);
        FillLengths(node.Right!, depth + 1, lengths);
    }

    private static bool[][] BuildCodes(byte[] lengths)
    {
        var codes = new bool[256][];
        var symbols = Enumerable.Range(0, 256)
            .Where(symbol => lengths[symbol] > 0)
            .OrderBy(symbol => lengths[symbol])
            .ThenBy(static symbol => symbol)
            .ToArray();
        BigInteger code = BigInteger.Zero;
        int previousLength = 0;

        foreach (int symbol in symbols)
        {
            int length = lengths[symbol];
            code <<= length - previousLength;

            if (code >= (BigInteger.One << length))
            {
                throw new InvalidDataException("표가 올바르지 않습니다.");
            }

            var bits = new bool[length];

            for (int bit = 0; bit < length; bit++)
            {
                bits[bit] = ((code >> (length - bit - 1)) & BigInteger.One) != BigInteger.Zero;
            }

            codes[symbol] = bits;
            code++;
            previousLength = length;
        }

        return codes;
    }

    private static string EncodeAscii85(byte[] data)
    {
        var output = new StringBuilder(data.Length + data.Length / 4 + 16);
        int lineLength = 0;

        void Append(char value)
        {
            if (lineLength == 80)
            {
                output.Append("\r\n");
                lineLength = 0;
            }

            output.Append(value);
            lineLength++;
        }

        Span<char> encoded = stackalloc char[5];

        for (int offset = 0; offset < data.Length; offset += 4)
        {
            int count = Math.Min(4, data.Length - offset);
            uint value = 0;

            for (int index = 0; index < 4; index++)
            {
                value = (value << 8) | (index < count ? data[offset + index] : 0u);
            }

            for (int index = encoded.Length - 1; index >= 0; index--)
            {
                encoded[index] = (char)('!' + value % 85);
                value /= 85;
            }

            int encodedCount = count == 4 ? 5 : count + 1;

            for (int index = 0; index < encodedCount; index++)
            {
                Append(encoded[index]);
            }
        }

        return output.ToString();
    }

    private static bool TryDecodeAscii85Container(string encoded, out byte[]? container)
    {
        container = null;
        byte[] decoded;

        try
        {
            var symbols = new List<byte>(encoded.Length);

            foreach (char character in encoded)
            {
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                if (character is < '!' or > 'u')
                {
                    return false;
                }

                symbols.Add((byte)character);
            }

            if (symbols.Count % 5 == 1)
            {
                return false;
            }

            using var output = new MemoryStream();
            Span<byte> bytes = stackalloc byte[4];

            for (int offset = 0; offset < symbols.Count; offset += 5)
            {
                int count = Math.Min(5, symbols.Count - offset);
                ulong value = 0;

                for (int index = 0; index < 5; index++)
                {
                    uint digit = index < count ? (uint)(symbols[offset + index] - '!') : 84u;
                    value = value * 85 + digit;
                }

                if (value > uint.MaxValue)
                {
                    return false;
                }

                uint block = (uint)value;
                bytes[0] = (byte)(block >> 24);
                bytes[1] = (byte)(block >> 16);
                bytes[2] = (byte)(block >> 8);
                bytes[3] = (byte)block;
                int byteCount = count == 5 ? 4 : count - 1;
                output.Write(bytes[..byteCount]);
            }

            decoded = output.ToArray();
        }
        catch (OverflowException)
        {
            return false;
        }

        bool isHuffmanContainer = decoded.Length >= HuffmanContainerMagic.Length &&
                                  decoded.AsSpan(0, HuffmanContainerMagic.Length).SequenceEqual(HuffmanContainerMagic);
        bool isBrotliContainer = decoded.Length >= BrotliContainerMagic.Length &&
                                 decoded.AsSpan(0, BrotliContainerMagic.Length).SequenceEqual(BrotliContainerMagic);

        if (!isHuffmanContainer && !isBrotliContainer)
        {
            return false;
        }

        container = decoded;
        return true;
    }

    private static bool StreamStartsWith(Stream input, byte[] prefix)
    {
        long originalPosition = input.Position;
        input.Position = 0;
        byte[] actual = new byte[prefix.Length];
        int read = 0;

        while (read < actual.Length)
        {
            int count = input.Read(actual, read, actual.Length - read);

            if (count == 0)
            {
                break;
            }

            read += count;
        }

        input.Position = originalPosition;
        return read == prefix.Length && actual.AsSpan().SequenceEqual(prefix);
    }

    private static void HDCEBinaryArchive(Stream archiveFile, string archiveDirectory)
    {
        byte[] archiveBytes;

        if (StreamStartsWith(archiveFile, BrotliContainerMagic))
        {
            using var huffmanContainer = new MemoryStream(DecompressBrotliLayer(archiveFile), false);
            archiveBytes = DecompressBinaryContainer(huffmanContainer);
        }
        else
        {
            archiveBytes = DecompressBinaryContainer(archiveFile);
        }

        using var input = new MemoryStream(archiveBytes, false);
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        byte[] magic = reader.ReadBytes(BinaryArchiveMagic.Length);

        if (!magic.AsSpan().SequenceEqual(BinaryArchiveMagic))
        {
            throw new InvalidDataException("파일 형식이 올바르지 않습니다.");
        }

        string rootName = reader.ReadString();

        if (string.IsNullOrWhiteSpace(rootName) || rootName is "." or ".." ||
            rootName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || rootName.Contains('/') || rootName.Contains('\\'))
        {
            throw new InvalidDataException("폴더명이 올바르지 않습니다.");
        }

        int entryCount = reader.ReadInt32();

        if (entryCount < 1 || entryCount > MaximumEntries)
        {
            throw new InvalidDataException("항목 수가 올바르지 않습니다.");
        }

        string destination = FindDestination(archiveDirectory, rootName);

        if (File.Exists(destination))
        {
            throw new IOException($"같은 이름의 파일이 있습니다: {destination}");
        }

        string temporaryDirectory = FindTemporaryDirectory(destination);
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            HDCEBinaryEntries(reader, temporaryDirectory, entryCount);

            if (reader.ReadByte() != 0xFF || input.Position != input.Length)
            {
                throw new InvalidDataException("데이터 끝 표시가 올바르지 않습니다.");
            }

            InstallHDCEedTree(temporaryDirectory, destination);

            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            Console.WriteLine($"완료: {destination}");
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            throw;
        }
    }

    private static byte[] DecompressBinaryContainer(Stream input)
    {
        input.Position = 0;
        using var reader = new BinaryReader(input, Encoding.ASCII, true);
        byte[] magic = reader.ReadBytes(HuffmanContainerMagic.Length);

        if (!magic.AsSpan().SequenceEqual(HuffmanContainerMagic))
        {
            throw new InvalidDataException("파일 형식이 올바르지 않습니다.");
        }

        int plainPrefixLength = reader.ReadInt32();
        int encryptedPrefixLength = reader.ReadInt32();
        long rawLength = reader.ReadInt64();
        long compressedLength = reader.ReadInt64();
        uint expectedCrc = reader.ReadUInt32();
        byte[] codeLengths = reader.ReadBytes(256);

        if (plainPrefixLength < 0 || plainPrefixLength > 100 ||
            encryptedPrefixLength != AesEncryptedByteLength(plainPrefixLength) ||
            rawLength < 0 || rawLength > int.MaxValue || encryptedPrefixLength > rawLength ||
            compressedLength < 0 || compressedLength > int.MaxValue || codeLengths.Length != 256 ||
            input.Length - input.Position != compressedLength)
        {
            throw new InvalidDataException("HDC 데이터가 올바르지 않습니다.");
        }

        byte[] compressedBytes = reader.ReadBytes((int)compressedLength);

        if (compressedBytes.LongLength != compressedLength)
        {
            throw new InvalidDataException("데이터가 중간에 끝났습니다.");
        }

        using var compressed = new MemoryStream(compressedBytes, false);
        using var transformed = new MemoryStream();
        uint actualCrc = DecompressFile(compressed, codeLengths, compressedLength, rawLength, transformed);

        if (actualCrc != expectedCrc)
        {
            throw new InvalidDataException("파일 무결성 검증에 실패했습니다.");
        }

        byte[] transformedBytes = transformed.ToArray();

        if (transformedBytes.LongLength != rawLength)
        {
            throw new InvalidDataException("데이터 크기가 올바르지 않습니다.");
        }

        byte[] encryptedPrefix = transformedBytes.AsSpan(0, encryptedPrefixLength).ToArray();
        byte[] plainPrefix = Decrypt(encryptedPrefix)
            ?? throw new InvalidDataException("문자열을 읽지 못했습니다.");

        if (plainPrefix.Length != plainPrefixLength)
        {
            throw new InvalidDataException("문자열을 읽지 못했습니다.");
        }

        byte[] archive = new byte[plainPrefix.Length + transformedBytes.Length - encryptedPrefixLength];
        Buffer.BlockCopy(plainPrefix, 0, archive, 0, plainPrefix.Length);
        Buffer.BlockCopy(transformedBytes, encryptedPrefixLength, archive, plainPrefix.Length, transformedBytes.Length - encryptedPrefixLength);
        return archive;
    }

    private static byte[] DecompressBrotliLayer(Stream input)
    {
        input.Position = 0;
        using var reader = new BinaryReader(input, Encoding.ASCII, true);
        byte[] magic = reader.ReadBytes(BrotliContainerMagic.Length);

        if (!magic.AsSpan().SequenceEqual(BrotliContainerMagic))
        {
            throw new InvalidDataException("파일 형식이 올바르지 않습니다.");
        }

        long rawLength = reader.ReadInt64();
        long compressedLength = reader.ReadInt64();
        uint expectedCrc = reader.ReadUInt32();

        if (rawLength < 0 || rawLength > int.MaxValue || compressedLength < 0 || compressedLength > int.MaxValue ||
            input.Length - input.Position != compressedLength)
        {
            throw new InvalidDataException("HDC 데이터가 올바르지 않습니다.");
        }

        byte[] compressedBytes = reader.ReadBytes((int)compressedLength);

        if (compressedBytes.LongLength != compressedLength)
        {
            throw new InvalidDataException("데이터가 중간에 끝났습니다.");
        }

        using var compressed = new MemoryStream(compressedBytes, false);
        using var decompressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionMode.Decompress, true))
        {
            byte[] buffer = new byte[BufferSize];
            int read;

            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decompressed.Length + read > rawLength)
                {
                    throw new InvalidDataException("데이터 크기가 올바르지 않습니다.");
                }

                decompressed.Write(buffer, 0, read);
            }
        }

        if (decompressed.Length != rawLength)
        {
            throw new InvalidDataException("데이터 크기가 올바르지 않습니다.");
        }

        byte[] huffmanContainer = decompressed.ToArray();
        var crc = new Crc32();
        crc.Update(huffmanContainer, 0, huffmanContainer.Length);

        if (crc.Value != expectedCrc || huffmanContainer.Length < HuffmanContainerMagic.Length ||
            !huffmanContainer.AsSpan(0, HuffmanContainerMagic.Length).SequenceEqual(HuffmanContainerMagic))
        {
            throw new InvalidDataException("파일 무결성 검증에 실패했습니다.");
        }

        return huffmanContainer;
    }

    private static void HDCEBinaryEntries(BinaryReader reader, string temporaryDirectory, int entryCount)
    {
        var directories = new List<(string RelativePath, long Ticks)>();
        string rootFullPath = Path.GetFullPath(temporaryDirectory);

        for (int index = 0; index < entryCount; index++)
        {
            byte type = reader.ReadByte();
            FileAttributes attributes = (FileAttributes)reader.ReadInt32() & StoredAttributes;
            long ticks = reader.ReadInt64();
            string relativePath = reader.ReadString();

            if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException("파일 시간 값이 범위를 벗어났습니다.");
            }

            string fullPath = GetSafePath(rootFullPath, relativePath);

            if (type == (byte)'D')
            {
                Directory.CreateDirectory(fullPath);
                directories.Add((relativePath, ticks));
                continue;
            }

            if (type != (byte)'F' || relativePath.Length == 0)
            {
                throw new InvalidDataException("파일 항목이 올바르지 않습니다.");
            }

            long fileLength = reader.ReadInt64();
            uint expectedCrc = reader.ReadUInt32();

            if (fileLength < 0 || fileLength > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("파일 크기가 올바르지 않습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var crc = new Crc32();
            byte[] buffer = new byte[BufferSize];
            long remaining = fileLength;

            using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                while (remaining > 0)
                {
                    int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                    if (read == 0)
                    {
                        throw new InvalidDataException("파일 데이터가 중간에 끝났습니다.");
                    }

                    crc.Update(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }

                output.Flush(true);
            }

            if (crc.Value != expectedCrc)
            {
                throw new InvalidDataException($"파일 무결성 검증에 실패했습니다: {relativePath}");
            }

            ApplyFileMetadata(fullPath, attributes, ticks);
        }

        foreach (var directory in directories.OrderByDescending(static item => item.RelativePath.Length))
        {
            string fullPath = GetSafePath(rootFullPath, directory.RelativePath);
            Directory.SetLastWriteTimeUtc(fullPath, new DateTime(directory.Ticks, DateTimeKind.Utc));
        }
    }

    private static void HDCE(string requestedArchivePath)
    {
        if (string.IsNullOrWhiteSpace(requestedArchivePath))
        {
            throw new ArgumentException("주소가 비어 있습니다.");
        }

        string archiveInput = requestedArchivePath.Trim().Trim('"');

        if (!archiveInput.EndsWith(".hdc", StringComparison.OrdinalIgnoreCase))
        {
            archiveInput += ".hdc";
        }

        string archivePath = Path.GetFullPath(archiveInput);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("파일을 찾을 수 없습니다.", archivePath);
        }

        string archiveDirectory = Path.GetDirectoryName(archivePath)!;
        using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (StreamStartsWith(archiveFile, HuffmanContainerMagic) || StreamStartsWith(archiveFile, BrotliContainerMagic))
        {
            HDCEBinaryArchive(archiveFile, archiveDirectory);
            return;
        }

        string storedArchive = ReadAsciiText(archiveFile, archiveFile.Length);

        if (TryDecodeAscii85Container(storedArchive, out byte[]? decodedContainer))
        {
            using var decodedStream = new MemoryStream(decodedContainer!, false);
            HDCEBinaryArchive(decodedStream, archiveDirectory);
            return;
        }

        string archiveText;

        if (!TryDecompressEncryptedArchive(storedArchive, out string? decodedArchive))
        {
            archiveText = Decrypt(storedArchive) ?? storedArchive;
        }
        else
        {
            archiveText = decodedArchive!;
        }

        if (archiveText.Any(static character => character > 0x7F))
        {
            throw new InvalidDataException("문자열을 읽지 못했습니다.");
        }

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(archiveText), false);
        (string rootName, int entryCount, bool encryptedPayload, bool rawPayload) = ReadArchiveHeader(input);
        string destination = FindDestination(archiveDirectory, rootName);

        if (File.Exists(destination))
        {
            throw new IOException($"같은 이름의 파일이 있습니다: {destination}");
        }

        string temporaryDirectory = FindTemporaryDirectory(destination);
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            HDCEEntries(input, temporaryDirectory, entryCount, encryptedPayload, rawPayload);

            if (ReadAsciiLine(input) != "END")
            {
                throw new InvalidDataException("데이터 끝 표시가 올바르지 않습니다.");
            }

            int trailing;

            while ((trailing = input.ReadByte()) >= 0)
            {
                if (trailing is not ('\r' or '\n' or ' ' or '\t'))
                {
                    throw new InvalidDataException("해석되지 않은 데이터가 있습니다.");
                }
            }

            InstallHDCEedTree(temporaryDirectory, destination);
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            Console.WriteLine($"완료: {destination}");
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            throw;
        }
    }

    private static (string RootName, int EntryCount, bool EncryptedPayload, bool RawPayload) ReadArchiveHeader(Stream payload)
    {
        string magic = ReadAsciiLine(payload, true);
        bool encryptedPayload = magic == LegacyEncryptedPayloadMagic;
        bool rawPayload = magic == ArchiveMagic;

        if (!encryptedPayload && !rawPayload && magic != LegacyArchiveMagic && magic != LegacyFullEncryptionMagic)
        {
            throw new InvalidDataException("HDC 형식이 아닙니다.");
        }

        string[] rootFields = ReadAsciiLine(payload).Split('|');

        if (rootFields.Length != 2 || rootFields[0] != "ROOT")
        {
            throw new InvalidDataException("폴더 정보가 올바르지 않습니다.");
        }

        string rootName = DecodeText(rootFields[1]);

        if (string.IsNullOrWhiteSpace(rootName) || rootName is "." or ".." ||
            rootName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || rootName.Contains('/') || rootName.Contains('\\'))
        {
            throw new InvalidDataException("폴더명이 올바르지 않습니다.");
        }

        string[] countFields = ReadAsciiLine(payload).Split('|');

        if (countFields.Length != 2 || countFields[0] != "COUNT" ||
            !int.TryParse(countFields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int entryCount) ||
            entryCount < 1 || entryCount > MaximumEntries)
        {
            throw new InvalidDataException("항목 수가 올바르지 않습니다.");
        }

        return (rootName, entryCount, encryptedPayload, rawPayload);
    }

    private static void HDCEEntries(Stream payload, string temporaryDirectory, int entryCount, bool encryptedPayload, bool rawPayload)
    {
        var directories = new List<(string RelativePath, FileAttributes Attributes, long Ticks)>();
        string rootFullPath = Path.GetFullPath(temporaryDirectory);

        for (int index = 0; index < entryCount; index++)
        {
            string[] fields = ReadAsciiLine(payload).Split('|');

            if (fields.Length == 4 && fields[0] == "D")
            {
                FileAttributes attributes = ParseAttributes(fields[1]);
                long ticks = ParseTicks(fields[2]);
                string relativePath = DecodeText(fields[3]);
                string fullPath = GetSafePath(rootFullPath, relativePath);
                Directory.CreateDirectory(fullPath);
                directories.Add((relativePath, attributes, ticks));
                continue;
            }

            if (rawPayload)
            {
                if (fields.Length != 7 || fields[0] != "F")
                {
                    throw new InvalidDataException("파일 항목이 올바르지 않습니다.");
                }

                FileAttributes rawFileAttributes = ParseAttributes(fields[1]);
                long rawFileTicks = ParseTicks(fields[2]);
                long rawFileLength = ParseNonNegativeLong(fields[3], "파일 크기");

                if (!uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint expectedRawCrc))
                {
                    throw new InvalidDataException("파일 CRC 값이 올바르지 않습니다.");
                }

                long rawEncodedLength = ParseNonNegativeLong(fields[5], "Base64 데이터 크기");

                if (rawEncodedLength != Base64Length(rawFileLength))
                {
                    throw new InvalidDataException("기록된 크기와 일치하지 않습니다.");
                }

                string rawRelativeFilePath = DecodeText(fields[6]);
                string rawFullFilePath = GetSafePath(rootFullPath, rawRelativeFilePath);

                if (rawRelativeFilePath.Length == 0)
                {
                    throw new InvalidDataException("파일 경로가 비어 있습니다.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(rawFullFilePath)!);
                var rawBase64 = new Base64ReadStream(payload, rawEncodedLength);
                uint actualRawCrc;

                using (var output = new FileStream(rawFullFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
                {
                    actualRawCrc = CopyBase64File(rawBase64, rawFileLength, output);
                    output.Flush(true);
                }

                rawBase64.AssertConsumed();

                if (actualRawCrc != expectedRawCrc)
                {
                    throw new InvalidDataException($"파일 무결성 검증에 실패했습니다: {rawRelativeFilePath}");
                }

                int rawLineEnd = payload.ReadByte();

                if (rawLineEnd == '\r')
                {
                    rawLineEnd = payload.ReadByte();
                }

                if (rawLineEnd != '\n')
                {
                    throw new InvalidDataException("파일 데이터의 끝 표시가 없습니다.");
                }

                ApplyFileMetadata(rawFullFilePath, rawFileAttributes, rawFileTicks);
                continue;
            }

            if (fields.Length != 9 || fields[0] != "F")
            {
                throw new InvalidDataException("파일 항목이 올바르지 않습니다.");
            }

            FileAttributes fileAttributes = ParseAttributes(fields[1]);
            long fileTicks = ParseTicks(fields[2]);
            long rawLength = ParseNonNegativeLong(fields[3], "파일 크기");
            long compressedLength = ParseNonNegativeLong(fields[4], "파일 크기");

            if (!uint.TryParse(fields[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint expectedCrc))
            {
                throw new InvalidDataException("파일 CRC 값이 올바르지 않습니다.");
            }

            byte[] codeLengths;

            try
            {
                codeLengths = Convert.FromBase64String(fields[6]);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("코드 표를 읽지 못했습니다.", ex);
            }

            if (codeLengths.Length != 256)
            {
                throw new InvalidDataException("코드 표 크기가 올바르지 않습니다.");
            }

            long encodedLength = ParseNonNegativeLong(fields[7], "Base64 데이터 크기");

            long expectedEncodedLength = encryptedPayload
                ? EncryptedPayloadLength(compressedLength)
                : Base64Length(compressedLength);

            if (encodedLength != expectedEncodedLength)
            {
                throw new InvalidDataException("기록된 크기와 일치하지 않습니다.");
            }

            if (encryptedPayload && encodedLength > int.MaxValue)
            {
                throw new InvalidDataException("데이터가 너무 큽니다.");
            }

            string relativeFilePath = DecodeText(fields[8]);
            string fullFilePath = GetSafePath(rootFullPath, relativeFilePath);

            if (relativeFilePath.Length == 0)
            {
                throw new InvalidDataException("파일 경로가 비어 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath)!);
            uint actualCrc;

            if (encryptedPayload)
            {
                string encryptedData = ReadAsciiText(payload, encodedLength);
                string huffmanData = Decrypt(encryptedData)
                    ?? throw new InvalidDataException("문자열을 읽지 못했습니다.");
                long expectedHuffmanLength = Base64Length(compressedLength);

                if (huffmanData.Length != expectedHuffmanLength || huffmanData.Any(static character => character > 0x7F))
                {
                    throw new InvalidDataException("기록된 크기와 일치하지 않습니다.");
                }

                using var huffmanInput = new MemoryStream(Encoding.ASCII.GetBytes(huffmanData), false);
                var base64 = new Base64ReadStream(huffmanInput, huffmanData.Length);

                using (var output = new FileStream(fullFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
                {
                    actualCrc = DecompressFile(base64, codeLengths, compressedLength, rawLength, output);
                    output.Flush(true);
                }

                base64.AssertConsumed();
            }
            else
            {
                var base64 = new Base64ReadStream(payload, encodedLength);

                using (var output = new FileStream(fullFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
                {
                    actualCrc = DecompressFile(base64, codeLengths, compressedLength, rawLength, output);
                    output.Flush(true);
                }

                base64.AssertConsumed();
            }

            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException($"파일 무결성 검증에 실패했습니다: {relativeFilePath}");
            }

            int lineEnd = payload.ReadByte();

            if (lineEnd == '\r')
            {
                lineEnd = payload.ReadByte();
            }

            if (lineEnd != '\n')
            {
                throw new InvalidDataException("파일 데이터의 끝 표시가 없습니다.");
            }

            ApplyFileMetadata(fullFilePath, fileAttributes, fileTicks);
        }

        foreach (var directory in directories.OrderByDescending(static item => item.RelativePath.Length))
        {
            string fullPath = GetSafePath(rootFullPath, directory.RelativePath);
            ApplyFileMetadata(fullPath, directory.Attributes, directory.Ticks);
        }
    }

    private static uint CopyBase64File(Base64ReadStream input, long expectedLength, Stream output)
    {
        var crc = new Crc32();
        byte[] buffer = new byte[BufferSize];
        long totalLength = 0;
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalLength = checked(totalLength + read);

            if (totalLength > expectedLength)
            {
                throw new InvalidDataException("파일 크기가 올바르지 않습니다.");
            }

            crc.Update(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        if (totalLength != expectedLength)
        {
            throw new InvalidDataException("파일 크기가 올바르지 않습니다.");
        }

        return crc.Value;
    }

    private static FileAttributes ParseAttributes(string value)
    {
        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int raw))
        {
            throw new InvalidDataException("파일 특성 값이 올바르지 않습니다.");
        }

        return (FileAttributes)raw & StoredAttributes;
    }

    private static long ParseTicks(string value)
    {
        long ticks = ParseNonNegativeLong(value, "파일 시간");

        if (ticks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("파일 시간 값이 범위를 벗어났습니다.");
        }

        return ticks;
    }

    private static long ParseNonNegativeLong(string value, string description)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result < 0)
        {
            throw new InvalidDataException($"{description} 값이 올바르지 않습니다.");
        }

        return result;
    }

    private static string GetSafePath(string root, string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return root;
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':'))
        {
            throw new InvalidDataException("사용할 수 없는 문자가 있습니다.");
        }

        string[] segments = relativePath.Split('/');

        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("안전하지 않습니다.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("폴더를 벗어납니다.");
        }

        return fullPath;
    }

    private static void ApplyFileMetadata(string path, FileAttributes attributes, long ticks)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            Directory.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));
            return;
        }

        File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));
        FileAttributes stored = attributes & StoredAttributes;

        if (stored == 0)
        {
            stored = FileAttributes.Normal;
        }

        File.SetAttributes(path, stored);
    }

    private static string FindDestination(string directory, string rootName)
    {
        return Path.Combine(directory, rootName);
    }

    private static string FindTemporaryDirectory(string destination)
    {
        bool destinationExists = Directory.Exists(destination);
        string temporaryRoot = destinationExists ? destination : Path.GetDirectoryName(destination)!;
        string temporaryName = destinationExists ? ".hdc-tmp" : Path.GetFileName(destination) + ".hdc-tmp";
        string candidate = Path.Combine(temporaryRoot, temporaryName);
        int suffix = 2;

        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(temporaryRoot, temporaryName + "_" + suffix.ToString(CultureInfo.InvariantCulture));
            suffix++;
        }

        return candidate;
    }

    private static void InstallHDCEedTree(string temporaryDirectory, string destination)
    {
        string fullTemporaryDirectory = Path.GetFullPath(temporaryDirectory);
        string fullDestination = Path.GetFullPath(destination);

        if (File.Exists(fullDestination))
        {
            throw new IOException($"같은 이름의 파일이 있습니다: {fullDestination}");
        }

        if (!Directory.Exists(fullDestination))
        {
            Directory.Move(fullTemporaryDirectory, fullDestination);
            return;
        }

        string[] sourceDirectories = Directory.GetDirectories(fullTemporaryDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path.Length)
            .ToArray();
        string[] sourceFiles = Directory.GetFiles(fullTemporaryDirectory, "*", SearchOption.AllDirectories);

        foreach (string sourceDirectory in sourceDirectories)
        {
            string relativePath = Path.GetRelativePath(fullTemporaryDirectory, sourceDirectory);
            string targetPath = GetSafePath(fullDestination, relativePath.Replace(Path.DirectorySeparatorChar, '/'));

            if (File.Exists(targetPath))
            {
                throw new IOException($"같은 이름의 파일이 있습니다: {targetPath}");
            }
        }

        var conflictingFiles = new List<(string Source, string Target)>();

        foreach (string sourceFile in sourceFiles)
        {
            string relativePath = Path.GetRelativePath(fullTemporaryDirectory, sourceFile);
            string targetPath = GetSafePath(fullDestination, relativePath.Replace(Path.DirectorySeparatorChar, '/'));

            if (Directory.Exists(targetPath))
            {
                throw new IOException($"같은 이름의 폴더가 있습니다: {targetPath}");
            }

            if (File.Exists(targetPath) && !FilesAreEqual(sourceFile, targetPath))
            {
                conflictingFiles.Add((sourceFile, targetPath));
            }
        }

        if (conflictingFiles.Count > 0)
        {
            Console.WriteLine($"파일 {conflictingFiles.Count}개가 파일과 다릅니다.");
            Console.Write($"폴더의 파일을 덮어쓰려면 Y 입력 [{fullDestination}]: ");

            if (!string.Equals(Console.ReadLine(), "Y", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("파일을 보존하고 취소했습니다.");
            }
        }

        Directory.CreateDirectory(fullDestination);

        foreach (string sourceDirectory in sourceDirectories)
        {
            string relativePath = Path.GetRelativePath(fullTemporaryDirectory, sourceDirectory);
            string targetPath = GetSafePath(fullDestination, relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(targetPath);
        }

        foreach (string sourceFile in sourceFiles)
        {
            string relativePath = Path.GetRelativePath(fullTemporaryDirectory, sourceFile);
            string targetPath = GetSafePath(fullDestination, relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (File.Exists(targetPath) && FilesAreEqual(sourceFile, targetPath))
            {
                File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourceFile));
                continue;
            }

            if (File.Exists(targetPath) && (File.GetAttributes(targetPath) & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(targetPath, FileAttributes.Normal);
            }

            File.Copy(sourceFile, targetPath, true);
            File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourceFile));
        }

        foreach (string sourceDirectory in sourceDirectories.OrderByDescending(static path => path.Length))
        {
            string relativePath = Path.GetRelativePath(fullTemporaryDirectory, sourceDirectory);
            string targetPath = GetSafePath(fullDestination, relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.SetLastWriteTimeUtc(targetPath, Directory.GetLastWriteTimeUtc(sourceDirectory));
        }

        Directory.SetLastWriteTimeUtc(fullDestination, Directory.GetLastWriteTimeUtc(fullTemporaryDirectory));
    }

    private static bool FilesAreEqual(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);

        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        byte[] firstBuffer = new byte[BufferSize];
        byte[] secondBuffer = new byte[BufferSize];

        while (true)
        {
            int firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            int secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);

            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            for (int i = 0; i < firstRead; i++)
            {
                if (firstBuffer[i] != secondBuffer[i])
                {
                    return false;
                }
            }
        }
    }

    private static long Base64Length(long byteLength)
    {
        return checked(((byteLength + 2) / 3) * 4);
    }

    private static long EncryptedPayloadLength(long compressedLength)
    {
        return AesEncryptedTextLength(Base64Length(compressedLength));
    }

    private static long AesEncryptedTextLength(long plainTextLength)
    {
        return Base64Length(AesEncryptedByteLength(plainTextLength));
    }

    private static long AesEncryptedByteLength(long plainTextLength)
    {
        return checked((plainTextLength / 16 + 1) * 16);
    }

    private static (byte[] Data, int PlainPrefixLength, int EncryptedPrefixLength) EncryptArchivePrefix(byte[] archive)
    {
        int plainPrefixLength = Math.Min(100, archive.Length);
        byte[] encryptedPrefix = Encrypt(archive.AsSpan(0, plainPrefixLength).ToArray());
        byte[] transformedArchive = new byte[encryptedPrefix.Length + archive.Length - plainPrefixLength];
        Buffer.BlockCopy(encryptedPrefix, 0, transformedArchive, 0, encryptedPrefix.Length);
        Buffer.BlockCopy(archive, plainPrefixLength, transformedArchive, encryptedPrefix.Length, archive.Length - plainPrefixLength);
        return (transformedArchive, plainPrefixLength, encryptedPrefix.Length);
    }

    private static byte[] CompressEncryptedArchive(byte[] transformedArchive, int plainPrefixLength, int encryptedPrefixLength)
    {
        byte[] source = transformedArchive;
        var frequencies = new long[256];
        var crc = new Crc32();

        foreach (byte value in source)
        {
            frequencies[value]++;
            crc.Update(value);
        }

        byte[] codeLengths = BuildCodeLengths(frequencies);
        bool[][] codes = BuildCodes(codeLengths);
        using var compressed = new MemoryStream();
        var bitWriter = new HuffmanBitWriter(compressed, codes);

        foreach (byte value in source)
        {
            bitWriter.WriteSymbol(value);
        }

        long compressedLength = bitWriter.Finish();

        if (compressed.Length != compressedLength)
        {
            throw new InvalidOperationException("데이터 크기가 일치하지 않습니다.");
        }

        byte[] compressedBytes = compressed.ToArray();

        using var container = new MemoryStream();

        using (var output = new BinaryWriter(container, Encoding.ASCII, true))
        {
            output.Write(HuffmanContainerMagic);
            output.Write(plainPrefixLength);
            output.Write(encryptedPrefixLength);
            output.Write((long)source.Length);
            output.Write(compressedLength);
            output.Write(crc.Value);
            output.Write(codeLengths);
            output.Write(compressedBytes);
        }

        return container.ToArray();
    }

    private static byte[] CompressBrotliLayer(byte[] source)
    {
        using var compressed = new MemoryStream();

        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, true))
        {
            brotli.Write(source, 0, source.Length);
        }

        byte[] compressedBytes = compressed.ToArray();
        var crc = new Crc32();
        crc.Update(source, 0, source.Length);
        using var container = new MemoryStream();

        using (var output = new BinaryWriter(container, Encoding.ASCII, true))
        {
            output.Write(BrotliContainerMagic);
            output.Write((long)source.Length);
            output.Write((long)compressedBytes.Length);
            output.Write(crc.Value);
            output.Write(compressedBytes);
        }

        return container.ToArray();
    }

    private static bool TryDecompressEncryptedArchive(string encodedContainer, out string? archiveText)
    {
        archiveText = null;
        byte[] container;

        try
        {
            container = Convert.FromBase64String(encodedContainer);
        }
        catch (FormatException)
        {
            return false;
        }

        if (container.Length < LegacyHuffmanContainerMagic.Length ||
            !container.AsSpan(0, LegacyHuffmanContainerMagic.Length).SequenceEqual(LegacyHuffmanContainerMagic))
        {
            return false;
        }

        using var input = new MemoryStream(container, false);
        using var reader = new BinaryReader(input, Encoding.ASCII, true);
        _ = reader.ReadBytes(LegacyHuffmanContainerMagic.Length);

        int plainPrefixLength = reader.ReadInt32();
        int encryptedPrefixLength = reader.ReadInt32();
        long rawLength = reader.ReadInt64();
        long compressedLength = reader.ReadInt64();
        uint expectedCrc = reader.ReadUInt32();
        byte[] codeLengths = reader.ReadBytes(256);

        if (plainPrefixLength < 0 || plainPrefixLength > 100 ||
            encryptedPrefixLength != AesEncryptedTextLength(plainPrefixLength) || encryptedPrefixLength > rawLength ||
            rawLength < 0 || rawLength > int.MaxValue || compressedLength < 0 || compressedLength > int.MaxValue ||
            codeLengths.Length != 256 || input.Length - input.Position != compressedLength)
        {
            throw new InvalidDataException("HDC 데이터가 올바르지 않습니다.");
        }

        byte[] compressedBytes = reader.ReadBytes((int)compressedLength);

        if (compressedBytes.LongLength != compressedLength)
        {
            throw new InvalidDataException("데이터가 중간에 끝났습니다.");
        }

        using var compressed = new MemoryStream(compressedBytes, false);
        using var decrypted = new MemoryStream();
        uint actualCrc = DecompressFile(compressed, codeLengths, compressedLength, rawLength, decrypted);

        if (actualCrc != expectedCrc)
        {
            throw new InvalidDataException("파일 무결성 검증에 실패했습니다.");
        }

        string transformedArchive = Encoding.ASCII.GetString(decrypted.ToArray());

        if (transformedArchive.Length != rawLength || transformedArchive.Any(static character => character > 0x7F))
        {
            throw new InvalidDataException("문자열을 읽지 못했습니다.");
        }

        string encryptedPrefix = transformedArchive[..encryptedPrefixLength];
        string plainPrefix = Decrypt(encryptedPrefix)
            ?? throw new InvalidDataException("문자열을 읽지 못했습니다.");

        if (plainPrefix.Length != plainPrefixLength || plainPrefix.Any(static character => character > 0x7F))
        {
            throw new InvalidDataException("문자열을 읽지 못했습니다.");
        }

        archiveText = plainPrefix + transformedArchive[encryptedPrefixLength..];
        return true;
    }

    private static string EncodeText(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string DecodeText(string value)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("문자열을 읽지 못했습니다.", ex);
        }
    }

    private static string ReadAsciiLine(Stream input, bool allowBom = false)
    {
        var bytes = new List<byte>(128);

        while (bytes.Count <= 1_048_576)
        {
            int value = input.ReadByte();

            if (value < 0)
            {
                throw new InvalidDataException("데이터가 중간에 끝났습니다.");
            }

            if (value == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                if (allowBom && bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes.RemoveRange(0, 3);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add((byte)value);
        }

        throw new InvalidDataException("헤더 줄이 너무 깁니다.");
    }

    private static void WriteAsciiLine(Stream output, string value)
    {
        WriteAscii(output, value + "\r\n");
    }

    private static void WriteAscii(Stream output, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
    }

    private static string ReadAsciiText(Stream input, long length)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new InvalidDataException("데이터가 너무 큽니다.");
        }

        byte[] bytes = new byte[(int)length];
        int offset = 0;

        while (offset < bytes.Length)
        {
            int read = input.Read(bytes, offset, bytes.Length - offset);

            if (read == 0)
            {
                throw new InvalidDataException("데이터가 중간에 끝났습니다.");
            }

            offset += read;
        }

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        for (int i = start; i < bytes.Length; i++)
        {
            if (bytes[i] > 0x7F)
            {
                throw new InvalidDataException("문자열을 읽지 못했습니다.");
            }
        }

        return Encoding.ASCII.GetString(bytes, start, bytes.Length - start);
    }

    private sealed class Entry
    {
        public string RelativePath { get; }
        public bool IsDirectory { get; }
        public FileAttributes Attributes { get; }
        public long LastWriteUtcTicks { get; }
        public long Length { get; }
        public string FullPath { get; }

        public Entry(string relativePath, bool isDirectory, FileAttributes attributes, long lastWriteUtcTicks, long length, string fullPath)
        {
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            Attributes = attributes;
            LastWriteUtcTicks = lastWriteUtcTicks;
            Length = length;
            FullPath = fullPath;
        }
    }

    private sealed class FileAnalysis
    {
        public long RawLength { get; }
        public long CompressedLength { get; }
        public uint Crc32 { get; }
        public byte[] CodeLengths { get; }
        public bool[][] Codes { get; }
        public long[] Frequencies { get; }

        public FileAnalysis(long rawLength, long compressedLength, uint crc32, byte[] codeLengths, bool[][] codes, long[] frequencies)
        {
            RawLength = rawLength;
            CompressedLength = compressedLength;
            Crc32 = crc32;
            CodeLengths = codeLengths;
            Codes = codes;
            Frequencies = frequencies;
        }
    }

    private sealed class HuffmanNode
    {
        public int Symbol { get; }
        public long Frequency { get; }
        public int Order { get; }
        public HuffmanNode? Left { get; }
        public HuffmanNode? Right { get; }

        public HuffmanNode(int symbol, long frequency, int order, HuffmanNode? left = null, HuffmanNode? right = null)
        {
            Symbol = symbol;
            Frequency = frequency;
            Order = order;
            Left = left;
            Right = right;
        }
    }

    private sealed class HuffmanBitWriter
    {
        private readonly Stream _output;
        private readonly bool[][] _codes;
        private byte _currentByte;
        private int _bitCount;
        private long _compressedLength;

        public HuffmanBitWriter(Stream output, bool[][] codes)
        {
            _output = output;
            _codes = codes;
        }

        public void WriteSymbol(byte symbol)
        {
            bool[]? code = _codes[symbol];

            if (code is null)
            {
                throw new InvalidDataException("없는 바이트가 발견되었습니다.");
            }

            foreach (bool bit in code)
            {
                _currentByte = (byte)((_currentByte << 1) | (bit ? 1 : 0));
                _bitCount++;

                if (_bitCount == 8)
                {
                    _output.WriteByte(_currentByte);
                    _compressedLength++;
                    _currentByte = 0;
                    _bitCount = 0;
                }
            }
        }

        public long Finish()
        {
            if (_bitCount > 0)
            {
                _currentByte <<= 8 - _bitCount;
                _output.WriteByte(_currentByte);
                _compressedLength++;
            }

            return _compressedLength;
        }
    }

    private sealed class HuffmanNodeRead
    {
        public int Symbol = -1;
        public HuffmanNodeRead? Zero;
        public HuffmanNodeRead? One;
    }

    private static uint DecompressFile(Stream input, byte[] lengths, long compressedLength, long rawLength, Stream output)
    {
        bool[][] codes = BuildCodes(lengths);
        var root = new HuffmanNodeRead();
        var symbols = Enumerable.Range(0, 256)
            .Where(symbol => lengths[symbol] > 0)
            .OrderBy(symbol => lengths[symbol])
            .ThenBy(static symbol => symbol)
            .ToArray();
        BigInteger code = BigInteger.Zero;
        int previousLength = 0;

        foreach (int symbol in symbols)
        {
            int length = lengths[symbol];
            code <<= length - previousLength;

            if (code >= (BigInteger.One << length))
            {
                throw new InvalidDataException("코드 표가 올바르지 않습니다.");
            }

            HuffmanNodeRead node = root;

            for (int bit = 0; bit < length; bit++)
            {
                if (node.Symbol >= 0)
                {
                    throw new InvalidDataException("코드가 서로 겹칩니다.");
                }

                bool one = ((code >> (length - bit - 1)) & BigInteger.One) != BigInteger.Zero;
                HuffmanNodeRead? next = one ? node.One : node.Zero;

                if (next is null)
                {
                    next = new HuffmanNodeRead();

                    if (one)
                    {
                        node.One = next;
                    }
                    else
                    {
                        node.Zero = next;
                    }
                }

                node = next;
            }

            if (node.Symbol >= 0 || node.Zero is not null || node.One is not null)
            {
                throw new InvalidDataException("코드가 서로 겹칩니다.");
            }

            node.Symbol = symbol;
            code++;
            previousLength = length;
        }

        if (rawLength == 0)
        {
            if (compressedLength != 0 || symbols.Length != 0)
            {
                throw new InvalidDataException("정보가 올바르지 않습니다.");
            }

            return new Crc32().Value;
        }

        if (symbols.Length == 0)
        {
            throw new InvalidDataException("허프만 코드 표가 비어 있습니다.");
        }

        var crc = new Crc32();
        byte[] buffer = new byte[BufferSize];
        int buffered = 0;
        long bytesRead = 0;
        byte currentByte = 0;
        int bitsRemaining = 0;
        HuffmanNodeRead current = root;

        for (long produced = 0; produced < rawLength; produced++)
        {
            while (current.Symbol < 0)
            {
                int bit = ReadBit(input, ref bytesRead, compressedLength, ref currentByte, ref bitsRemaining);
                current = bit == 0 ? current.Zero! : current.One!;

                if (current is null)
                {
                    throw new InvalidDataException("비트가 코드 표와 맞지 않습니다.");
                }
            }

            byte value = (byte)current.Symbol;
            current = root;
            buffer[buffered++] = value;
            crc.Update(value);

            if (buffered == buffer.Length)
            {
                output.Write(buffer, 0, buffered);
                buffered = 0;
            }
        }

        if (buffered > 0)
        {
            output.Write(buffer, 0, buffered);
        }

        if (bytesRead != compressedLength)
        {
            throw new InvalidDataException("데이터 크기가 예상과 다릅니다.");
        }

        return crc.Value;
    }

    private static int ReadBit(Stream input, ref long bytesRead, long expectedBytes, ref byte currentByte, ref int bitsRemaining)
    {
        if (bitsRemaining == 0)
        {
            int value = input.ReadByte();

            if (value < 0 || bytesRead >= expectedBytes)
            {
                throw new InvalidDataException("비트가 중간에 끝났습니다.");
            }

            currentByte = (byte)value;
            bitsRemaining = 8;
            bytesRead++;
        }

        int bit = (currentByte & 0x80) == 0 ? 0 : 1;
        currentByte <<= 1;
        bitsRemaining--;
        return bit;
    }

    private sealed class Base64WriteStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[3];
        private int _count;
        private bool _finished;

        public Base64WriteStream(Stream output)
        {
            _output = output;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                WriteByte(buffer[offset + i]);
            }
        }

        public override void WriteByte(byte value)
        {
            if (_finished)
            {
                throw new InvalidOperationException("출력이 이미 끝났습니다.");
            }

            _buffer[_count++] = value;

            if (_count == 3)
            {
                Emit(3);
            }
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            if (_count > 0)
            {
                Emit(_count);
            }

            _finished = true;
        }

        private void Emit(int count)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(_buffer, 0, count));
            _output.Write(bytes, 0, bytes.Length);
            _count = 0;
        }
    }

    private sealed class Base64ReadStream : Stream
    {
        private readonly Stream _input;
        private readonly long _expectedCharacters;
        private long _charactersRead;
        private long _decodedBytes;
        private readonly byte[] _decoded = new byte[3];
        private int _decodedIndex;
        private int _decodedCount;
        private byte _currentByte;
        private int _currentBitsRemaining;

        public Base64ReadStream(Stream input, long expectedCharacters)
        {
            _input = input;
            _expectedCharacters = expectedCharacters;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int CurrentBitsRemaining => _currentBitsRemaining;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;

            while (read < count)
            {
                int value = ReadByte();

                if (value < 0)
                {
                    break;
                }

                buffer[offset + read] = (byte)value;
                read++;
            }

            return read;
        }

        public override int ReadByte()
        {
            if (_decodedIndex >= _decodedCount)
            {
                if (_charactersRead >= _expectedCharacters)
                {
                    return -1;
                }

                if (_expectedCharacters - _charactersRead < 4)
                {
                    throw new InvalidDataException("데이터 길이가 올바르지 않습니다.");
                }

                char[] chars = new char[4];

                for (int i = 0; i < chars.Length; i++)
                {
                    int value = _input.ReadByte();

                    if (value < 0 || value > 127)
                    {
                        throw new InvalidDataException("데이터가 중간에 끝났습니다.");
                    }

                    chars[i] = (char)value;
                }

                _charactersRead += 4;
                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64CharArray(chars, 0, chars.Length);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("데이터를 읽지 못했습니다.", ex);
                }

                _decodedCount = bytes.Length;
                _decodedIndex = 0;

                if (_decodedCount == 0)
                {
                    throw new InvalidDataException("데이터 블록이 비어 있습니다.");
                }

                Array.Copy(bytes, _decoded, _decodedCount);
            }

            _decodedBytes++;
            return _decoded[_decodedIndex++];
        }

        public void SetCurrentByte(byte value)
        {
            _currentByte = value;
            _currentBitsRemaining = 8;
        }

        public int ReadBit()
        {
            int bit = (_currentByte & 0x80) == 0 ? 0 : 1;
            _currentByte <<= 1;
            _currentBitsRemaining--;
            return bit;
        }

        public void AssertConsumed()
        {
            if (_charactersRead != _expectedCharacters)
            {
                throw new InvalidDataException("데이터가 모두 사용되지 않았습니다.");
            }
        }
    }

    private sealed class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _value = 0xFFFFFFFF;
        public uint Value => ~_value;

        public void Update(byte value)
        {
            _value = Table[(_value ^ value) & 0xFF] ^ (_value >> 8);
        }

        public void Update(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Update(data[offset + i]);
            }
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];

            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }

    // SELECT TO_BASE64(AES_ENCRYPT('Password', 'ProjectName')); SELECT CAST(AES_DECRYPT(FROM_BASE64('코드'), 'ProjectName') AS CHAR);
    public static string? Decrypt(string sData, string? sKey = null)	// CAST(AES_DECRYPT(FROM_BASE64(sData), 마스터 키) AS CHAR) 호환 (sKey: 확인할 키, 없음: 마스터 키, 키 없음·실패: null)
	{
		sKey ??= "HDCAESX";

		if (string.IsNullOrEmpty(sData) || sKey == "")
		{
			return null;
		}

		byte[]	arrData;

		try
		{
			arrData = Convert.FromBase64String(sData);
		}
		catch (FormatException)
		{
			return null;
		}

		if (arrData.Length == 0 || arrData.Length % 16 != 0)	// AES 블록 단위 아님
		{
			return null;
		}

		try
		{
			using Aes kAes = Aes.Create();

			kAes.Key = GetMySQLKey(sKey);

			return Encoding.UTF8.GetString(kAes.DecryptEcb(arrData, PaddingMode.PKCS7));	// CAST AS CHAR (utf8mb4)
		}
		catch (CryptographicException)	// 키 불일치·패딩 오류
		{
			return null;
		}
	}

	public static string Encrypt(string sText, string? sKey = null)	// TO_BASE64(AES_ENCRYPT(sText, 키)) 호환 (Decrypt 반대, sKey: 쓸 키 (예: 외부 API 토큰 = sCode 를 sRand 키로), 없음: 마스터 키 → 설정 sCode 값, 키 없음: 예외)
	{
		sKey ??= "HDCAESX";

		if (sKey == "")
		{
			throw new InvalidOperationException("Error");
		}

		using Aes kAes = Aes.Create();

		kAes.Key = GetMySQLKey(sKey);

		return Convert.ToBase64String(kAes.EncryptEcb(Encoding.UTF8.GetBytes(sText), PaddingMode.PKCS7));
	}

    public static byte[] Encrypt(byte[] data, string? sKey = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        sKey ??= "HDCAESX";

        if (sKey == "")
        {
            throw new InvalidOperationException("Error");
        }

        using Aes aes = Aes.Create();
        aes.Key = GetMySQLKey(sKey);
        return aes.EncryptEcb(data, PaddingMode.PKCS7);
    }

    public static byte[]? Decrypt(byte[] data, string? sKey = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        sKey ??= "HDCAESX";

        if (data.Length == 0 || data.Length % 16 != 0 || sKey == "")
        {
            return null;
        }

        try
        {
            using Aes aes = Aes.Create();
            aes.Key = GetMySQLKey(sKey);
            return aes.DecryptEcb(data, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

	private static byte[] GetMySQLKey(string sKey)	// MySQL 키 변환: 16바이트 버퍼에 순환 XOR
	{
		byte[]	arrKey		= new byte[16];
		byte[]	arrSource	= Encoding.UTF8.GetBytes(sKey);

		for (int nIndex = 0; nIndex < arrSource.Length; ++nIndex)
		{
			arrKey[nIndex % arrKey.Length] ^= arrSource[nIndex];
		}

		return arrKey;
	}
}
