using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security;
using System.Text;

internal static class HDC
{
    private const string ArchiveMagic = "HDC-HUFFMAN-1";
    private const int BufferSize = 128 * 1024;
    private const int MaximumEntries = 1_000_000;
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
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {

                WriteAsciiLine(output, ArchiveMagic);
                WriteAsciiLine(output, "ROOT|" + EncodeText(Path.GetFileName(sourceRoot)));
                WriteAsciiLine(output, "COUNT|" + entries.Count.ToString(CultureInfo.InvariantCulture));

                foreach (Entry entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        WriteAsciiLine(output, string.Join('|',
                            "D",
                            ((int)entry.Attributes).ToString("X8", CultureInfo.InvariantCulture),
                            entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
                            EncodeText(entry.RelativePath)));
                        continue;
                    }

                    FileAnalysis analysis = AnalyzeFile(entry);
                    long encodedLength = Base64Length(analysis.CompressedLength);
                    string lengths = Convert.ToBase64String(analysis.CodeLengths);

                    WriteAsciiLine(output, string.Join('|',
                        "F",
                        ((int)entry.Attributes).ToString("X8", CultureInfo.InvariantCulture),
                        entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
                        analysis.RawLength.ToString(CultureInfo.InvariantCulture),
                        analysis.CompressedLength.ToString(CultureInfo.InvariantCulture),
                        analysis.Crc32.ToString("X8", CultureInfo.InvariantCulture),
                        lengths,
                        encodedLength.ToString(CultureInfo.InvariantCulture),
                        EncodeText(entry.RelativePath)));

                    CompressFile(entry, analysis, output);
                    WriteAscii(output, "\r\n");
                }

                WriteAsciiLine(output, "END");
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

    private static void CompressFile(Entry entry, FileAnalysis analysis, Stream output)
    {
        var crc = new Crc32();
        long rawLength = 0;
        var base64 = new Base64WriteStream(output);
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
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (string rootName, int entryCount) = ReadArchiveHeader(input);
        string destination = FindDestination(archiveDirectory, rootName);

        if (File.Exists(destination))
        {
            throw new IOException($"같은 이름의 파일이 있습니다: {destination}");
        }

        string temporaryDirectory = FindTemporaryDirectory(destination);
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            HDCEEntries(input, temporaryDirectory, entryCount);

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

    private static (string RootName, int EntryCount) ReadArchiveHeader(Stream payload)
    {
        if (ReadAsciiLine(payload, true) != ArchiveMagic)
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

        return (rootName, entryCount);
    }

    private static void HDCEEntries(Stream payload, string temporaryDirectory, int entryCount)
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

            if (encodedLength != Base64Length(compressedLength))
            {
                throw new InvalidDataException("기록된 크기와 일치하지 않습니다.");
            }

            string relativeFilePath = DecodeText(fields[8]);
            string fullFilePath = GetSafePath(rootFullPath, relativeFilePath);

            if (relativeFilePath.Length == 0)
            {
                throw new InvalidDataException("파일 경로가 비어 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath)!);
            var base64 = new Base64ReadStream(payload, encodedLength);
            uint actualCrc;

            using (var output = new FileStream(fullFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                actualCrc = DecompressFile(base64, codeLengths, compressedLength, rawLength, output);
                output.Flush(true);
            }

            base64.AssertConsumed();

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
        private readonly Base64WriteStream _output;
        private readonly bool[][] _codes;
        private byte _currentByte;
        private int _bitCount;
        private long _compressedLength;

        public HuffmanBitWriter(Base64WriteStream output, bool[][] codes)
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

    private static uint DecompressFile(Base64ReadStream input, byte[] lengths, long compressedLength, long rawLength, Stream output)
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
        HuffmanNodeRead current = root;

        for (long produced = 0; produced < rawLength; produced++)
        {
            while (current.Symbol < 0)
            {
                int bit = ReadBit(input, ref bytesRead, compressedLength);
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

    private static int ReadBit(Base64ReadStream input, ref long bytesRead, long expectedBytes)
    {
        if (input.CurrentBitsRemaining == 0)
        {
            int value = input.ReadByte();

            if (value < 0 || bytesRead >= expectedBytes)
            {
                throw new InvalidDataException("비트가 중간에 끝났습니다.");
            }

            input.SetCurrentByte((byte)value);
            bytesRead++;
        }

        return input.ReadBit();
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
}
