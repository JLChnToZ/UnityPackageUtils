# UnityPackageUtil

This is a simple command line tool to extract / inspect / pack UnityPackage files.

## Usage

```batch
UnityPackageUtil verb [args...] [sources]
```

Where supported verbs (sub commands) and arguments are:
- `pack`, `p`: Pack Unity package
  - `--icon`: Icon file, must be a PNG file
  - `-o`, `--output`: Destination directory or Unity package
  - `-n`, `--dryrun`: Do not write to disk
  - `-f`, `--filter`: Glob pattern to filter files
- `extract`, `e`, `unpack`: Extract Unity package
  - `-r`, `--replace`: Replace existing files if conflict
  - `-k`, `--keep`: Keep existing files if conflict
  - `-o`, `--output`: Destination directory or Unity package
  - `-n`, `--dryrun`: Do not write to disk
  - `-f`, `--filter`: Glob pattern to filter files

## Licesnse

[MIT](LICENSE)