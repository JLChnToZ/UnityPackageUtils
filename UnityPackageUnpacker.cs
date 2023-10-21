using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using DotNet.Globbing;

namespace JLChnToZ.UnityPackageUtil {
    class UnityPackageUnpacker {
        readonly Dictionary<Guid, string> existingGuids = new();
        readonly string? destFolder;
        readonly Glob[]? filters;
        bool? replace;

        public static int Extract(ExtractOptions options) {
            var destPath = options.Dest;
            UnityPackageUnpacker? unpacker = null;
            foreach (var srcPath in options.sources) {
                if (string.IsNullOrEmpty(destPath))
                    destPath = Path.GetDirectoryName(srcPath)!;
                unpacker ??= new UnityPackageUnpacker(destPath, options.GlobFilters, options.CanReplace);
                using var srcStream = File.OpenRead(srcPath);
                unpacker.Extract(srcStream);
            }
            return 0;
        }

        public static IEnumerable<AssetEntry> EnumerateUnityPackage(Stream stream) {
            using var gzStream = new GZipInputStream(stream);
            using var tarStream = new TarInputStream(gzStream, Utils.UTF8NoBOMEncoding);
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
                            ms.Seek(0, SeekOrigin.Begin);
                            data.assetStream = ms;
                            break;
                        }
                    case "asset.meta": {
                            using var ms = new MemoryStream();
                            tarStream.CopyEntryContents(ms);
                            ms.Seek(0, SeekOrigin.Begin);
                            using var streamReader = new StreamReader(ms, Utils.UTF8NoBOMEncoding);
                            data.meta = streamReader.ReadToEnd();
                            break;
                        }
                    case "pathname": {
                            using var ms = new MemoryStream();
                            tarStream.CopyEntryContents(ms);
                            ms.Seek(0, SeekOrigin.Begin);
                            using var streamReader = new StreamReader(ms, Utils.UTF8NoBOMEncoding);
                            data.pathName = streamReader.ReadLine();
                            break;
                        }
                    default: continue;
                }
                if (data.assetStream != null && data.pathName != null && data.meta != null) {
                    fileMap.Remove(guid);
                    yield return new(guid, data.pathName, data.assetStream, data.meta);
                } else
                    fileMap[guid] = data;
            }
        }

        public UnityPackageUnpacker(string? destFolder, Glob[]? filters, bool? replace) {
            this.destFolder = destFolder;
            this.filters = filters;
            this.replace = replace;
            GatherDuplicateGuids();
        }

        public void Extract(Stream stream) {
            foreach (var entry in EnumerateUnityPackage(stream)) {
                if (Utils.IsFiltered(entry.path, filters)) {
                    Console.WriteLine($"(Skipped) {entry.path} (GUID: {entry.guid})");
                    continue;
                }
                if (string.IsNullOrEmpty(destFolder)) {
                    Console.WriteLine($"{entry.path} (GUID: {entry.guid})");
                    continue;
                }
                if (existingGuids.TryGetValue(entry.guid, out var path) &&
                    !Utils.PromptReplace(ref replace, $"File with same GUID already exists: {entry.guid}\nExisting: {path}\nNew: {entry.path}")) {
                    Console.WriteLine($"(Ignored) {entry.path} (Duplicate GUID: {entry.guid})");
                    continue;
                }
                existingGuids[entry.guid] = entry.path;
                entry.WriteTo(destFolder, ref replace);
            }
        }

        void GatherDuplicateGuids() {
            if (!string.IsNullOrEmpty(destFolder) && Directory.Exists(destFolder)) {
                foreach (var assetPath in Directory.EnumerateFiles(destFolder, "*.meta", SearchOption.AllDirectories)) {
                    if (!Utils.TryFindGuidFromFile(assetPath[..^5], out _, out var guid)) continue;
                    if (existingGuids.TryGetValue(guid, out var path))
                        Console.WriteLine($"(Warning) Duplicate GUID found on destination: {guid}\nExisting: {path}\nNew: {Path.GetRelativePath(destFolder, assetPath)}");
                    else
                        existingGuids.Add(guid, Path.GetRelativePath(destFolder, assetPath));
                }
            }
        }
    }
}