# Phase 1 benchmark helper: decode test WAVs with CPU or CUDA provider.
# Usage: .\bench.ps1 <provider> <language> [wav]
param(
    [Parameter(Mandatory=$true)][string]$Provider,
    [Parameter(Mandatory=$true)][string]$Language,
    [string]$Wav = "es"
)

$model = "..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11"
$wavPath = Join-Path $model "test_wavs\$Wav.wav"
if (-not (Test-Path $wavPath)) { Write-Error "WAV not found: $wavPath"; exit 1 }

$threads = if ($Provider -eq "cuda") { 1 } else { 4 }

dotnet run -c Release --no-build -- `
    "--provider=$Provider" `
    "--encoder=$model\encoder.int8.onnx" `
    "--decoder=$model\decoder.int8.onnx" `
    "--joiner=$model\joiner.int8.onnx" `
    "--tokens=$model\tokens.txt" `
    "--language=$Language" `
    "--wav=$wavPath" `
    "--num-threads=$threads"
