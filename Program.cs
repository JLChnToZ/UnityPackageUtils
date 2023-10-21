using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using CommandLine;
using DotNet.Globbing;

[assembly: AssemblyProduct("UnityPackageUtil")]
[assembly: AssemblyTitle("UnityPackageUtil")]
[assembly: AssemblyDescription("A command line tool to pack and extract Unity packages")]
[assembly: AssemblyCompany("Explosive Theorem Lab")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]

namespace JLChnToZ.UnityPackageUtil {
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

        [Option('r', "replace", Required = false, HelpText = "Replace existing files if conflict")]
        public bool ReplaceAll { get; set; }

        [Option('k', "keep", Required = false, HelpText = "Keep existing files if conflict")]
        public bool KeepAll { get; set; }

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

        public bool? CanReplace => DryRun ? false : ReplaceAll ? true : KeepAll ? false : null;
    }

    [Verb("pack", aliases: new[] { "p", "merge" }, HelpText = "Pack or merge Unity package")]
    class PackOptions : Options {
        [Option("icon", Required = false, HelpText = "Icon file, must be a PNG file")]
        public string? Icon { get; set; }

        [Option('l', "level", Required = false, Default = 5, HelpText = "Compression level, 0-9")]
        public int CompressLevel { get; set; }
    }

    [Verb("extract", aliases: new[] { "e", "unpack" }, HelpText = "Extract Unity package")]
    class ExtractOptions : Options { }

    public static class Program {
        public static void Main(string[] args) => Parser.Default
            .ParseArguments<PackOptions, ExtractOptions>(args)
            .MapResult(
                (Func<PackOptions, int>)UnityPackagePacker.Pack,
                (Func<ExtractOptions, int>)UnityPackageUnpacker.Extract,
                NotParsed
            );

        private static int NotParsed(IEnumerable<Error> _) => -1;
    }
}