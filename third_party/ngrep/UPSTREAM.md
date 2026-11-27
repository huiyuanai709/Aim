# ngrep

Vendored from [huiyuanai709/ngrep](https://github.com/huiyuanai709/ngrep).

| | |
|---|---|
| Version | 0.20.0 |
| Commit | `ca2ccc6ea046520c5de6574d4d343132cfb432ac` |
| Date | 2026-11-21 |
| Tree | `src/Ngrep` copied to this directory |

Copyright (c) huiyuanai709.

The upstream commit did not include a LICENSE file. These sources are vendored
so Aim can run ngrep's search, index, ignore rules, and SIMD scanners in-process.
Aim does not shell out to an `ngrep` binary.

## What Aim changed

`Ngrep.csproj` here is a class library, not upstream's Native AOT executable.

Upstream publishes with the Satori GC SDK and, on x64, three instruction-set
builds (`IlcInstructionSet` baseline, `x86-64-v3`, and `x86-64-v4`). Those
builds fold `Avx2.IsSupported` and `Avx512BW.IsSupported` to constants, so a
higher build will not start on an older CPU.

Aim keeps one binary per RID (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`).
The SIMD methods in this tree already branch on `Sse2.IsSupported`,
`Avx2.IsSupported`, `Avx512BW.IsSupported`, and `Vector128.IsHardwareAccelerated`.
Aim does not set `IlcInstructionSet`, so those checks stay runtime checks.

`Program.cs` is upstream's executable host (thread-pool floor, UTF-8 stdout).
Aim's entry point does not call it. `aim search` calls `Ngrep.Cli.NgrepCli.Run`.
Review `code_search` calls `Ngrep.Search.Searcher` for worktree scans.

The C# under this directory, other than this project file and `UPSTREAM.md`,
matches that upstream commit.

## Updating

1. Replace the `.cs` files in this directory with `src/Ngrep` from the new upstream commit.
2. Keep this `Ngrep.csproj` (library, `net11.0`, unsafe blocks, `InternalsVisibleTo` Aim).
3. Record the commit and version in the table above.
4. Run `dotnet test` and `dotnet publish Aim/Aim.csproj -c Release -r linux-x64 --self-contained`.
