# CSRoll — Working Conventions

## Version bump

Every code change (bug fix, new modifier, rework, etc.) bumps `PluginVersion` in `src/CSRoll.cs`:

```csharp
private const string PluginVersion = "1.19.4";
```

- Patch bump (`1.19.3` -> `1.19.4`) for a normal batch of fixes/tweaks.
- Minor bump (`1.19.x` -> `1.20.0`) for a larger batch of new features (e.g. adding a new modifier).
- Never leave a change uncommitted-and-unversioned — the version string is the checkpoint marker
  that later becomes a commit, so if it wasn't bumped, that work has no version of its own and gets
  folded into whichever bump comes next.

Before bumping/committing:

1. `dotnet build` must succeed with 0 errors.
2. `dotnet publish -c Release` to refresh `build/publish/CSRoll` and `build/CSRoll.zip` (both
   gitignored — this is a local build step, not something to commit).

## Commit style

One commit per version bump. Message format:

```
v{version}

FEAT:
- bullet point per new feature/behavior addition (omit this section entirely if there are none)

BUGFIXES:
- bullet point per bug fix, phrased as root-cause -> fix (omit this section entirely if there are none)
```

- Keep bullets factual and specific (name the modifier/file, not just "fixed a bug").
- Stage only the files that actually belong to that version's changes — don't bundle unrelated
  in-flight work into one commit.
- Push after each commit (`git push origin main`) rather than batching multiple versions into one
  push, unless told otherwise.

## Releases

Every version commit gets a matching GitHub Release, published right after that commit is pushed:

1. `git tag -a v{version} -m "v{version}" <commit-sha>` and `git push origin v{version}`.
2. `gh release create v{version} build/CSRoll.zip --repo LCrew/S2-CSRoll --title "v{version}" --notes "..."`,
   with notes mirroring that commit's `FEAT:`/`BUGFIXES:` bullets (Markdown `## FEAT` / `## BUGFIXES`
   headings instead of plain-text labels).

`build/CSRoll.zip` (from the publish step above) is the artifact — `build/` is gitignored, so the
zip never goes into the repo itself, only into the release.

## Repo/remote

- Remote: `origin` -> `https://github.com/LCrew/S2-CSRoll.git`, public, default branch `main`.
- `gh` is authenticated as the repo owner (`LCrew`) with `repo` scope.
