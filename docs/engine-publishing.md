# Engine NuGet publishing

Publishing a stable GitHub app release triggers `nuget-engine-release.yml`. It checks out the
release tag, verifies matching app, engine, and release versions, runs the engine tests, packs
the library and symbols, and publishes through the existing NuGet trusted publisher.
The Overkill development branch is not a publishing trigger.

Update both project versions together. The engine build and `release.ps1` reject mismatches
before packaging. Maintain both changelogs when engine behavior changes.

## Retry a published release

The workflow can be started manually from `main`:

```powershell
gh workflow run nuget-engine-release.yml --ref main -f version=1.8.3
```

Normally the source is the matching release tag. For a tag with incorrect engine package
metadata, supply `-f source_ref=FULL_COMMIT_SHA` using a corrected commit on `main`.
The workflow verifies that engine files match the release tag, except the project metadata
and engine changelog. It does not move or replace the app release tag.

Packages are retained as workflow artifacts before publishing. If NuGet authentication fails,
download the `.nupkg` artifact and upload it through the NuGet.org package upload page, or fix
the trusted-publisher policy and rerun the workflow. Existing package versions are skipped.
