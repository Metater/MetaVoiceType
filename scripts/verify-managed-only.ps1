# MetaVoiceType managed-code guard.
#
# Fails if application/project source contains newly authored native-code
# files or custom DllImport/LibraryImport declarations.
#
# Opaque third-party dependency trees (cuda/, cuda-redist/, models/,
# bin/, obj/) are NOT scanned — they are precompiled runtime artifacts,
# not application source.
#
# Exit code 0 = clean, 1 = violation found.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$applicationRoots = @(
    (Join-Path $repoRoot "MetaVoiceType.ConsolePrototype")
)

$nativePatterns = @(
    "*.c", "*.cc", "*.cpp", "*.cxx", "*.h", "*.hpp",
    "CMakeLists.txt", "*.vcxproj"
)

$violations = New-Object System.Collections.Generic.List[string]

foreach ($root in $applicationRoots) {
    if (-not (Test-Path $root)) { continue }

    # 1. Native source files in application directories (skip bin/obj).
    foreach ($pattern in $nativePatterns) {
        Get-ChildItem -Path $root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            ForEach-Object {
                $violations.Add("native file: $($_.FullName)")
            }
    }

    # 2. Custom P/Invoke in application .cs files.
    Get-ChildItem -Path $root -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $file = $_.FullName
            $content = Get-Content -Path $file -Raw
            if ($content -match '\[DllImport\(') {
                $violations.Add("DllImport in: $file")
            }
            if ($content -match '\[LibraryImport\(') {
                $violations.Add("LibraryImport in: $file")
            }
        }
}

if ($violations.Count -gt 0) {
    Write-Output "MANAGED-CODE GUARD FAILED:"
    $violations | ForEach-Object { Write-Output "  $_" }
    Write-Output ""
    Write-Output "MetaVoiceType policy: application code is C# using maintained"
    Write-Output "managed wrappers. Official precompiled runtime binaries used"
    Write-Output "internally by those wrappers are allowed. Custom native code"
    Write-Output "and custom P/Invoke require explicit project-owner authorization."
    Write-Output "See docs/MANAGED-CODE-POLICY.md."
    exit 1
}

Write-Output "MANAGED-CODE GUARD PASSED: no authored native files or custom P/Invoke in application source."
exit 0
