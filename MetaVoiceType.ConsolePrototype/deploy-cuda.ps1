# Overrides the CPU NuGet runtime DLLs with the CUDA-enabled sherpa-onnx
# release DLLs + NVIDIA redistributable runtime DLLs (CUDA 12.x, cuDNN 9.x).
param(
    [string]$OutDir = "bin\Release\net10.0"
)

$cudaBin = "..\cuda\sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda\bin"
$cudaLib = "..\cuda\sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda\lib"

if (-not (Test-Path $cudaBin)) { Write-Error "CUDA bin not found: $cudaBin"; exit 1 }

# sherpa-onnx-c-api.dll lives in lib/, the rest in bin/.
Copy-Item "$cudaLib\sherpa-onnx-c-api.dll" -Destination $OutDir -Force
Get-ChildItem $cudaBin -Filter *.dll | ForEach-Object {
    Copy-Item $_.FullName -Destination $OutDir -Force
}
Write-Output "CUDA DLLs deployed to ${OutDir}:"
Get-ChildItem $OutDir -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize
