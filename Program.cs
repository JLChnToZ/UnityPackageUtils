using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using CommandLine;

[assembly: AssemblyProduct("UnityPackageUtil")]
[assembly: AssemblyTitle("UnityPackageUtil")]
[assembly: AssemblyDescription("A command line tool to pack and unpack Unity packages")]
[assembly: AssemblyCompany("Explosive Theorem Lab")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

public class Program {
    class Options {
        [Value(0, MetaName = "source", Required = false, HelpText = "Source directory or Unity package")]
        public IEnumerable<string> Sources {
            get => sources;
            set => sources = value?.ToArray() ?? Array.Empty<string>();
        }

        [Option('o', "output", Required = false, HelpText = "Destination directory or Unity package")]
        public string Dest { get; set; }

        [Option('n', "dryrun", Required = false, HelpText = "Do not write to disk")]
        public bool DryRun { get; set; }

        public string[] sources = Array.Empty<string>();
    }

    [Verb("pack", HelpText = "Pack Unity package")]
    class PackOptions : Options {}

    [Verb("unpack", HelpText = "Unpack Unity package")]
    class UnpackOptions : Options {}

    public static void Main(string[] args) {
        Parser.Default.ParseArguments<PackOptions, UnpackOptions>(args).MapResult(
            (PackOptions opts) => { PackUnityPackage(opts); return 0; },
            (UnpackOptions opts) => { ExtractUnityPackage(opts); return 0; },
            errs => 1
        );
    }

    private static void ExtractUnityPackage(UnpackOptions options) {
        var destPath = options.Dest;
        foreach (var srcPath in options.sources) {
            if (string.IsNullOrEmpty(destPath))
                destPath = Path.GetDirectoryName(srcPath)!;
            using var srcStream = File.OpenRead(srcPath);
            ExtractUnityPackage(srcStream, options.DryRun ? null : destPath);
        }
    }

    private static void PackUnityPackage(PackOptions options) {
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
        PackUnityPackage(srcPath, destStream);
    }

    private static void ExtractUnityPackage(Stream stream, string destFolder) {
        using var gzStream = new GZipInputStream(stream);
        using var tarStream = new TarInputStream(gzStream, Encoding.UTF8);
        var fileMap = new Dictionary<Guid, (Stream assetStream, string meta, string pathName)>();
        for (TarEntry entry; (entry = tarStream.GetNextEntry()) != null;) {
            var pathSplitted = entry.Name.Split('/', '\\');
            int offset = 0;
            for (int i = 0; i < pathSplitted.Length; i++) {
                if (string.IsNullOrEmpty(pathSplitted[i]) || pathSplitted[i] == ".")
                    offset = i + 1;
                else
                    break;
            }
            if (pathSplitted.Length - offset != 2 ||
                !Guid.TryParseExact(pathSplitted[pathSplitted.Length - 2], "N", out var guid)) {
                Console.WriteLine($"Invalid file entry: {entry.Name}");
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
                    data.pathName = streamReader.ReadToEnd();
                    break;
                }
                default:
                    continue;
            }
            if (data.assetStream != null && data.pathName != null && data.meta != null) {
                fileMap.Remove(guid);
                Console.WriteLine($"Extracting {data.pathName} (GUID: {guid})");
                if (string.IsNullOrEmpty(destFolder)) continue;
                var assetPath = Path.Combine(destFolder, data.pathName);
                RecursiveCreateDirectory(assetPath);
                using var outfs = File.OpenWrite(assetPath);
                data.assetStream.CopyTo(outfs);
                data.assetStream.Dispose();
                File.WriteAllText($"{assetPath}.meta", data.meta, Encoding.UTF8);
            } else
                fileMap[guid] = data;
        }
    }

    private static void PackUnityPackage(string[] srcPaths, Stream dest) {
        var srcDirectoryPath = FindUnityProjectRootPath(srcPaths);
        using var gzStream = new GZipOutputStream(dest);
        using var tarStream = new TarOutputStream(gzStream, Encoding.UTF8);
        var dirInfoStack = new Stack<FileSystemInfo>();
        var processedFiles = new HashSet<string>();
        if (srcPaths != null && srcPaths.Length > 0)
            foreach (var path in srcPaths) {
                if (File.Exists(path))
                    dirInfoStack.Push(new FileInfo(path));
                else if (Directory.Exists(path))
                    dirInfoStack.Push(new DirectoryInfo(path));
            }
        else
            dirInfoStack.Push(new DirectoryInfo(Directory.GetCurrentDirectory()));
        while (dirInfoStack.Count > 0) {
            var info = dirInfoStack.Pop();
            if (info is DirectoryInfo dirInfo)
                foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                    ProcessSingleEntry(tarStream, entry, srcDirectoryPath, dirInfoStack, processedFiles);
            else if (info is FileInfo fileInfo) {
                if (string.Equals(fileInfo.Extension, ".meta", StringComparison.OrdinalIgnoreCase)) {
                    var nonMetaFile = Path.GetFileNameWithoutExtension(fileInfo.FullName);
                    fileInfo = new FileInfo(nonMetaFile);
                    if (!fileInfo.Exists) {
                        dirInfo = new DirectoryInfo(nonMetaFile);
                        if (dirInfo.Exists)
                            ProcessSingleEntry(tarStream, dirInfo, srcDirectoryPath, dirInfoStack, processedFiles);
                        continue;
                    }
                }
                ProcessSingleEntry(tarStream, fileInfo, srcDirectoryPath, dirInfoStack, processedFiles);
            }
        }
    }

    private static void ProcessSingleEntry(TarOutputStream tarStream, FileSystemInfo entry, string srcDirectoryPath, Stack<FileSystemInfo> dirInfostack, HashSet<string> processedFiles) {
        if (string.Equals(entry.Extension, ".meta", StringComparison.OrdinalIgnoreCase) &&
            entry.Attributes.HasFlag(FileAttributes.Normal)) return;
        if (!processedFiles.Add(entry.FullName)) return;
        var metaFile = new FileInfo($"{entry.FullName}.meta");
        if (!metaFile.Exists) {
            if (entry is DirectoryInfo subDir)
                dirInfostack.Push(subDir);
            return;
        }
        var contents = new StreamReader(metaFile.OpenRead(), Encoding.UTF8);
        string? line;
        while ((line = contents.ReadLine()) != null) {
            if (!line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) continue;
            contents.Dispose();
            var guidStr = line.Substring(5).Trim();
            Console.WriteLine($"Packing {Path.GetRelativePath(srcDirectoryPath, entry.FullName)} (GUID: {Guid.ParseExact(guidStr, "N")})");
            WritePathName(tarStream, srcDirectoryPath, entry, guidStr);
            if (entry is DirectoryInfo subDir)
                dirInfostack.Push(subDir);
            else if (entry is FileInfo file) {
                WriteFile(tarStream, $"{guidStr}/asset", file);
                WriteFile(tarStream, $"{guidStr}/asset.meta", metaFile);
            }
            break;
        }
        contents.Dispose();
    }

    private static void RecursiveCreateDirectory(string assetPath) {
        var pathSplitted = assetPath.Split('/', '\\');
        var destPath = pathSplitted[0];
        for (int i = 1, count = pathSplitted.Length - 1; i < count; i++) {
            destPath = Path.Combine(destPath, pathSplitted[i]);
            if (!Directory.Exists(destPath)) Directory.CreateDirectory(destPath);
        }
    }

    private static void WritePathName(TarOutputStream stream, string srcDirectoryPath, FileSystemInfo info, string guid) {
        var pathName = info.FullName.Substring(srcDirectoryPath.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
        var pathNameBytes = Encoding.UTF8.GetBytes(pathName);
        var entry = TarEntry.CreateTarEntry($"{guid}/pathname");
        entry.Size = pathNameBytes.Length;
        stream.PutNextEntry(entry);
        stream.Write(pathNameBytes, 0, pathNameBytes.Length);
        stream.CloseEntry();
    }

    private static void WriteFile(TarOutputStream stream, string destPath, FileInfo srcFile) {
        var entry = TarEntry.CreateTarEntry(destPath);
        entry.Size = srcFile.Length;
        stream.PutNextEntry(entry);
        using var fs = srcFile.OpenRead();
        fs.CopyTo(stream);
        stream.CloseEntry();
    }

    private static string FindUnityProjectRootPath(string[] filePaths) {
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
}