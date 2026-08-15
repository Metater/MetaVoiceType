param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts\publish"
$releaseDirectory = Join-Path $projectRoot "artifacts\releases"
$project = Join-Path $projectRoot "src\MetaVoiceType\MetaVoiceType.csproj"
$icon = Join-Path $projectRoot "src\MetaVoiceType\UI\Assets\metavoicetype.ico"

dotnet test (Join-Path $projectRoot "MetaVoiceType.slnx") -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
vpk pack --packId MetaVoiceType --packVersion $Version --packDir $publishDirectory --mainExe MetaVoiceType.exe --packTitle MetaVoiceType --packAuthors Metater --icon $icon --outputDir $releaseDirectory --runtime win-x64 --channel win --yes
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed." }
