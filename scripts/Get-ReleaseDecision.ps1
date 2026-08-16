[CmdletBinding()]
param(
    [string]$PropsPath = "Directory.Build.props",
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ReleaseTagsJson,
    [string]$GhPath = "gh",
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PropsPath -PathType Leaf)) {
    throw "Version props file was not found: $PropsPath"
}

[xml]$props = Get-Content -Raw -LiteralPath $PropsPath
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "$PropsPath does not declare Version."
}

$tag = "v$version"
if (-not $PSBoundParameters.ContainsKey("ReleaseTagsJson")) {
    if ([string]::IsNullOrWhiteSpace($Repository)) {
        throw "A GitHub repository is required to query releases."
    }
    $queryOutput = & $GhPath release list --repo $Repository --json tagName --limit 1000 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($queryOutput | Out-String).Trim()
        throw "GitHub release query failed for '$Repository' (exit $LASTEXITCODE).`n$detail"
    }
    $ReleaseTagsJson = ($queryOutput | Out-String).Trim()
}

try {
    $releases = @($ReleaseTagsJson | ConvertFrom-Json)
}
catch {
    throw "GitHub release query returned invalid JSON: $($_.Exception.Message)"
}

$exists = @($releases | ForEach-Object { [string]$_.tagName }) -contains $tag
$shouldRelease = -not $exists
$decision = [ordered]@{
    version = $version
    tag = $tag
    releaseExists = $exists
    shouldRelease = $shouldRelease
}

if (-not [string]::IsNullOrWhiteSpace($OutputFile)) {
    @(
        "version=$version"
        "tag=$tag"
        "releaseExists=$($exists.ToString().ToLowerInvariant())"
        "shouldRelease=$($shouldRelease.ToString().ToLowerInvariant())"
    ) | Add-Content -LiteralPath $OutputFile
}

[pscustomobject]$decision
