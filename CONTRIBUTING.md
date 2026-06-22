# Contributing to Chisel

Thanks for your interest! This guide covers the dev environment, how the test fixtures work (the one
non-obvious bit), the rules of the road, and the PR workflow.

For what chisel does and how to use it, see [README.md](README.md). For the deep technical reference
— how the walk works, every output format, source generators, embedding the `Core` library — see
[docs/GUIDE.md](docs/GUIDE.md).

---

## Before you begin

- For bug reports and feature requests, please [open an issue](../../issues) first.
- For non-trivial changes, sketch the approach in an issue before writing code.
- Small fixes (typos, obvious bugs) can go straight to a pull request.

---

## Quick start

**Prerequisites:**

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned via `global.json` to `10.0.300`).
- [PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`) — for the build scripts under `build/`.
- Git.

```bash
git clone https://github.com/JanusMael/chisel.git
cd chisel
dotnet build

# Prepare the test fixtures (see below), then run the suite:
pwsh build/restore-test-fixtures.ps1
dotnet test

# Run the CLI against any solution:
dotnet run --project src/Chisel.Cli -- --type MyNS.IFoo --solution path/to/App.sln --output ./out
```

---

## Test fixtures (read this before `dotnet test`)

The xUnit suite loads the example solutions under `tests/Fixtures/` via MSBuildWorkspace at run time.
**They are not part of `Chisel.slnx`**, so a plain `dotnet restore` of the repo never touches them,
and MSBuildWorkspace does not restore on load. On a fresh clone you must prepare them first:

```bash
pwsh build/restore-test-fixtures.ps1
```

This restores every fixture solution and builds the `SourceGen` fixture (its generator assembly must
exist before the `SourceGenTests` can discover generated symbols). Skip it and the package / build /
source-generator tests fail with workspace-load or "no generated symbol" errors. CI runs this same
script before testing.

---

## Project layout

| Project | Description |
|---------|-------------|
| `src/Chisel.Core` | The library — all slicing logic (workspace load, resolution, the walk, collection, emission). No CLI dependencies. |
| `src/Chisel.Cli` | Thin console host (`chisel`): arg parsing, Serilog wiring, the run manifest, exit codes. |
| `tests/Chisel.Core.Tests` | xUnit tests (unit + end-to-end fixture slices). |
| `tests/Fixtures` | Worked-example solutions — one per behaviour — that double as the integration-test inputs. |

The CLI is intentionally thin: anything reusable belongs in `Chisel.Core` (it ships as a library too
— see the embedding section in [docs/GUIDE.md](docs/GUIDE.md)).

---

## Rules of the road

### Best-effort diagnostics, not exceptions

chisel's contract is to produce a mostly-complete slice rather than abort. A problem with one item (a
file, symbol, or reference) is recorded as a non-fatal `SliceDiagnostic` and the run continues —
**only four conditions are fatal** (no SDK → exit `7`, missing solution → `5`, unloadable workspace →
`4`, unresolvable/ambiguous seed → `3`). When you add a per-item operation, wrap it with
`DiagnosticSink.Guard` / `GuardAsync` (which turns exceptions into `Error` diagnostics and continues —
except `OperationCanceledException`, which always propagates). Don't add new throw-and-abort paths.

### MSBuild registration ordering

`MsBuildBootstrapper.EnsureRegistered()` / `TryEnsureRegistered()` must run before any code that loads
`Microsoft.Build.*` / `MSBuildWorkspace` types. The `Microsoft.Build.*` runtime assemblies are
deliberately *not* shipped (`ExcludeAssets=runtime`); the locator binds them from the installed SDK.
Keep `Program.cs` free of MSBuild type references — the CLR resolves them at JIT time, so a reference
there would load MSBuild before registration. See the comment in `Program.cs`.

### Versioned outputs

`result.json` (the run manifest) is a stable, documented contract. Any change to its shape must bump
`RunManifest.CurrentSchemaVersion`.

### Coding style

- Match the surrounding style — 4-space indentation, file-scoped namespaces, `latest` C#.
- `TreatWarningsAsErrors` is on everywhere: the build must be **zero warnings**.
- New public API needs XML doc comments.
- Handle errors explicitly — no bare `catch { }`. Filter (`when (ex is IOException or …)`) and log or
  convert to a diagnostic.
- Prefer many small files over few large ones.

### Testing — add a fixture for new behaviour

The fixtures under `tests/Fixtures/` are the established way to test the walk end-to-end. When you add
or change a collection behaviour:

1. Add a minimal fixture solution under `tests/Fixtures/<Name>/` that exercises it.
2. Add a test (see `SliceRunnerFixtureTests.cs`) asserting **both** the collected file set **and** that
   the generated `Slice.csproj` builds — the build-the-slice tests run `dotnet build … -warnaserror`.

Unit-level logic (arg parsing, manifest shape, path comparison, diagnostics) has direct tests too —
follow the nearest existing pattern.

---

## Building release artifacts (optional)

The same scripts CI uses, runnable locally:

```bash
# Self-contained per-RID binaries → ./dist
pwsh build/publish.ps1 -Rids win-x64,win-arm64 -Version 2026.2.624

# The .NET tool package
dotnet pack src/Chisel.Cli -c Release -o ./nupkg
```

Releases are cut by pushing a CalVer tag (e.g. `2026.2.624`, `YEAR.QUARTER.MMDD`); the tag becomes the
package version. See [.github/workflows/release.yml](.github/workflows/release.yml).

---

## Commit & PR conventions

Commit messages: `type(scope): short imperative summary`, with the WHY in the body for non-trivial
changes (the diff already shows the what). Types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`,
`perf`. Prefer one concern per commit.

### Pre-PR checklist

- [ ] `dotnet build` passes with **zero warnings** (`TreatWarningsAsErrors` is on).
- [ ] `pwsh build/restore-test-fixtures.ps1` then `dotnet test` — all green, no new skips.
- [ ] New behaviour has a fixture **and** a test (asserts the collected set *and* that the slice builds).
- [ ] New public API has XML docs; no bare `catch { }`; per-item failures go through `DiagnosticSink.Guard`.
- [ ] Changes to `result.json` / the run manifest bump `RunManifest.CurrentSchemaVersion`.
- [ ] Commit messages explain the WHY; no secrets or credentials in the diff.

---

## Reporting issues

Open an issue with the platform, the chisel version (`dotnet chisel --version`), the command you ran,
and what happened vs. what you expected. The `--verbose` output and the `chisel.log` written into the
run's `--output` directory are helpful attachments.

---

## Code of conduct

Be respectful. Disagree without being personal, and assume good faith from reviewers and contributors
alike. We're all volunteers — nobody owes anyone their time.

Thanks for contributing!
