using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Models;
using MetaVoiceType.Storage;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace MetaVoiceType.Transcription;

public sealed partial class SherpaRuntimeBootstrapper
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly StartupOptions _options;
    private readonly ILogger<SherpaRuntimeBootstrapper> _logger;
    private bool _configured;
    private string? _gpuLibrary;
    private string? _loadFailure;

    public SherpaRuntimeBootstrapper(AppPaths paths, StartupOptions options, ILogger<SherpaRuntimeBootstrapper> logger)
    {
        _paths = paths;
        _options = options;
        _logger = logger;
    }

    public bool ForceCpu => _options.ForceCpu;
    public bool CudaRuntimeSelected { get; private set; }
    public string? GpuName { get; private set; }
    public string? RuntimeFailure => _loadFailure;

    public string? ProbeNvidiaGpu()
    {
        lock (_gate) return GpuName ??= DetectNvidiaGpu();
    }

    public bool Configure()
    {
        lock (_gate)
        {
            if (_configured) return CudaRuntimeSelected;
            _configured = true;
            if (ForceCpu)
            {
                _loadFailure = "CPU was forced by diagnostics.";
                return false;
            }

            GpuName = ProbeNvidiaGpu();
            if (GpuName is null)
            {
                _loadFailure = "No compatible NVIDIA GPU or driver was detected.";
                return false;
            }

            ModelArtifact runtime = ModelCatalog.LoadBundled().Get("sherpa-cuda-12");
            string runtimeRoot = Path.Combine(_paths.RuntimeModels, runtime.ExpectedDirectory);
            string library = Path.Combine(runtimeRoot, runtime.Files.NativeLibrary!);
            string libraryDirectory = Path.Combine(runtimeRoot, runtime.Files.LibraryDirectory!);
            if (!runtime.RequiredFiles.All(file => File.Exists(Path.Combine(runtimeRoot, file.Replace('/', Path.DirectorySeparatorChar)))))
            {
                _loadFailure = "The verified Sherpa CUDA runtime is not installed.";
                return false;
            }

            string[] cudaDependencies = ["cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "cufft64_11.dll", "cudnn64_9.dll"];
            string? dependencyDirectory = new[]
            {
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native")
            }.FirstOrDefault(directory => cudaDependencies.All(file => File.Exists(Path.Combine(directory, file))));
            if (dependencyDirectory is null)
            {
                _loadFailure = "The bundled NuGet CUDA/cuDNN dependencies are incomplete.";
                return false;
            }

            _gpuLibrary = library;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] current = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            string prepend = string.Join(Path.PathSeparator, new[] { libraryDirectory, dependencyDirectory }.Where(directory => !current.Contains(directory, StringComparer.OrdinalIgnoreCase)));
            if (prepend.Length > 0) Environment.SetEnvironmentVariable("PATH", prepend + Path.PathSeparator + path);
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, ResolveSherpaLibrary);
                CudaRuntimeSelected = true;
                LogRuntimeSelected(_logger, runtime.DisplayName, GpuName);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                _loadFailure = "Sherpa native runtime was already loaded before CUDA selection: " + ex.Message;
                LogRuntimeFallback(_logger, _loadFailure);
                return false;
            }
        }
    }

    private nint ResolveSherpaLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase) || _gpuLibrary is null) return 0;
        try { return NativeLibrary.Load(_gpuLibrary); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileLoadException)
        {
            _loadFailure = $"CUDA runtime load failed: {ex.Message}";
            CudaRuntimeSelected = false;
            LogRuntimeFallback(_logger, _loadFailure);
            return 0;
        }
    }

    private static string? DetectNvidiaGpu()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "--query-gpu=name --format=csv,noheader"
            });
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) return null;
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Selected {Runtime} for {Gpu}.")]
    private static partial void LogRuntimeSelected(ILogger logger, string runtime, string gpu);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Using Sherpa CPU runtime: {Reason}")]
    private static partial void LogRuntimeFallback(ILogger logger, string reason);
}
