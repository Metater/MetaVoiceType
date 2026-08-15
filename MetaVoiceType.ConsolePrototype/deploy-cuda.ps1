# Overrides the CPU NuGet runtime DLLs with the CUDA-enabled sherpa-onnx
# release DLLs + NVIDIA redistributable runtime DLLs (CUDA 12.x, cuDNN 9.x).
#
# .NET probes runtimes/<rid>/native BEFORE the app root, so both locations
# must be overwritten or the CPU-only NuGet DLLs win.
param(
    [string]$OutDir = "bin\Release\net10.0"
)

$cudaBin = "..\cuda\sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda\bin"
$cudaLib = "..\cuda\sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda\lib"

if (-not (Test-Path $cudaBin)) { Write-Error "CUDA bin not found: $cudaBin"; exit 1 }

function Deploy-NativeDlls([string]$dest) {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    # sherpa-onnx-c-api.dll lives in lib/, the rest in bin/.
    Copy-Item "$cudaLib\sherpa-onnx-c-api.dll" -Destination $dest -Force
    Get-ChildItem $cudaBin -Filter *.dll | ForEach-Object {
        Copy-Item $_.FullName -Destination $dest -Force
    }
}

# App root
Deploy-NativeDlls $OutDir
# RID-specific folder (probed first by the .NET native loader)
Deploy-NativeDlls "$OutDir\runtimes\win-x64\native"

Write-Output "CUDA DLLs deployed to ${OutDir} and ${OutDir}\runtimes\win-x64\native"
Get-ChildItem $OutDir -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize
