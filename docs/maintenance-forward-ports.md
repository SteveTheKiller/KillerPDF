# Keeping maintenance fixes in Overkill

Every commit added to `main` after the reviewed 1.8.4 baseline must also be accounted for in `dev/1.9-overkill`. This includes code, tests, translations, documentation, and release metadata.

The **Maintenance changes reach Overkill** workflow checks pushes and pull requests for either branch. An identical cherry-picked patch passes automatically, even when the commit has a different version prefix. Commits already merged into Overkill also pass.

If Overkill needs a different implementation, port and test the fix, commit it with the `v1.9.0:` prefix, then add a record to `.github/maintenance-forward-ports.json`:

```json
{
  "maintenanceCommit": "FULL_40_CHARACTER_MAINTENANCE_HASH",
  "developmentCommit": "FULL_40_CHARACTER_OVERKILL_HASH",
  "reason": "Explain how the Overkill implementation carries this change."
}
```

Keep the records on both branches. Both commits must exist on the checked branches. Version-specific metadata also needs a record explaining its 1.9 counterpart; it is never silently excluded. Do not move the baseline to hide missing work.

Fetch current refs before a local check:

```powershell
git fetch origin
./build/Check-MaintenanceForwardPorts.ps1
```

To check local work before pushing both branches:

```powershell
./build/Check-MaintenanceForwardPorts.ps1 -MaintenanceRef main -DevelopmentRef dev/1.9-overkill
```

Missing ports fail with the commit hashes and subjects. Port the change before merging its maintenance PR, or push both completed branches together. A push to Overkill runs a fresh check after a missing port is added.

This checks commit coverage, not semantic correctness. Adapted ports still require review and tests. Reverts must be carried deliberately too. GitHub branch protection must require this status to block PR merges; the workflow alone reports failures and does not block direct pushes. No branch-protection settings are changed by installing this check.
