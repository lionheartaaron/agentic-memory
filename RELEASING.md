# Releasing

Agentic Memory uses a two-branch Gitflow: `develop` is where work lands day to day, `main` is
always the latest released state, and a release is nothing more than a tag pushed on `main`.
This document is the checklist for cutting one.

## Branches

| Branch | Purpose |
|---|---|
| `develop` | Default branch. All feature/fix branches merge here via PR. Always green (CI runs the full suite on all three platforms on every push, see [`.github/workflows/ci.yml`](.github/workflows/ci.yml)), but not necessarily release-ready. |
| `main` | Always matches the most recent release. Nothing is pushed here except a merge from `develop` (or a hotfix branch) immediately followed by a tag. |
| `feature/*`, `fix/*` | Cut from `develop`, merged back into `develop` via PR. Short-lived. |
| `hotfix/*` | Cut from `main` for an urgent fix that can't wait for `develop` to be release-ready. See [Hotfixes](#hotfixes). |

```
feature/x ──┐
            ├──► develop ──► (release PR) ──► main ──► tag vX.Y.Z ──► GitHub Release
fix/y ──────┘                                  ▲
                                    hotfix/z ───┘ (then back-merged into develop)
```

## Two versions, and only one of them is the tag

Get these straight before anything else. They move independently, and mixing them up writes a wrong
answer permanently into a user's data.

| | Where it lives | When it moves | Who cares |
|---|---|---|---|
| **Application version** | `<Version>` in [`agentic-memory.csproj`](agentic-memory/agentic-memory.csproj), read at runtime by [`AppVersion`](agentic-memory/Configuration/AppVersion.cs) | Every release | The tag, the MCP `ServerInfo`, the console banner, and `CreatedByAppVersion` / `LastOpenedByAppVersion` in every database this build opens |
| **Database schema version** | `DatabaseSchema.Current` in [`DatabaseSchema.cs`](agentic-memory/Persistence/Migrations/DatabaseSchema.cs), derived from the last step in `Steps` | Only when the stored data changes shape | Whether an existing database is migrated on open, and whether an older build will refuse to open a newer one |

A release does not need a schema bump. A schema bump does not need its own release. Adding a
migration step moves the schema version by itself, so there is no number to edit.

**Before tagging, check whether this release adds a migration step.** If it does:

- Confirm the step's `Version` is exactly one above the previous last step and that no shipped
  step was edited. A step that has been released is frozen. Someone's database has already been
  through it, and changing it now means two databases claiming the same schema version with
  different contents in them.
- Say so in the release notes. Users on the previous build **cannot open a database this build
  has touched**, because the migrator refuses a schema newer than it understands and exits
  non-zero by design. That is a one-way door for anyone who might want to roll back, so it
  should be stated rather than discovered.
- Migrations are forward-only. The pre-migration snapshot the migrator writes into the backup
  directory is the entire recovery story.

## Cutting a normal release

1. **Confirm `develop` is ready.** Every push to `develop` runs the full suite on Windows, Linux
   and macOS plus a dashboard build ([`ci.yml`](.github/workflows/ci.yml)); make sure the latest
   run is green on all four jobs.

2. **Merge `develop` into `main`.** Open a PR `develop` → `main` (preferred, since it gives you a
   final review and a green CI check on the merge itself) or merge locally:
   ```bash
   git checkout main
   git pull origin main
   git merge --ff-only develop   # fails loudly if main has diverged; see Hotfixes if it does
   git push origin main
   ```

3. **Bump the version.** Update `<Version>` in
   [`agentic-memory.csproj`](agentic-memory/agentic-memory.csproj) to match the release you're
   about to tag, and commit it on `main` (or include it in the release PR from step 2).

   This one is **enforced, not advisory**: `release.yml` compares the csproj against the tag and
   fails the release if they disagree. Unlike a version printed in a banner, this one gets
   written into user data. The migrator stamps `CreatedByAppVersion` and appends to the
   database's migration history on every open, and a wrong value there is a permanent wrong
   answer in a file nobody can go back and correct.

4. **Tag `main` and push the tag:**
   ```bash
   git checkout main
   git pull origin main
   git tag -a v1.3.0 -m "v1.3.0"
   git push origin v1.3.0
   ```
   Pushing the tag triggers [`release.yml`](.github/workflows/release.yml), which builds the
   server for all six platform targets and publishes them as a GitHub Release.

5. **Merge `main` back into `develop`** so the version bump (and anything else that landed
   directly on `main`) isn't lost:
   ```bash
   git checkout develop
   git merge main
   git push origin develop
   ```

### Why the tag has to be on `main`

Git tags aren't tied to a branch. Pushing `v1.3.0` from `develop` or a stray feature branch would
trigger `release.yml` just as well as tagging `main`. Rather than rely on everyone remembering the
convention, `release.yml` runs a `verify-tag` job first that checks the tagged commit is actually
an ancestor of `origin/main` (and that the csproj version matches). Every other job `needs` it and
won't start if it fails. Tag the wrong branch and the workflow fails fast with a clear error
instead of quietly shipping a release built from unreviewed code.

## What a release produces

Six self-contained folders, archived. Self-contained means the .NET runtime is inside the
archive, so a host machine needs nothing installed. That matters when this ships as a sidecar
next to an Electron app.

| Asset | Platform |
|---|---|
| `agentic-memory-X.Y.Z-win-x64.zip` | Windows, Intel/AMD |
| `agentic-memory-X.Y.Z-win-arm64.zip` | Windows on ARM |
| `agentic-memory-X.Y.Z-linux-x64.tar.gz` | Linux, Intel/AMD |
| `agentic-memory-X.Y.Z-linux-arm64.tar.gz` | Linux on ARM (Raspberry Pi 5, Ampere, AWS Graviton) |
| `agentic-memory-X.Y.Z-osx-arm64.tar.gz` | macOS, Apple Silicon |
| `agentic-memory-X.Y.Z-osx-x64.tar.gz` | macOS, Intel |
| `SHA256SUMS.txt` | Checksums for all six |

Each archive unpacks into a folder of its own name containing `agentic-memory` (or
`agentic-memory.exe`), `appsettings.json`, `wwwroot/` and the runtime. Roughly 190 MB
uncompressed, most of it ONNX Runtime and V8.

**Model weights are not in the archives.** The embedding model (~90 MB) downloads from Hugging
Face on first run; the TypeScript compiler downloads on first workspace registration; the
generative model (~5 GB) downloads only if `Generation.Enabled` is turned on. An install that
has to work offline needs its models directory seeded in advance. See
[Where your data lives](README.md#where-your-data-lives).

`SHA256SUMS.txt` matters more here than it would for a normal app. A host process that downloads
this sidecar at install time should verify it, because whatever it unpacks is going to be
executed.

### Platform notes

- **macOS builds are ad-hoc signed, not notarized.** Apple Silicon refuses to run *any* unsigned
  Mach-O code, so the workflow signs the launcher and every native library with identity `-`;
  without that the arm64 build is killed by the kernel on launch. That is not Developer ID
  signing, so Gatekeeper still shows the "unidentified developer" prompt the first time and a
  user needs right-click → Open, or `xattr -cr agentic-memory`.
- **The two ARM targets are cross-published** (win-arm64 from an x64 Windows runner, linux-arm64
  from an x64 Linux runner). Nothing is compiled on the runner, since the runtime and every
  native dependency arrive prebuilt from NuGet, so the output is identical to what a native
  runner would produce. It just goes out unexercised, because the runner can't execute it.
- **ClearScript publishes V8 as one package per runtime identifier**, and the csproj selects the
  one matching the target. All six of these targets have one, so all six ship with TypeScript
  code intelligence. A target ClearScript doesn't publish for still builds; TypeScript indexing
  reports itself unavailable.

### The smoke test

Every target the runner can actually execute (all but the two cross-published ARM ones) is
started before it's packaged, and has to answer `GET /api/admin/health` and serve the dashboard
at `/` within three minutes, or the release fails.

This exists because a green test suite doesn't say much about a *published* build: the tests run
framework-dependent, out of a different directory layout, against the source tree. The smoke test
is the only step that exercises the assembled artifact, meaning the native libraries loading, the
model download, the schema migrator creating a database from nothing, and Kestrel actually
serving. It is also why `/api/admin/health` is exempt from API-key authentication: what makes it
usable as a readiness probe for an Electron host is what makes it usable here.

The dashboard check is not redundant with the health check. The content root can resolve to the
wrong place and leave every API route answering normally while `GET /` returns 404, which is how
that bug reached a build in the first place.

## Hotfixes

For a fix that can't wait for `develop` to reach release-ready state:

```bash
git checkout -b hotfix/short-description main
# fix, commit, bump <Version> in agentic-memory.csproj
git checkout main
git merge --no-ff hotfix/short-description
git push origin main
git tag -a v1.3.1 -m "v1.3.1"
git push origin v1.3.1
git checkout develop
git merge main
git push origin develop
```

Same shape as a normal release (merge into `main`, tag, back-merge into `develop`). The only
difference is that the fix branches from `main` instead of `develop`, so it ships without pulling
in whatever else is mid-flight on `develop`.

## Versioning

Tags follow [SemVer](https://semver.org/): `vMAJOR.MINOR.PATCH` (e.g. `v1.3.0`). The `v*` pattern
in `release.yml` matches any tag starting with `v`, so a pre-release like `v1.3.0-rc.1` also
triggers a build; the workflow marks any tag containing a hyphen as a GitHub pre-release so it
doesn't displace the latest stable one. The csproj check compares only the part before the
hyphen, so `v1.3.0-rc.1` is satisfied by `<Version>1.3.0</Version>`.

Nothing in SemVer covers the database schema. That version is not user-facing and is not expected
to track the app version. See [Two versions](#two-versions-and-only-one-of-them-is-the-tag).

## Quick reference

| I want to... | Do this |
|---|---|
| Ship what's on `develop` | Merge `develop` → `main`, bump `<Version>`, tag `main`, push tag |
| Ship an urgent fix now | Branch `hotfix/*` from `main`, merge to `main`, bump, tag, back-merge to `develop` |
| Run tests without releasing | Just push; [`ci.yml`](.github/workflows/ci.yml) runs on every push/PR, any branch |
| Ship a release candidate | Tag `v1.3.0-rc.1`; it publishes as a GitHub pre-release |
| Re-run a failed release | Delete the tag locally and remotely, fix the problem, re-tag, re-push |
| Check what schema version a build supports | `GET /api/admin/database` → `supportedSchemaVersion` |
