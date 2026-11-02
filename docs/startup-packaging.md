# Payload signing and GUI startup checks

Release packaging signs exactly KillerPDF.App.exe, KillerPDF.App.dll, and
KillerPdf.Engine.dll in both installer and portable payloads. Publish, including any
ReadyToRun transformation, finishes first. Each signature is verified before repacking.
Signed repacking rejects missing, unsigned, catalog-only, or invalid owned binaries
before regenerating payload.manifest from final bytes. Third-party signatures are preserved.
Final release validation also checks those three files extracted from each public
executable, including PublishOnly mode, and from the isolated installed copy.

The payload build and final portable/installed checks run --startup-smoke instead of
--version. This constructs the real MainWindow, including SetTool(Select) and
ClearMeasurement, shows it transparent without activation or a taskbar entry, waits for
Loaded, ContentRendered, and dispatcher idle, then closes cleanly. Settings are fresh
and in memory; signature/data paths use a unique temporary root. This check skips
single-instance IPC, protocol registration, saved documents, update checks, and normal
user temporary-file cleanup. The portable launcher uses a separate temporary extraction
root and suppresses interactive failure dialogs for this command.

Run one checkout at a time with its supported .NET SDK and an interactive Windows
desktop capable of rendering WPF. No signing credentials are needed for these checks:

```powershell
powershell -NoProfile -File build/Test-PayloadSecurity.ps1
powershell -NoProfile -File build/Test-GuiStartupRunner.ps1
dotnet build KillerPDF.csproj -c Release -p:KillerPayloadBuild=true
powershell -NoProfile -File build/Test-GuiStartup.ps1 -ExePath bin/Release/net10.0-windows/KillerPDF.App.exe
```

The runner returns failure for nonzero exit, timeout, missing milestones, or a failure
report. Reports and stdout/stderr are retained in the reported temporary evidence folder,
outside the payload and source tree. Its deadline terminates only its created process
tree; the app also has a 30-second watchdog.

Fixtures validate target selection, failure cases, regenerated hashes, and script ordering
without signing. They do not prove Authenticode trust for a future release. Steve runs
the actual signing/release workflow and checks the final packages. GUI startup does not
prove compatibility with a reporter's Application Control policy or establish #444's cause.

Print cancellation has separate [document-state smoke cases](../tests/KillerPDF.DocumentStateSmoke/README.md).
Use a leased UI VM with an existing Microsoft Print to PDF queue and a test-owned loose
payload. The preparation cases request close through the actual preview and reject
later progress. The native cancellation case requires the operator to cancel the virtual
printer's output picker. These checks do not print to physical hardware.

## Reusing a signed release dry run

Commit the release inputs first. Keep SimplySign connected and run a full signed dry run:

```powershell
.\release.ps1 -DryRun -SkipStoreSubmission
```

A completed full dry run writes `bin/Release/net10.0-windows/publish/release-validation.json`
and retains the engine and desktop TRX files beside it. A dry run started with an older
script has no reusable record. An unsigned dry run cannot authorize reuse.

After a successful dry run, keep the source commit and all outputs unchanged, synchronize
that commit with origin/main, and publish with:

```powershell
.\release.ps1 -ReuseDryRun -SkipStoreSubmission
```

`ReuseDryRun` implies `PublishOnly`. It skips building, signing, startup/install smoke tests
and both unit suites only after checking the recorded source commit, version and exact
package, source archive, checksum and TRX hashes. Empty, skipped, failing or interrupted
validation is refused. Current dependency audits, package versions, outer/embedded
signatures, checksums, clean-tree and remote-state gates still run. The engine site facts
use the retained engine results. A second identity check runs before publishing.

`SkipStoreSubmission` skips both Store API preflight and submission. The Store installer
is still built, verified and attached to the GitHub release for a later Store submission.
Without this option, both Store steps retain their normal behavior. `PublishOnly` alone
continues to run tests. Starting another non-reuse release attempt invalidates the earlier
record until a new full dry run succeeds.

Offline regression checks use fresh scratch directories and never publish:

```powershell
powershell -NoProfile -File build/Test-ReleaseDryRun.ps1 -EvidenceRoot C:/Users/steve/scratch/kp-release-receipt-test
pwsh -NoProfile -File build/Test-ReleaseOptions.ps1 -EvidenceRoot C:/Users/steve/scratch/kp-release-options-test
```
