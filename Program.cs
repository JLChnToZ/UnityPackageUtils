using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using CommandLine;
using DotNet.Globbing;

[assembly: AssemblyProduct("UnityPackageUtil")]
[assembly: AssemblyTitle("UnityPackageUtil")]
[assembly: AssemblyDescription("A command line tool to pack and extract Unity packages")]
[assembly: AssemblyCompany("Explosive Theorem Lab")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

public class Program {
    class Options {
        public string[] sources = Array.Empty<string>();
        string[] filters = Array.Empty<string>();
        Glob[]? globs;

        [Value(0, MetaName = "source", Required = false, HelpText = "Source directory or Unity package")]
        public IEnumerable<string> Sources {
            get => sources;
            set => sources = value?.ToArray() ?? Array.Empty<string>();
        }

        [Option('o', "output", Required = false, HelpText = "Destination directory or Unity package")]
        public string? Dest { get; set; }

        [Option('n', "dryrun", Required = false, HelpText = "Do not write to disk")]
        public bool DryRun { get; set; }

        [Option('f', "filter", Required = false, HelpText = "Glob pattern to filter files")]
        public IEnumerable<string> Filters {
            get => filters;
            set {
                filters = value?.ToArray() ?? Array.Empty<string>();
                globs = null;
            }
        }

        public Glob[]? GlobFilters {
            get {
                if (globs == null && filters.Length > 0) {
                    globs = new Glob[filters.Length];
                    for (int i = 0; i < filters.Length; i++)
                        globs[i] = Glob.Parse(filters[i]);
                }
                return globs;
            }
        }
    }

    [Verb("pack", aliases: new [] { "p" }, HelpText = "Pack Unity package")]
    class PackOptions : Options {
        [Option("icon", Required = false, HelpText = "Icon file, must be a PNG file")]
        public string? Icon { get; set; }
    }

    [Verb("extract", aliases: new [] { "e", "unpack" }, HelpText = "Extract Unity package")]
    class ExtractOptions : Options {

        [Option('r', "replace", Required = false, HelpText = "Replace existing files if conflict")]
        public bool ReplaceAll { get; set; }

        [Option('k', "keep", Required = false, HelpText = "Keep existing files if conflict")]
        public bool KeepAll { get; set; }
    }

    public static void Main(string[] args) => Parser.Default
        .ParseArguments<PackOptions, ExtractOptions>(args)
        .MapResult(
            (Func<PackOptions, int>)PackUnityPackage,
            (Func<ExtractOptions, int>)ExtractUnityPackage,
            _ => 1
        );

    private static int ExtractUnityPackage(ExtractOptions options) {
        var destPath = options.Dest;
        foreach (var srcPath in options.sources) {
            if (string.IsNullOrEmpty(destPath))
                destPath = Path.GetDirectoryName(srcPath)!;
            using var srcStream = File.OpenRead(srcPath);
            ExtractUnityPackage(
                srcStream,
                options.DryRun ? null : destPath,
                options.GlobFilters,
                options.DryRun ? false :
                options.ReplaceAll ? true :
                options.KeepAll ? false :
                null
            );
        }
        return 0;
    }

    private static int PackUnityPackage(PackOptions options) {
        var srcPath = options.sources;
        var destPath = options.Dest;
        if (string.IsNullOrEmpty(destPath)) {
            if (srcPath != null && srcPath.Length > 0) {
                var name = Path.GetFileName(srcPath[0]);
                if (string.IsNullOrEmpty(name)) name = Path.GetFileName(srcPath[0].TrimEnd(Path.DirectorySeparatorChar));
                destPath = $"{name}.unitypackage";
            } else
                destPath = $"{Path.GetFileName(Directory.GetCurrentDirectory())}.unitypackage";
            Console.WriteLine($"No destination specified, will output to {destPath}");
        }
        if (srcPath == null || srcPath.Length == 0) {
            Console.WriteLine("No source specified, will pack current directory");
            var cwd = Path.GetDirectoryName(Path.Combine(Directory.GetCurrentDirectory(), destPath))!;
            if (IsUnityProject(cwd))
                srcPath = new string[] {
                    Path.Combine(cwd, "Assets"),
                    Path.Combine(cwd, "Packages"),
                };
        }
        using var destStream = options.DryRun ? Stream.Null : File.OpenWrite(destPath);
        PackUnityPackage(srcPath, destStream, options.Icon, options.GlobFilters);
        return 0;
    }

    private static void ExtractUnityPackage(Stream stream, string? destFolder, Glob[]? filters, bool? replace) {
        using var gzStream = new GZipInputStream(stream);
        using var tarStream = new TarInputStream(gzStream, Encoding.UTF8);
        var fileMap = new Dictionary<Guid, (Stream? assetStream, string? meta, string? pathName)>();
        for (TarEntry entry; (entry = tarStream.GetNextEntry()) != null;) {
            var pathSplitted = entry.Name.Split('/', '\\');
            int offset = 0;
            for (int i = 0; i < pathSplitted.Length; i++) {
                if (string.IsNullOrEmpty(pathSplitted[i]) || pathSplitted[i] == ".")
                    offset = i + 1;
                else break;
            }
            if (pathSplitted.Length - offset != 2 ||
                !Guid.TryParseExact(pathSplitted[pathSplitted.Length - 2], "N", out var guid)) {
                Console.WriteLine($"(Ignored) {entry.Name}");
                continue;
            }
            fileMap.TryGetValue(guid, out var data);
            switch (pathSplitted[pathSplitted.Length - 1]) {
                case "asset": {
                    var ms = new MemoryStream();
                    tarStream.CopyEntryContents(ms);
                    ms.Position = 0;
                    data.assetStream = ms;
                    break;
                }
                case "asset.meta": {
                    using var ms = new MemoryStream();
                    tarStream.CopyEntryContents(ms);
                    ms.Position = 0;
                    using var streamReader = new StreamReader(ms, Encoding.UTF8);
                    data.meta = streamReader.ReadToEnd();
                    break;
                }
                case "pathname": {
                    using var ms = new MemoryStream();
                    tarStream.CopyEntryContents(ms);
                    ms.Position = 0;
                    using var streamReader = new StreamReader(ms, Encoding.UTF8);
                    data.pathName = streamReader.ReadLine();
                    break;
                }
                default: continue;
            }
            if (data.assetStream != null && data.pathName != null && data.meta != null) {
                fileMap.Remove(guid);
                if (IsFiltered(data.pathName, filters)) {
                    Console.WriteLine($"(Skipped) {data.pathName} (GUID: {guid})");
                    continue;
                }
                if (string.IsNullOrEmpty(destFolder)) {
                    Console.WriteLine($"{data.pathName} (GUID: {guid})");
                    continue;
                }
                var assetPath = Path.Combine(destFolder, data.pathName);
                RecursiveCreateDirectory(assetPath);
                if (File.Exists(assetPath)) {
                    if (!replace.HasValue) {
                        Console.WriteLine($"File already exists: {assetPath}, replace?");
                        Console.Write("(Y = Yes, N = No, Shift+Y = Yes to All, Shift+N = No to All) ");
                        while (true) {
                            var key = Console.ReadKey(true);
                            switch (key.Key) {
                                case ConsoleKey.Y:
                                    Console.WriteLine("Y");
                                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) replace = true;
                                    goto replaceOnce;
                                case ConsoleKey.N:
                                    Console.WriteLine("N");
                                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) replace = false;
                                    goto skipOnce;
                            }
                        }
                        skipOnce: continue;
                        replaceOnce:;
                    } else if (!replace.Value) {
                        Console.WriteLine($"(Skipped) {data.pathName} (GUID: {guid})");
                        continue;
                    }
                }
                Console.WriteLine($"{data.pathName} (GUID: {guid})");
                using var outfs = new FileStream(assetPath, FileMode.OpenOrCreate, FileAccess.Write);
                data.assetStream.CopyTo(outfs);
                data.assetStream.Dispose();
                File.WriteAllText($"{assetPath}.meta", data.meta, Encoding.UTF8);
            } else
                fileMap[guid] = data;
        }
    }

    private static void PackUnityPackage(string[]? srcPaths, Stream dest ,string? iconPath, Glob[]? filters) {
        var srcDirectoryPath = FindUnityProjectRootPath(srcPaths);
        using var gzStream = new GZipOutputStream(dest);
        using var tarStream = new TarOutputStream(gzStream, Encoding.UTF8);
        var pendingStack = new Stack<FileSystemInfo>();
        var processedFiles = new HashSet<string>();
        if (srcPaths != null && srcPaths.Length > 0)
            foreach (var path in srcPaths) {
                if (File.Exists(path))
                    pendingStack.Push(new FileInfo(path));
                else if (Directory.Exists(path))
                    pendingStack.Push(new DirectoryInfo(path));
            }
        else
            pendingStack.Push(new DirectoryInfo(Directory.GetCurrentDirectory()));
        while (pendingStack.Count > 0) {
            var info = pendingStack.Pop();
            if (info is DirectoryInfo dirInfo)
                foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                    ProcessSingleEntry(tarStream, entry, srcDirectoryPath, pendingStack, filters, processedFiles);
            else if (info is FileInfo)
                ProcessSingleEntry(tarStream, info, srcDirectoryPath, pendingStack, filters, processedFiles);
        }
        CheckAndWritePNGFile(tarStream, iconPath, ".icon.png");
    }

    private static void ProcessSingleEntry(
        TarOutputStream tarStream,
        FileSystemInfo entry,
        string srcDirectoryPath,
        Stack<FileSystemInfo> pendingStack,
        Glob[]? filters,
        HashSet<string> processedFiles
    ) {
        if (string.Equals(entry.Extension, ".meta", StringComparison.OrdinalIgnoreCase) &&
            entry.Attributes.HasFlag(FileAttributes.Normal)) {
            var nonMetaFile = Path.GetFileNameWithoutExtension(entry.FullName);
            if (processedFiles.Contains(nonMetaFile)) return;
            if (Directory.Exists(nonMetaFile))
                pendingStack.Push(new DirectoryInfo(nonMetaFile));
            else if (File.Exists(nonMetaFile))
                pendingStack.Push(new FileInfo(nonMetaFile));
            return;
        }
        if (!processedFiles.Add(entry.FullName)) return;
        if (entry is DirectoryInfo) {
            pendingStack.Push(entry);
            return;
        }
        var relPath = Path.GetRelativePath(srcDirectoryPath, entry.FullName).Replace('\\', '/');
        if (!TryFindGuidFromFile(entry.FullName, out var metaFile, out var guid)) {
            Console.WriteLine($"(Ignored) {relPath} (GUID not found or invalid)");
            return;
        }
        if (IsFiltered(relPath, filters)) {
            Console.WriteLine($"(Skipped) {relPath} (GUID: {guid})");
            return;
        }
        Console.WriteLine($"{relPath} (GUID: {guid})");
        var guidStr = guid.ToString("N");
        using (var sr = new StreamWriter(new MemoryStream(), Encoding.UTF8, leaveOpen: true)) {
            sr.Write(relPath);
            sr.Flush();
            var ms = sr.BaseStream;
            ms.Seek(0, SeekOrigin.Begin);
            WriteFile(tarStream, $"{guidStr}/pathname", ms);
        }
        if (entry is FileInfo file) {
            WriteFile(tarStream, $"{guidStr}/asset", file);
            WriteFile(tarStream, $"{guidStr}/asset.meta", metaFile);
        }
    }

    private static bool TryFindGuidFromFile(string file, [NotNullWhen(true)] out FileInfo? metaFile, out Guid guid) {
        metaFile = new FileInfo($"{file}.meta");
        if (metaFile.Exists) {
            using var contents = new StreamReader(metaFile.OpenRead(), Encoding.UTF8);
            string? line;
            while ((line = contents.ReadLine()) != null) {
                if (line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                    return Guid.TryParseExact(line.Substring(5).Trim(), "N", out guid);
            }
        }
        metaFile = null;
        guid = default;
        return false;
    }

    private static void RecursiveCreateDirectory(string assetPath) {
        var pathSplitted = assetPath.Split('/', '\\');
        var destPath = pathSplitted[0];
        for (int i = 1, count = pathSplitted.Length - 1; i < count; i++) {
            destPath = Path.Combine(destPath, pathSplitted[i]);
            if (!Directory.Exists(destPath)) Directory.CreateDirectory(destPath);
        }
    }

    private static void CheckAndWritePNGFile(TarOutputStream stream, string? srcPath, string destPath) {
        if (string.IsNullOrEmpty(srcPath)) return;
        if (!File.Exists(srcPath)) {
            Console.WriteLine($"File not found: {srcPath}");
            return;
        }
        using var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read);
        if (fs.Length < 8 ||
            fs.ReadByte() != 0x89 ||
            fs.ReadByte() != 0x50 ||
            fs.ReadByte() != 0x4E ||
            fs.ReadByte() != 0x47) {
            Console.WriteLine("Invalid PNG file, icon will not be included.");
            return;
        }
        fs.Seek(0, SeekOrigin.Begin);
        WriteFile(stream, destPath, fs);
    }

    private static void WriteFile(TarOutputStream stream, string destPath, FileInfo srcFile) {
        using var fs = srcFile.OpenRead();
        WriteFile(stream, destPath, fs);
    }

    private static void WriteFile(TarOutputStream stream, string destPath, Stream srcStream) {
        var entry = TarEntry.CreateTarEntry(destPath);
        entry.Size = srcStream.Length;
        stream.PutNextEntry(entry);
        srcStream.CopyTo(stream);
        stream.CloseEntry();
    }

    private static string FindUnityProjectRootPath(string[]? filePaths) {
        ArraySegment<string> rootPathSplitted = default;
        var cwd = Directory.GetCurrentDirectory();
        if (filePaths == null || filePaths.Length == 0)
            rootPathSplitted = cwd.Split(Path.DirectorySeparatorChar);
        else
            foreach (var path in filePaths) {
                var dirPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                if (!Directory.Exists(dirPath)) {
                    if (File.Exists(dirPath)) dirPath = Path.GetDirectoryName(dirPath)!;
                    else throw new ArgumentException($"Path not found: {dirPath}");
                }
                var dirPathSplitted = dirPath.Split(Path.DirectorySeparatorChar);
                if (rootPathSplitted.Array == null) {
                    rootPathSplitted = dirPathSplitted;
                    continue;
                }
                var minCount = Math.Min(rootPathSplitted.Count, dirPathSplitted.Length);
                int i;
                for (i = 0; i < minCount; i++)
                    if (!string.Equals(rootPathSplitted[i], dirPathSplitted[i], StringComparison.OrdinalIgnoreCase))
                        break;
                if (i <= 0) throw new ArgumentException("No common root path found");
                rootPathSplitted = new ArraySegment<string>(rootPathSplitted.Array, 0, i);
            }
        while (rootPathSplitted.Array != null && rootPathSplitted.Count > 0) {
            var currentDirectory = string.Join(Path.DirectorySeparatorChar, rootPathSplitted.Array, 0, rootPathSplitted.Count);
            if (IsUnityProject(currentDirectory))
                return currentDirectory;
            rootPathSplitted = new ArraySegment<string>(rootPathSplitted.Array, 0, rootPathSplitted.Count - 1);
        }
        throw new ArgumentException("No common root path found");
    }

    // A Unity project folder should contain Assets, Packages, ProjectSettings and ProjectVersion.txt
    private static bool IsUnityProject(string path) =>
        Directory.Exists(Path.Join(path, "Assets")) &&
        Directory.Exists(Path.Join(path, "Packages")) &&
        Directory.Exists(Path.Join(path, "ProjectSettings")) &&
        File.Exists(Path.Join(path, "ProjectSettings", "ProjectVersion.txt"));

    private static bool IsFiltered(string path, Glob[]? filters) {
        if (filters == null || filters.Length == 0) return false;
        foreach (var filter in filters) if (filter.IsMatch(path)) return false;
        return true;
    }
}