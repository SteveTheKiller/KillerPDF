# TDPdf — Intune distribution & code signing

Last reviewed: 2026-05-18

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
  (`file version >= <AssemblyVersion>`) re-runs the installer on every
  enrolled device.

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
<AssemblyVersion>1.1.0.0</AssemblyVersion>
<FileVersion>1.1.0.0</FileVersion>
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
| Install command | `TDPdf.exe /install` |
| Uninstall command | `"%ProgramFiles%\TDPdf\TDPdf.exe" /uninstall /silent` |
| Install behavior | **System** |
| Device restart behavior | No specific action |
| Operating system architecture | x64 |
| Minimum operating system | Windows 10 1809 |
| Detection rule | **File** → Path `%ProgramFiles%\TDPdf` → File `TDPdf.exe` → Detection method **String (version)** → Operator `Greater than or equal to` → Value `<AssemblyVersion>` (e.g. `1.1.0.0`) |

> Make sure to leave **"Associated with a 32-bit app on 64-bit clients"** set to **No** on both the install command and the detection rule. TDPdf is an x64 build that installs to the real `C:\Program Files\TDPdf`; if Intune treats the app as 32-bit it will look in `C:\Program Files (x86)\TDPdf` instead and detection will fail.

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
2. Writes the `TDPdf.pdf` ProgID + `.pdf` extension association under the
   matching hive (`HKLM\Software\Classes` for SYSTEM, `HKCU\Software\Classes`
   otherwise).
3. Writes the Add/Remove Programs entry under the matching hive's
   `Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf` with both
   `UninstallString` (interactive) and `QuietUninstallString`
   (`/uninstall /silent`) set.
4. Creates a Start Menu shortcut — All Users (`CommonPrograms`) for the
   system-context install, per-user (`Programs`) otherwise.

Uninstall writes a deferred batch file that self-deletes the install
directory after the process exits, then removes the HKCU entries.

## Update model

Intune polls each device on its sync cycle (default ~8 hours, manual
sync from Company Portal forces it sooner). On each sync:

1. Intune runs the detection rule: `file version` of
   `%ProgramFiles%\TDPdf\TDPdf.exe` is read.
2. If the installed version is `< <AssemblyVersion>` from the latest
   upload, Intune re-runs the install command (`TDPdf.exe /install`).
3. The installer copies the new EXE over the old one in
   `%ProgramFiles%\TDPdf\`.

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

## GPLv3 compliance reminder

Every release ships `TDPdf-<Version>-src.zip` alongside `TDPdf.exe` to
satisfy §5(a) "corresponding source". `release.ps1`'s publish step
triggers the `BundleSource` MSBuild target which calls
`build/bundle-source.ps1`. The zip is built from `git ls-files`, so any
**unstaged** file is excluded — commit everything before releasing.
Don't strip `LICENSE`, `NOTICE`, or the "fork of KillerPDF" attribution
in the About dialog / README / CHANGELOG.
