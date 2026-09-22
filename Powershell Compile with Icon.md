-- Powershell, with icon, single file, no .net

dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false


-- Powershell, with icon, single file, with .net

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false




dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-SelfContained
```[cite: 2, 8]

**Output File:** `bin\Release\Publish-SelfContained\Gw2WikiTool.exe`[cite: 8]

---

### Option 2: Standalone `.exe` **WITHOUT** .NET Runtime Included (Framework-Dependent)

This builds a lightweight single executable that relies on the .NET 8 runtime installed on the user's PC[cite: 8].

* **File Size:** ~10 MB–15 MB
* **Requirement:** The end user **must have the .NET 8 Desktop Runtime installed**.

```powershell
dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-FrameworkDependent
```[cite: 2, 8]

**Output File:** `bin\Release\Publish-FrameworkDependent\Gw2WikiTool.exe`[cite: 8]

---

### Combined PowerShell Script (Builds Both at Once)

You can copy and run this script in PowerShell from your project folder to build both versions sequentially:

```powershell
Write-Host "Building Self-Contained EXE (With .NET Runtime)..." -ForegroundColor Cyan
dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-SelfContained

Write-Host "`nBuilding Framework-Dependent EXE (Without .NET Runtime)..." -ForegroundColor Cyan
dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-FrameworkDependent

Write-Host "`nBuilds Complete!" -ForegroundColor Green
```[cite: 2, 8]