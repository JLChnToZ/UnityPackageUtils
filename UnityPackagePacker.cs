using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using DotNet.Globbing;

namespace JLChnToZ.UnityPackageUtil {
    class UnityPackagePacker : IDisposable {
        readonly Dictionary<Guid, AssetEntry> assetEntries = new();
        readonly Dictionary<string, Guid> pathGuidMap = new(StringComparer.OrdinalIgnoreCase);
        readonly Stack<(FileSystemInfo entry, bool isUnityPackage)> pendingStack = new();
        readonly HashSet<string> processedFiles = new();
        readonly string srcDirectoryPath;
        readonly Glob[]? filters;
        bool? replace;
        TarOutputStream? tarStream;
        GZipOutputStream? gzipStream;
        int compressionLevel = 5;

        public int CompressionLevel {
            get {
                if (gzipStream != null) compressionLevel = gzipStream.GetLevel();
                return compressionLevel;
            }
            set {
                if (value < 0 || value > 9) throw new ArgumentOutOfRangeException(nameof(value));
                compressionLevel = value;
                if (gzipStream != null) gzipStream.SetLevel(value);
            }
        }

        public static int Pack(PackOptions options) {
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
            UnityPackagePacker? packer = null;
            Stream? destStream = null;
            try {
                packer = new UnityPackagePacker(srcPath, options.GlobFilters, options.CanReplace) {
                    CompressionLevel = options.CompressLevel,
                };
                destStream = options.DryRun ? Stream.Null : File.OpenWrite(destPath);
                packer.Pack(destStream, options.Icon);
            } finally {
                packer?.Dispose();
                destStream?.Dispose();
            }
            return 0;
        }

        static bool IsUnityProject(string path) =>
            Directory.Exists(Path.Join(path, "Assets")) &&
            Directory.Exists(Path.Join(path, "Packages")) &&
            Directory.Exists(Path.Join(path, "ProjectSettings")) &&
            File.Exists(Path.Join(path, "ProjectSettings", "ProjectVersion.txt"));
        
        static string FindUnityProjectRootPath(string[]? filePaths) {
            ArraySegment<string> rootPathSplitted = default;
            var cwd = Directory.GetCurrentDirectory();
            if (filePaths == null || filePaths.Length == 0)
                rootPathSplitted = cwd.Split(Path.DirectorySeparatorChar);
            else
                foreach (var path in filePaths) {
                    var dirPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (dirPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists($"{dirPath}.meta")) continue;
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
            if (rootPathSplitted.Array == null) return cwd;
            while (rootPathSplitted.Array != null && rootPathSplitted.Count > 0) {
                var currentDirectory = string.Join(Path.DirectorySeparatorChar, rootPathSplitted.Array, 0, rootPathSplitted.Count);
                if (IsUnityProject(currentDirectory))
                    return currentDirectory;
                rootPathSplitted = new ArraySegment<string>(rootPathSplitted.Array, 0, rootPathSplitted.Count - 1);
            }
            throw new ArgumentException("No common root path found");
        }

        public static void WriteFile(object src, string destPath, TarOutputStream destStream) {
            Stream stream;
            if (src is Stream s)
                stream = s;
            else if (src is FileInfo file)
                stream = file.OpenRead();
            else if (src is string str) {
                var sr = new StreamWriter(new MemoryStream(), Utils.UTF8NoBOMEncoding, leaveOpen: true);
                sr.Write(str);
                sr.Flush();
                stream = sr.BaseStream;
                stream.Seek(0, SeekOrigin.Begin);
            } else
                throw new ArgumentException("Invalid argument type.", nameof(src));
            using (stream) WriteFile(stream, destPath, destStream);
        }

        static void WriteFile(Stream srcStream, string destPath, TarOutputStream? destStream) {
            if (destStream == null) throw new InvalidOperationException("Not initialized.");
            var entry = TarEntry.CreateTarEntry(destPath);
            entry.Size = srcStream.Length;
            destStream.PutNextEntry(entry);
            srcStream.CopyTo(destStream);
            destStream.CloseEntry();
        }

        public UnityPackagePacker(string[]? srcPaths, Glob[]? filters, bool? replace) {
            this.filters = filters;
            this.replace = replace;
            srcDirectoryPath = FindUnityProjectRootPath(srcPaths);
            if (srcPaths != null && srcPaths.Length > 0)
                foreach (var path in srcPaths) {
                    if (File.Exists(path))
                        pendingStack.Push((new FileInfo(path), false));
                    else if (Directory.Exists(path))
                        pendingStack.Push((new DirectoryInfo(path), false));
                }
            else
                pendingStack.Push((new DirectoryInfo(Directory.GetCurrentDirectory()), false));
            while (pendingStack.Count > 0) {
                var (info, isUnityPackage) = pendingStack.Pop();
                if (info is FileInfo fileInfo) {
                    if (!isUnityPackage) {
                        VaildateFileEntry(info);
                        continue;
                    }
                    using var fs = fileInfo.OpenRead();
                    foreach (var entry in UnityPackageUnpacker.EnumerateUnityPackage(fs)) {
                        if (!ValidateEntry(entry.path, entry.guid)) continue;
                        assetEntries[entry.guid] = entry;
                    }
                    continue;
                }
                if (info is DirectoryInfo dirInfo) {
                    foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                        VaildateFileEntry(entry);
                    continue;
                }
            }
        }

        public void Pack(Stream dest, string? iconPath) {
            if (gzipStream != null || tarStream != null) Dispose();
            gzipStream = new GZipOutputStream(dest);
            gzipStream.SetLevel(compressionLevel);
            tarStream = new TarOutputStream(gzipStream, Utils.UTF8NoBOMEncoding);
            foreach (var entry in assetEntries.Values) entry.WriteTo(tarStream);
            CheckAndWritePNGFile(iconPath, ".icon.png");
            tarStream.Flush();
            gzipStream.Flush();
            if (dest.Position < dest.Length) dest.SetLength(dest.Position); // Truncate
        }

        void VaildateFileEntry(FileSystemInfo entry) {
            if (string.Equals(entry.Extension, ".meta", StringComparison.OrdinalIgnoreCase) &&
                entry.Attributes.HasFlag(FileAttributes.Normal)) {
                var nonmeta = Path.GetFileNameWithoutExtension(entry.FullName);
                if (processedFiles.Contains(nonmeta)) return;
                if (Directory.Exists(nonmeta))
                    pendingStack.Push((new DirectoryInfo(nonmeta), false));
                else if (File.Exists(nonmeta))
                    pendingStack.Push((new FileInfo(nonmeta), false));
                return;
            }
            if (!processedFiles.Add(entry.FullName)) return;
            if (entry is DirectoryInfo) {
                pendingStack.Push((entry, false));
                return;
            }
            var relPath = Path.GetRelativePath(srcDirectoryPath, entry.FullName).Replace('\\', '/');
            if (!Utils.TryFindGuidFromFile(entry.FullName, out var meta, out var guid)) {
                if (string.Equals(entry.Extension, ".unitypackage", StringComparison.OrdinalIgnoreCase)) {
                    pendingStack.Push((entry, true));
                    return;
                }
                Console.WriteLine($"(Ignored) {relPath} (GUID not found or invalid)");
                return;
            }
            if (!ValidateEntry(relPath, guid)) return;
            assetEntries[guid] = new(guid, relPath, entry, meta);
        }

        bool ValidateEntry(string relPath, Guid guid) {
            if (Utils.IsFiltered(relPath, filters)) {
                Console.WriteLine($"(Skipped) {relPath} (GUID: {guid})");
                return false;
            }
            if (assetEntries.TryGetValue(guid, out var fileEntry) &&
                !Utils.PromptReplace(ref replace, $"File with same GUID already exists: {guid}\nExisting: {fileEntry.path}\nNew: {relPath}")) {
                Console.WriteLine($"(Ignored) {relPath} (Duplicate GUID: {guid})");
                return false;
            }
            if (pathGuidMap.TryGetValue(relPath, out var otherGuid) && otherGuid != guid) {
                if (!Utils.PromptReplace(ref replace, $"File with same path already exists: {relPath} (GUID: {otherGuid})"))
                    return false;
                Console.WriteLine($"(Replaced) {relPath} (GUID: {otherGuid} -> {guid})");
                assetEntries.Remove(otherGuid);
            }
            pathGuidMap[relPath] = guid;
            return true;
        }

        void CheckAndWritePNGFile(string? srcPath, string destPath) {
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
            WriteFile(fs, destPath, tarStream);
        }

        public void Dispose() {
            if (tarStream != null) {
                tarStream.Dispose();
                tarStream = null;
            }
            if (gzipStream != null) {
                gzipStream.Dispose();
                gzipStream = null;
            }
            processedFiles.Clear();
            pathGuidMap.Clear();
        }
    }
}