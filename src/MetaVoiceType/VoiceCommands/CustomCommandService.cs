using System.Diagnostics;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.VoiceCommands;

public sealed record CustomCommandExecution(bool Started, int? ExitCode, string StandardOutput, string StandardError);

public static class CustomCommandValidator
{
    public static void Validate(CustomVoiceCommand command, IEnumerable<string> builtInPhrases, IEnumerable<CustomVoiceCommand> otherCommands)
    {
        if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Name)) throw new InvalidDataException("Command name is required.");
        if (string.IsNullOrWhiteSpace(command.VoiceCommandLanguageId)) throw new InvalidDataException("Command language is required.");
        string normalized = CommandPhraseValidator.Normalize(command.Phrase);
        if (normalized.Length == 0 || normalized == "[unk]") throw new InvalidDataException("Command phrases cannot be blank or [unk].");
        if (builtInPhrases.Any(x => CommandPhraseValidator.Normalize(x).Equals(normalized, StringComparison.OrdinalIgnoreCase)) ||
            otherCommands.Where(x => x.Id != command.Id && x.VoiceCommandLanguageId.Equals(command.VoiceCommandLanguageId, StringComparison.OrdinalIgnoreCase))
                .Any(x => CommandPhraseValidator.Normalize(x.Phrase).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This phrase is already used in the selected command language.");

        switch (command.CommandType)
        {
            case CustomCommandType.Program when string.IsNullOrWhiteSpace(command.Executable): throw new InvalidDataException("Choose an executable.");
            case CustomCommandType.PowerShell or CustomCommandType.CommandPrompt when string.IsNullOrWhiteSpace(command.ScriptOrCommand):
                throw new InvalidDataException("Enter a script or command.");
            case CustomCommandType.KeyboardShortcut: _ = ShortcutGestureParser.ParseAction(command.Shortcut); break;
        }
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory) && !Directory.Exists(command.WorkingDirectory))
            throw new DirectoryNotFoundException("The command working directory does not exist.");
    }
}

public sealed partial class CustomCommandExecutor(IKeyboardInputSimulator input, ILogger<CustomCommandExecutor> logger)
{
    public async Task<CustomCommandExecution> ExecuteAsync(CustomVoiceCommand command, bool waitForExit = false, CancellationToken cancellationToken = default)
    {
        if (!command.Enabled) return new(false, null, "", "");
        if (command.CommandType == CustomCommandType.KeyboardShortcut)
        {
            await input.SendShortcutAsync(ShortcutGestureParser.ParseAction(command.Shortcut), cancellationToken).ConfigureAwait(false);
            LogExecuted(logger, command.Id, command.CommandType);
            return new(true, 0, "", "");
        }

        ProcessStartInfo startInfo = CreateStartInfo(command, waitForExit);
        using Process? process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException("Windows did not start the custom command.");
        LogExecuted(logger, command.Id, command.CommandType);
        if (!waitForExit) return new(true, null, "", "");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new(true, process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static ProcessStartInfo CreateStartInfo(CustomVoiceCommand command, bool captureOutput)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = command.WindowMode == CommandWindowMode.Hidden,
            WindowStyle = command.WindowMode switch
            {
                CommandWindowMode.Hidden => ProcessWindowStyle.Hidden,
                CommandWindowMode.Minimized => ProcessWindowStyle.Minimized,
                _ => ProcessWindowStyle.Normal
            },
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory)) info.WorkingDirectory = command.WorkingDirectory;
        switch (command.CommandType)
        {
            case CustomCommandType.Program:
                info.FileName = command.Executable;
                info.Arguments = command.Arguments;
                break;
            case CustomCommandType.PowerShell:
                info.FileName = "powershell.exe";
                info.ArgumentList.Add("-NoLogo");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add(command.ScriptOrCommand);
                break;
            case CustomCommandType.CommandPrompt:
                info.FileName = "cmd.exe";
                info.ArgumentList.Add("/D");
                info.ArgumentList.Add("/S");
                info.ArgumentList.Add("/C");
                info.ArgumentList.Add(command.ScriptOrCommand);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(command), "Unsupported custom command type.");
        }
        return info;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Executed custom command {CommandId} ({CommandType}).")]
    private static partial void LogExecuted(ILogger logger, string commandId, CustomCommandType commandType);
}
