# UnityPackageUtil

This is a simple command line tool to extract / inspect / pack UnityPackage files.

## Usage

```batch
UnityPackageUtil verb [args...] [sources]
```

Where supported verbs (sub commands) and arguments are:
- `pack`, `p`, `merge`: Pack/Merge Unity package
  - `--icon`: Icon file, must be a PNG file
  - `-l`, `--level`: Compression level 0-9
  - `-r`, `--replace`: Replace existing files if conflict
  - `-k`, `--keep`: Keep existing files if conflict
  - `-o`, `--output`: Destination directory or Unity package
  - `-n`, `--dryrun`: Do not write to disk
  - `-f`, `--filter`: Glob pattern to filter files
- `extract`, `e`, `unpack`: Extract Unity package
  - `-r`, `--replace`: Replace existing files if conflict
  - `-k`, `--keep`: Keep existing files if conflict
  - `-o`, `--output`: Destination directory or Unity package
  - `-n`, `--dryrun`: Do not write to disk
  - `-f`, `--filter`: Glob pattern to filter files

Examples:

To extract a unitypackage to project folder:
```batch
> UnityPackageUtil e -o Path\To\Project .\Package1.unitypackage .\Package2.unitypackage
```

To pack a directory within a project to a unitypackage:
```batch
> UnityPackageUtil p -o Output.unitypackage Path\To\Project\Assets\Resources
```

To merge multiple unitypackages into one, but only for .cs files:
```batch
> UnityPackageUtil p -o Output.unitypackage -f **/*.cs -- .\Package1.unitypackage .\Package2.unitypackage
```

To add/change preview icon for a unitypackage, but without creating a copy:
```batch
> UnityPackageUtil p -o .\Fancy.unitypackage --icon Path\To\Icon.png .\Fancy.unitypackage
```

## Licesnse

[MIT](LICENSE)