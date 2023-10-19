using System;
using System.Text;
using System.IO;
using ICSharpCode.SharpZipLib.Tar;

namespace JLChnToZ.UnityPackageUtil {
    public readonly struct AssetEntry {
        public readonly Guid guid;
        public readonly string path;
        public readonly object entry, meta;

        static void RecursiveCreateDirectory(string assetPath) {
            var pathSplitted = assetPath.Split('/', '\\');
            var destPath = pathSplitted[0];
            for (int i = 1, count = pathSplitted.Length - 1; i < count; i++) {
                destPath = Path.Combine(destPath, pathSplitted[i]);
                if (!Directory.Exists(destPath)) Directory.CreateDirectory(destPath);
            }
        }

        public AssetEntry(Guid guid, string path, object entry, object meta) {
            this.guid = guid;
            this.path = path;
            this.entry = entry;
            this.meta = meta;
        }

        public void WriteTo(TarOutputStream destStream) {
            Console.WriteLine($"{path} (GUID: {guid})");
            var guidStr = guid.ToString("N");
            UnityPackagePacker.WriteFile(path, $"{guidStr}/pathname", destStream);
            UnityPackagePacker.WriteFile(entry, $"{guidStr}/asset", destStream);
            UnityPackagePacker.WriteFile(meta, $"{guidStr}/asset.meta", destStream);
        }
        
        public void WriteTo(string destFolder, ref bool? replace) {
            var assetPath = Path.Combine(destFolder, path);
            RecursiveCreateDirectory(assetPath);
            if (File.Exists(assetPath) && !Utils.PromptReplace(ref replace, $"File already exists: {assetPath}")) return;
            if (entry is not Stream assetStream) {
                Console.WriteLine($"(Ignored) {path} (GUID: {guid})");
                return;
            }
            Console.WriteLine($"{path} (GUID: {guid})");
            using var outfs = new FileStream(assetPath, FileMode.OpenOrCreate, FileAccess.Write);
            assetStream.CopyTo(outfs);
            assetStream.Dispose();
            File.WriteAllText($"{assetPath}.meta", meta as string, Encoding.UTF8);
        }
    }
}