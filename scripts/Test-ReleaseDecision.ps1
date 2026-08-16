$ErrorActionPreference = "Stop"
$decisionScript = Join-Path $PSScriptRoot "Get-ReleaseDecision.ps1"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MetaVoiceType.ReleaseTests." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function Assert-Throws([scriptblock]$Action, [string]$ExpectedText) {
    try { & $Action; throw "Expected an exception containing '$ExpectedText'." }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedText, [StringComparison]::OrdinalIgnoreCase)) { throw }
    }
}

try {
    $props = Join-Path $testRoot "Directory.Build.props"
    Set-Content -LiteralPath $props -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'

    $existing = & $decisionScript -PropsPath $props -ReleaseTagsJson '[{"tagName":"v1.2.3"}]' -OutputFile ""
    Assert-Equal $false $existing.shouldRelease "An existing release must not be republished."
    Assert-Equal $true $existing.releaseExists "Existing release detection failed."

    $missing = & $decisionScript -PropsPath $props -ReleaseTagsJson '[{"tagName":"v1.2.2"}]' -OutputFile ""
    Assert-Equal $true $missing.shouldRelease "A missing release must be published."
    Assert-Equal "v1.2.3" $missing.tag "The version tag was not derived correctly."

    $missingVersionProps = Join-Path $testRoot "NoVersion.props"
    Set-Content -LiteralPath $missingVersionProps -Value '<Project><PropertyGroup /></Project>'
    Assert-Throws { & $decisionScript -PropsPath $missingVersionProps -ReleaseTagsJson '[]' -OutputFile "" } "does not declare Version"

    $failingGh = Join-Path $testRoot "failing-gh.cmd"
    Set-Content -LiteralPath $failingGh -Value "@echo authentication failed 1>&2`r`n@exit /b 7"
    Assert-Throws { & $decisionScript -PropsPath $props -Repository "owner/repo" -GhPath $failingGh -OutputFile "" } "GitHub release query failed"

    Write-Host "Release decision tests passed."
}
finally {
    if ((Resolve-Path -LiteralPath $testRoot).Path.StartsWith([System.IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

# GitHub's pwsh wrapper exits with the last native process code even when an
# expected failure was caught and all assertions passed. Do not leak the fake
# gh fixture's exit code into the workflow step.
$global:LASTEXITCODE = 0
