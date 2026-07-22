# HomeWatch stability policy

HomeWatch changes must be made directly in the tracked source files. New regex-based `Apply-HomeWatch-v*.ps1` patch installers are not permitted.

## Stable startup

Use the validated launcher:

```powershell
.\Start-HomeWatch.ps1
```

The launcher uses port `8900` by default, validates the source before startup, refuses to start if validation fails, and reports a clear error when the port is already occupied.

To select another port:

```powershell
.\Start-HomeWatch.ps1 -Port 8901
```

## Required validation

Run this before every commit or deployment:

```powershell
.\Validate-HomeWatch.ps1
```

Validation checks:

- required source files exist
- `HomeWatch.ps1` passes the Windows PowerShell parser
- `web/app.js` passes `node --check` when Node.js is installed
- HTML declares UTF-8 and references required assets
- known corrupted encoding sequences are absent

GitHub Actions runs the same validation on every push and pull request.

## Change policy

1. Back up or commit the current working source.
2. Edit source files directly.
3. Run `Validate-HomeWatch.ps1`.
4. Start with `Start-HomeWatch.ps1` and test locally on port 8900.
5. Commit only after validation and manual smoke testing succeed.
6. Do not run old `Apply-HomeWatch-v*.ps1` installers against the working source.

## Recovery

To return tracked core files to the current Git commit:

```powershell
git restore HomeWatch.ps1 web\app.js web\index.html web\styles.css
```

Then validate and start:

```powershell
.\Validate-HomeWatch.ps1
.\Start-HomeWatch.ps1
```
