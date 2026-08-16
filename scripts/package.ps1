param([string]$Version, [switch]$SkipTests, [switch]$SkipDeltaBase)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts\publish"
$releaseDirectory = Join-Path $projectRoot "artifacts\releases"
$project = Join-Path $projectRoot "src\MetaVoiceType\MetaVoiceType.csproj"
$icon = Join-Path $projectRoot "src\MetaVoiceType\UI\Assets\metavoicetype.ico"
[xml]$props = Get-Content (Join-Path $projectRoot "Directory.Build.props")
$declaredVersion = [string]$props.Project.PropertyGroup.Version
if (-not $Version) { $Version = $declaredVersion }
if ($Version -ne $declaredVersion) { throw "Package version '$Version' does not match Directory.Build.props version '$declaredVersion'." }

if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path -LiteralPath $releaseDirectory) { Remove-Item -LiteralPath $releaseDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

if (-not $SkipDeltaBase) {
    $downloadArguments = @("download", "github", "--repoUrl", "https://github.com/Metater/MetaVoiceType", "--outputDir", $releaseDirectory, "--channel", "win")
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { $downloadArguments += @("--token", $env:GITHUB_TOKEN) }
    & vpk @downloadArguments
    if ($LASTEXITCODE -ne 0) { throw "Could not download the latest release package required for delta generation." }
}

if (-not $SkipTests) {
    dotnet test (Join-Path $projectRoot "MetaVoiceType.slnx") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
vpk pack --packId MetaVoiceType --packVersion $Version --packDir $publishDirectory --mainExe MetaVoiceType.exe --packTitle MetaVoiceType --packAuthors Metater --icon $icon --outputDir $releaseDirectory --runtime win-x64 --channel win --delta BestSize --yes
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed." }

$currentFullPackage = "MetaVoiceType-$Version-full.nupkg"
Get-ChildItem -LiteralPath $releaseDirectory -Filter "MetaVoiceType-*-full.nupkg" |
    Where-Object Name -ne $currentFullPackage |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
