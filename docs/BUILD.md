# Build and run

The project targets Windows and uses the pinned .NET SDK **10.0.402**. On a fresh checkout, open PowerShell in the repository root:

```powershell
./scripts/bootstrap.ps1
dotnet restore DlutTimetable.slnx --locked-mode
dotnet build DlutTimetable.slnx -c Release --no-restore
```

Run the desktop app with the repository SDK:

```powershell
./scripts/run.ps1
./scripts/run.ps1 -Demo
```

`-Demo` loads synthetic sample data. For real data, the app stores the timetable and preferences under the current Windows user's local application data directory. To create the self-contained Windows x64 development build:

```powershell
./scripts/publish-windows.ps1
```

The package is written to `artifacts/releases/`. It includes the desktop app, its authentication worker, and the .NET runtime. School refresh also requires the Microsoft Edge WebView2 Runtime; cached timetable viewing works offline.
