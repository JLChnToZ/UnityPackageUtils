using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using DotNet.Globbing;

namespace JLChnToZ.UnityPackageUtil {
    static class UnityPackageUnpacker {
        public static int Extract(ExtractOptions options) {
            var destPath = options.Dest;
            foreach (var srcPath in options.sources) {
                if (string.IsNullOrEmpty(destPath))
                    destPath = Path.GetDirectoryName(srcPath)!;
                using var srcStream = File.OpenRead(srcPath);
                Extract(srcStream, options.DryRun ? null : destPath, options.GlobFilters, options.CanReplace);
            }
            return 0;
        }

        public static void Extract(Stream stream, string? destFolder, Glob[]? filters, bool? replace) {
            foreach (var entry in EnumerateUnityPackage(stream)) {
                if (Utils.IsFiltered(entry.path, filters)) {
                    Console.WriteLine($"(Skipped) {entry.path} (GUID: {entry.guid})");
                    continue;
                }
                if (string.IsNullOrEmpty(destFolder)) {
                    Console.WriteLine($"{entry.path} (GUID: {entry.guid})");
                    continue;
                }
                entry.WriteTo(destFolder, ref replace);
            }
        }

        public static IEnumerable<AssetEntry> EnumerateUnityPackage(Stream stream) {
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
                    yield return new(guid, data.pathName, data.assetStream, data.meta);
                } else
                    fileMap[guid] = data;
            }
        }
    }
}