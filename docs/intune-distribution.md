# TDPdf — Intune distribution & code signing

Last reviewed: 2026-05-18 (1.0.0.3)

This is the canonical workflow for shipping TDPdf to a corporate Windows
fleet via Microsoft Intune. It supersedes the older `code-signing.md`
roadmap. Everything described here is Windows-only — `signtool`,
`LocalMachine\*` cert stores, `IntuneWinAppUtil.exe`, and `dotnet publish`
for `win-x64` all require running on the Windows release box.

## Overview

TDPdf is distributed as an **Intune Win32 app**, not as a public download:

- Single signed `TDPdf.exe` is packaged into a `.intunewin` archive.
- Intune installs it machine-wide under `%ProgramFiles%\TDPdf\`.
- The app self-installs and self-uninstalls via CLI flags
  (`/install [/silent]` and `/uninstall [/silent]`) so Intune can drive
  the lifecycle headlessly.
- Updates ship by bumping `<Version>` / `<AssemblyVersion>` /
  `<FileVersion>` in `TDPdf.csproj`; Intune's detection rule
  (registry value `HKLM\Software\TDPdf!Version >= <AssemblyVersion>`)
  re-runs the installer on every enrolled device.

## One-time setup

### 1. Self-signed code-signing certificate

TDPdf.exe is signed with a self-signed Authenticode cert issued to
`CN=The Doodle Project (Self-Signed)`. The public part is deployed to
managed devices via Intune so on those devices:

- Authenticode signature validates (cert is in `LocalMachine\Root`).
- SmartScreen / Defender treat it as a known publisher (cert is in
  `LocalMachine\TrustedPublisher`).

**This trust does NOT extend to the public.** On any device outside the
managed fleet, SmartScreen still warns "Unknown publisher". Self-signing
is a stopgap for corporate distribution — nothing more. If you need
public distribution, buy an OV/EV code-signing cert.

Generate (elevated PowerShell on the release box):

```powershell
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=The Doodle Project (Self-Signed)" `
  -KeyUsage DigitalSignature `
  -KeyAlgorithm RSA -KeyLength 4096 `
  -HashAlgorithm SHA256 `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter (Get-Date).AddYears(5)

# Export the PFX (private key) — keep this file safe, password-protect it.
$pw = Read-Host -AsSecureString "PFX password"
Export-PfxCertificate -Cert $cert -FilePath C:\certs\tdpdf-codesign.pfx -Password $pw

# Export the public .cer for Intune distribution.
Export-Certificate -Cert $cert -FilePath C:\certs\tdpdf-codesign.cer
```

### 2. Deploy the public cert to the fleet

In Intune admin center, deploy `tdpdf-codesign.cer` twice as a
**Trusted Certificate** profile:

- Destination store: **Computer certificate store - Root**
- Destination store: **Computer certificate store - Trusted Publishers**

Assign both profiles to the device group that will receive TDPdf. Until
this lands on a device, signed TDPdf.exe still trips "Unknown
publisher" SmartScreen prompts on it.

### 3. IntuneWinAppUtil

Download `IntuneWinAppUtil.exe` from the [Microsoft Win32 Content Prep
Tool repo](https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool)
and drop it at `C:\IntuneWinAppUtil.exe` (the default `release.ps1`
expects this path; override with `-IntuneWinAppUtilPath`).

## Per-release workflow

### 1. Bump versions

Edit `TDPdf.csproj` and bump **all three** version properties together:

```xml
<Version>1.1.0-tdpdf</Version>
<AssemblyVersion>1.1.0.1</AssemblyVersion>
<FileVersion>1.1.0.1</FileVersion>
```

- `<Version>` is SemVer — used for filenames, display, and the
  `.intunewin` filename. Pre-release suffixes (`-tdpdf`) are fine here.
- `<AssemblyVersion>` / `<FileVersion>` are 4-part numeric — written to
  EXE file metadata and used by Intune's detection rule. **These drive
  fleet updates** — if you don't bump them, Intune will not push.

Then add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` and
update the version badge in `pdf-landing/index.html`.

### 2. Run `release.ps1`

From an elevated PowerShell on the Windows release box, in the repo
root:

```powershell
.\release.ps1 -PfxPath C:\certs\tdpdf-codesign.pfx
```

The script will:

1. Prompt for the PFX password via `Read-Host -AsSecureString`.
2. `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
   -p:SelfContained=true` → `bin\Release\net9.0-windows\win-x64\publish\TDPdf.exe`.
3. Sign with `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256
   /f <pfx> /p <pw>` and verify with `signtool verify /pa /v`.
4. Print the SHA256 of the signed EXE.
5. Emit `TDPdf-<Version>-src.zip` (GPLv3 corresponding source, via the
   `BundleSource` MSBuild target that calls `build/bundle-source.ps1`).
6. Stage the signed EXE alone in `intune-staging\` and run
   `IntuneWinAppUtil.exe -c <staging> -s TDPdf.exe -o <publish> -q`,
   producing `TDPdf-<Version>.intunewin`.
7. Print the **exact Intune Win32 app form values** to paste.

Other signing options:

- `-CertStoreThumbprint <hash>` — sign with a cert already in your
  current user `My` store.
- No signing flags + Certum SimplySign cert installed — falls back to
  the original Certum store-cert flow.
- `-PackageIntune:$false` — skip the `.intunewin` step (useful for a
  signing-only dry run; pair with `-SkipSign` for a full no-op build).

### 3. Upload to Intune

In Intune admin center → **Apps → Windows → Add → Windows app (Win32)**:

| Field | Value |
| --- | --- |
| App package file | `TDPdf-<Version>.intunewin` |
| Name | `TDPdf` |
| Publisher | `The Doodle Project` |
| Install command | `TDPdf.exe /install /silent` |
| Uninstall command | `"%ProgramFiles%\TDPdf\TDPdf.exe" /uninstall /silent` |
| Install behavior | **System** |
| Device restart behavior | No specific action |
| Operating system architecture | x64 |
| Minimum operating system | Windows 10 1809 |
| Detection rule | **Use a custom detection script** → upload `build/intune/Detect-TDPdf.ps1` from this repo (see "Detection rule" below). |

> The `/silent` flag on the install command matters. Without it, an error
> inside `DoInstall` running under LocalSystem (session 0) used to try to
> raise a `MessageBox` on an invisible desktop, which could mask the
> failure as "exit code 0, app not detected." `1.0.0.3` removes that
> dialog path entirely, but the headless flag is still the correct
> production setting.

#### Detection rule

The installer writes `HKLM\Software\TDPdf` with `Installed=1`,
`InstallPath`, and `Version` **last** in `DoInstall` — only after the
EXE has been copied, ARP entry written, and PDF file association
registered. Any of the three options below check that marker; pick one.

**Recommended — Custom detection script (most robust):**

1. Under **Detection rules** choose **Rules format: Use a custom
   detection script**.
2. Upload `build/intune/Detect-TDPdf.ps1` from this repo.
3. Leave **Run script as 32-bit process on 64-bit clients = No**.
4. Leave **Enforce script signature check = No** (the script isn't
   signed; you can sign it yourself and flip this on if your tenant
   requires it).

The script reads the registry marker, parses `Version` with
`[Version]::TryParse`, and emits stdout only when the installed version
is greater than or equal to `$MinVersion` (defaulted to `1.0.0.3`). Bump
`$MinVersion` in the script for each release.

**Fallback A — Registry value comparison (manual rule):**

If you prefer a manual rule, Intune validates the fields strictly and
rejects malformed entries with "invalid detection rule, unable to parse
detection rule." Use these exact values:

| Field | Value |
| --- | --- |
| Rule type | `Registry` |
| Key path | `Software\TDPdf` *(no hive prefix, no leading backslash)* |
| Value name | `Version` |
| Detection method | `String comparison` |
| Operator | `Greater than or equal to` |
| Value | `1.0.0.3` *(literal release version — do not paste `<AssemblyVersion>`)* |
| Associated with a 32-bit app on 64-bit clients | `No` |

The hive (`HKEY_LOCAL_MACHINE`) is implicit when "Associated with a
32-bit app on 64-bit clients" is **No**. `String comparison` against a
dotted version like `1.0.0.3` works because Intune compares
left-to-right by numeric segment. Avoid the `Version` data type — some
tenants reject it at save time.

**Fallback B — Registry key exists (simplest):**

If you don't need a minimum-version gate (a fresh deploy after
uninstall always replaces the marker), the simplest possible rule is:

| Field | Value |
| --- | --- |
| Rule type | `Registry` |
| Key path | `Software\TDPdf` |
| Value name | `Installed` |
| Detection method | `Value exists` |
| Associated with a 32-bit app on 64-bit clients | `No` |

This will detect *any* installed TDPdf, including older versions — fine
for first-time rollout, less useful for forcing upgrades.

Assign to the test device group first, validate install on a real
enrolled device, then promote to the broader assignment.

### 4. Tag the release in git

After merge to `main`, move (or create) the release tag:

```bash
git tag -a v<Version> -m "TDPdf v<Version>"
git push origin v<Version>
```

## How the in-app installer works

`App.xaml.cs` handles the install/uninstall paths. Two CLI flag pairs:

- **`/install`** — interactive install (used by the first-launch dialog
  when a user runs the bare EXE).
- **`/install /silent`** — used by Intune. Suppresses MessageBoxes and
  returns exit code 1 on failure so Intune retries on the next sync.
- **`/uninstall`** — interactive uninstall with a confirmation dialog.
- **`/uninstall /silent`** — used by Intune (and the `QuietUninstallString`
  in Add/Remove Programs). No prompts.

Install behavior (silent or interactive):

1. Copies the running EXE to `%ProgramFiles%\TDPdf\TDPdf.exe` (when run
   as SYSTEM) or `%LocalAppData%\Programs\TDPdf\TDPdf.exe` (when run by
   a normal user — the legacy first-launch dialog path).
2. Verifies the copy landed on disk (file exists, non-zero length). If
   not, throws — the caller in `OnStartup` maps the exception to
   `Shutdown(1)` so Intune sees a real failure code rather than a
   swallowed exception.
3. Writes the `TDPdf.pdf` ProgID + `.pdf` extension association under the
   matching hive (`HKLM\Software\Classes` for SYSTEM, `HKCU\Software\Classes`
   otherwise).
4. Writes the Add/Remove Programs entry under the matching hive's
   `Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf` with both
   `UninstallString` (interactive) and `QuietUninstallString`
   (`/uninstall /silent`) set.
5. Creates a Start Menu shortcut — All Users (`CommonPrograms`) for the
   system-context install, per-user (`Programs`) otherwise.
6. **Last**, writes the Intune detection marker at
   `Software\TDPdf` (`Installed=1`, `InstallPath`, `Version`). This is
   intentionally the final step: a failure anywhere above must not
   leave a "detected" half-install behind.

Uninstall writes a deferred batch file that self-deletes the install
directory after the process exits, then removes the HKCU entries.

### Install log

Every `/install` and `/uninstall` invocation appends a timestamped log
to `install.log`. Location depends on the scope:

- SYSTEM (Intune install behavior=System) → `%ProgramData%\TDPdf\install.log`
- Interactive user install → `%LocalAppData%\TDPdf\install.log`

The log records the resolved scope, the source EXE path, the
destination path, the post-copy `FileVersionInfo`, every registry step,
and the final `INSTALL OK` / `INSTALL FAILED: <exception>` line. It
rotates at ~1 MB. This is the first thing to check when Intune reports
"installed successfully" but the detection rule fails to flip — the log
will show whether the file copy actually happened and which registry
keys were written.

## Update model

Intune polls each device on its sync cycle (default ~8 hours, manual
sync from Company Portal forces it sooner). On each sync:

1. Intune runs the detection rule: the value at
   `HKLM\Software\TDPdf!Version` is read and version-compared to the
   `<AssemblyVersion>` from the uploaded `.intunewin`.
2. If the installed version is `< <AssemblyVersion>`, Intune re-runs
   the install command (`TDPdf.exe /install /silent`).
3. The installer copies the new EXE over the old one in
   `%ProgramFiles%\TDPdf\` and rewrites the registry marker last.

So **bumping versions in `TDPdf.csproj` and re-uploading the
`.intunewin` is the entire update procedure** — no separate "update"
package, no MSI, no in-app updater.

### Known limitation: locked EXE

If TDPdf is running when Intune pushes an update, `File.Copy` in
`DoInstall` throws `IOException`. `/install /silent` returns exit code
1 and Intune retries on the next sync. Acceptable for an internal app
since the PDF editor may have unsaved work — we don't force-kill it.
If this becomes a real-world pain point, the fix is to copy the new
EXE to a temp path and use `MoveFileEx` with
`MOVEFILE_DELAY_UNTIL_REBOOT`, or restart-the-app pattern.

## Defender / SmartScreen / ASR notes

Cert trust only solves the Authenticode "Unknown publisher" prompt. The
other Windows blocking layers behave independently:

- **SmartScreen "Windows protected your PC"** — reputation-based.
  Self-signed certs cannot pass it on unmanaged devices. **Intune Win32
  deployment bypasses it entirely** (the management agent doesn't apply
  Mark-of-the-Web). Only EV code-signing certs build SmartScreen
  reputation for direct downloads.
- **Attack Surface Reduction rule
  `01443614-cd74-433a-b99e-2ecdc07bfc25`** ("Block executable files
  from running unless they meet a prevalence, age, or trusted list
  criterion") — independent of cert trust. On managed devices it can
  still block TDPdf even after the cert is trusted. Mitigations:
  - Path exclusion to the Intune ASR policy:
    `%ProgramFiles%\TDPdf\*`
  - Or flip the rule to Audit mode for the test group.
  - Or set up WDAC publisher allow-listing (heavier; only worth it if
    you're already running WDAC).
- **UAC** — the Intune system-context install runs as LocalSystem so no
  UAC prompt appears. Interactive end-user installs (double-click EXE
  → click Install) fall back to a per-user install under
  `%LocalAppData%` and also don't require elevation.

## Telemetry (optional)

Starting with **v1.0.0.6**, TDPdf supports **two complementary** ways to
opt a device into anonymous Application Insights telemetry. With neither
in play, TDPdf is a no-op: the SDK is initialized with an empty
configuration and no network calls are made.

| Method | Who provisions | When the key lives in the package |
|---|---|---|
| **Build-time-embedded key** (recommended for managed fleets) | The release engineer, once, when running `release.ps1` | Compiled into `TDPdf.exe` itself; never in a separate file on the device |
| **Per-device file-based provisioning** | An admin on the device via `TDPdf.exe /set-telemetry` | Only on devices where someone explicitly ran the command |

Either method ends up at the same on-device state: a hardened
`%ProgramData%\TDPdf\telemetry.dat` containing a DPAPI-LocalMachine-
encrypted copy of the connection string. The only difference is **how
that file gets there**.

### What gets sent (when enabled)

- `App.Startup` — once per interactive launch. Properties: `AppVersion`,
  `InstallScope` (`Installed` / `Portable`), `OSVersion`,
  `Is64BitProcess`.
- `Install.Start` / `Install.Success` — when `/install` runs.
- `Install.Crash` / `Crash` — sanitized crash events. Stack traces have
  Windows/POSIX paths, `password=`, and connection-string fragments
  redacted. A 12-hex-char `GroupingKey` (SHA-256 of
  `type|firstScrubbedFrame`) is included for bucketing.
- `Tool.Selected` — tool palette interactions. Property: `Tool` name.
- `File.Open` / `File.New` / `File.Save` / `File.SaveFlattened` /
  `File.Merge` / `File.Split` / `File.Print` — coarse-grained usage.
  **No file names, paths, sizes, or document content** are ever sent.

What is **never** sent: file paths, file names, document content, user
names, machine names, persistent device identifiers (no `User.Id`, no
`Device.Id`). Each session is anonymous.

### Recommended Azure setup

1. Create a **dedicated** Application Insights resource (workspace-based)
   in a resource group you control. **Do not reuse** one that already
   receives traffic from other applications — it makes anomaly
   investigation harder and complicates a key rotation if you ever need
   one. The blast radius of a leaked TDPdf key should be bounded to
   this resource and nothing else.
2. Set a **daily ingestion cap** (Application Insights → Usage and
   estimated costs → Daily cap). Even though TDPdf events are small,
   this protects against runaway costs from a buggy build or an attacker
   who recovers the embedded key.
3. Configure an **anomaly alert** so you're notified if event volume
   spikes (e.g. >10× the rolling 7-day average) — first indicator that
   a key has leaked.
4. Copy the **connection string** (not the legacy instrumentation key)
   from the resource's Overview blade.

### Method 1 — Build-time-embedded key (single Intune deployment)

This is the recommended path when you're shipping TDPdf via Intune to a
fleet of devices you administer. The connection string is encrypted into
`TDPdf.exe` at release time; the same single `.intunewin` you already
deploy carries everything needed for telemetry to light up.

**Release-time (one-off, on your release machine):**

1. Set the connection string in your **environment** for the shell
   session that will run `release.ps1`. Never as a command-line argument
   (it would land in process listings and the PowerShell history file):
   ```powershell
   $env:TDPDF_APPINSIGHTS_CONN = (Get-Content -Raw .\secret.txt).Trim()
   .\release.ps1
   Remove-Item Env:TDPDF_APPINSIGHTS_CONN
   ```
2. `release.ps1` will invoke `build\embed-telemetry-key.ps1` which
   generates a per-release AES-256 key, encrypts the connection string,
   XOR-splits the key across two compiled-in halves, and writes a
   gitignored `Diagnostics/EmbeddedTelemetry.Generated.cs` that the
   build picks up. Immediately after publish (success or failure) the
   generated file is **deleted** from the working tree.
3. Verify: `git status` should report a clean tree, and `git ls-files
   Diagnostics/` should NOT list `EmbeddedTelemetry.Generated.cs`. The
   source bundle (`TDPdf-<version>-src.zip`) is built from `git
   ls-files`, so it carries only the placeholder.
4. Deploy the resulting `.intunewin` as you normally do. **No
   second Intune app, no per-device PowerShell.**

**On the device (automatic):**

1. Intune installs TDPdf via `TDPdf.exe /install /silent` running as
   SYSTEM. The install branch attempts auto-provisioning: decrypts the
   embedded key, writes `%ProgramData%\TDPdf\telemetry.dat`, hardens the
   ACL.
2. If the device user later runs TDPdf interactively, startup also
   tries to auto-provision (covering the user-context-install case).
3. Auto-provisioning skips if `telemetry.dat` already exists, if a
   `telemetry.disabled` sentinel is present (see "Clearing", below), or
   if the embedded constants are empty (dev/CI builds).

**Threat model**: the encrypted blob and both XOR-split key halves live
in `TDPdf.exe`. A non-admin user on the device can't pull the key out
with a one-line PowerShell, but anyone who can reverse engineer or
debug-attach the binary can recover it. This is a **speed bump, not
strong cryptography**. The mitigations are the Azure-side daily cap,
dedicated resource, anomaly alert, and key rotation — not the embedded
encryption itself.

### Method 2 — Per-device file-based provisioning (fallback)

Useful when you don't want to (or can't) embed the key at release time:
admin-managed dev machines, exploratory testing, customer-specific
keys, etc. This is the original `1.0.0.5` workflow.

`%ProgramData%\TDPdf\telemetry.dat` is written manually via stdin (never
argv) to keep the connection string out of process listings and the
installer log:

```powershell
# secret.txt contains exactly the connection string on one line:
# InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://...
Get-Content -Raw secret.txt | & "$env:LOCALAPPDATA\Programs\TDPdf\TDPdf.exe" /set-telemetry
```

Or from `cmd`:

```cmd
type secret.txt | "C:\Path\to\TDPdf.exe" /set-telemetry
```

Running `/set-telemetry` also clears the `telemetry.disabled` sentinel,
so it re-enables on a previously-cleared device.

### Clearing telemetry on a device

```cmd
"C:\Path\to\TDPdf.exe" /clear-telemetry
```

This:
- deletes `%ProgramData%\TDPdf\telemetry.dat`;
- writes a `%ProgramData%\TDPdf\telemetry.disabled` sentinel that
  suppresses auto-provisioning from the embedded key on subsequent
  launches.

So `/clear-telemetry` is **sticky**: an Intune re-install of the same
build will not silently re-enable telemetry on a device the user
opted out of. To re-enable, an admin must run `/set-telemetry` or
delete the sentinel file.

### Rotating the connection string

The embedded key is encrypted with a fresh per-release AES key, so a
key rotation means cutting a new TDPdf release. There is no on-device
rotation step.

1. In the Azure portal, **regenerate** the App Insights connection
   string (or stand up a new App Insights resource entirely if you want
   a clean break).
2. Cut a new TDPdf release with the new value in
   `$env:TDPDF_APPINSIGHTS_CONN`. Bump the patch version.
3. Deploy via Intune as you normally do — the next install on each
   device overwrites `telemetry.dat` with the new key (atomic write via
   `.tmp` + `File.Replace`).
4. Once you confirm new-resource events are flowing, you can disable
   the old App Insights resource. The old key is now inert no matter
   what's still on a device.

The same flow works for the file-based fallback: replace `secret.txt`
and re-run `/set-telemetry` on the device.

### Why this design, not stronger crypto?

The threat model is: an **administrator** of the device is trusted (they
deployed the package); a non-admin local user should not be able to
casually read the connection string. Both the embedded-key path and the
file-based path rely on:

- DPAPI `LocalMachine` scope on `telemetry.dat` (a stolen file is
  useless on another machine);
- Explicit ACLs (no inheritance; SYSTEM + Admins FullControl,
  AuthenticatedUsers Read only);
- Compiled-in XOR-split entropy in `TDPdf.exe` for the embedded path
  (raises the bar above a PowerShell one-liner).

Anyone who can run code as SYSTEM (or attach a debugger to the elevated
TDPdf process) can recover the key. Use a **dedicated resource +
daily cap + anomaly alert + rotation discipline** so a recovered key
has a bounded blast radius. If you need stronger guarantees, don't
enable telemetry — the default no-op behavior is always safe.

## GPLv3 compliance reminder

Every release ships `TDPdf-<Version>-src.zip` alongside `TDPdf.exe` to
satisfy §5(a) "corresponding source". `release.ps1`'s publish step
triggers the `BundleSource` MSBuild target which calls
`build/bundle-source.ps1`. The zip is built from `git ls-files`, so any
**unstaged** file is excluded — commit everything before releasing.
Don't strip `LICENSE`, `NOTICE`, or the "fork of KillerPDF" attribution
in the About dialog / README / CHANGELOG.
