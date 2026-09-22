Write-Host "Building Self-Contained EXE (With .NET Runtime)..." -ForegroundColor Cyan
dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-SelfContained

Write-Host "`nBuilding Framework-Dependent EXE (Without .NET Runtime)..." -ForegroundColor Cyan
dotnet publish GW2WikiTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -o ./bin/Release/Publish-FrameworkDependent

Write-Host "`nBuilds Complete!" -ForegroundColor Green