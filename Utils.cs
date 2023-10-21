using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using DotNet.Globbing;

namespace JLChnToZ.UnityPackageUtil {
    public static class Utils {
        public static bool IsFiltered(string path, Glob[]? filters) {
            if (filters == null || filters.Length == 0) return false;
            foreach (var filter in filters) if (filter.IsMatch(path)) return false;
            return true;
        }

        public static bool TryFindGuidFromFile(string file, [NotNullWhen(true)] out FileInfo? meta, out Guid guid) {
            meta = new FileInfo($"{file}.meta");
            if (meta.Exists) {
                using var contents = new StreamReader(meta.OpenRead(), Encoding.UTF8);
                string? line;
                while ((line = contents.ReadLine()) != null)
                    if (line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                        return Guid.TryParseExact(line.AsSpan(5), "N", out guid);
            }
            meta = null;
            guid = default;
            return false;
        }

        public static bool PromptReplace(ref bool? replace, string prompt) {
            if (replace.HasValue) return replace.Value;
            Console.WriteLine($"{prompt}, replace?");
            Console.Write("(Y = Yes, N = No, Shift+Y = Yes to All, Shift+N = No to All) ");
            while (true) {
                var key = Console.ReadKey(true);
                switch (key.Key) {
                    case ConsoleKey.Y:
                        Console.WriteLine("Y");
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) replace = true;
                        return true;
                    case ConsoleKey.N:
                        Console.WriteLine("N");
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) replace = false;
                        return false;
                }
            }
        }
    }
}