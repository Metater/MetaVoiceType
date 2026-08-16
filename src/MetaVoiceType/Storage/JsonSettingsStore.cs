using System.Text.Json;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Storage;

public sealed partial class JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger) : ISettingsStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return new AppSettings();
            AppSettings loaded;
            await using (var stream = File.OpenRead(paths.SettingsFile))
                loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, AtomicJsonFile.Options, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            AppSettings migrated = Migrate(loaded);
            if (loaded.SchemaVersion != migrated.SchemaVersion) await AtomicJsonFile.WriteAsync(paths.SettingsFile, migrated, cancellationToken).ConfigureAwait(false);
            return migrated;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogUnreadable(logger, ex);
            return new AppSettings();
        }
        finally { _gate.Release(); }
    }

    internal static AppSettings Migrate(AppSettings settings)
    {
        var aliases = settings.CommandAliases ?? new(StringComparer.OrdinalIgnoreCase);
        foreach ((string language, Dictionary<string, string> commands) in settings.CommandOverrides ?? [])
        {
            if (!aliases.TryGetValue(language, out Dictionary<string, List<string>>? languageAliases))
                aliases[language] = languageAliases = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string command, string phrase) in commands)
                if (!languageAliases.ContainsKey(command) && !string.IsNullOrWhiteSpace(phrase))
                    languageAliases[command] = [phrase];
        }

        List<CustomVoiceCommand> customCommands = settings.CustomCommands ?? [];
        foreach (CustomVoiceCommand command in customCommands)
        {
            command.Aliases ??= [];
            if (command.Aliases.Count == 0 && !string.IsNullOrWhiteSpace(command.Phrase)) command.Aliases.Add(command.Phrase);
            command.Aliases = NormalizeAliases(command.Aliases);
            command.Phrase = command.Aliases.FirstOrDefault() ?? "";
        }

        List<WordReplacementGroup> groups = settings.WordReplacementGroups ?? [];
        foreach (IGrouping<string, WordReplacement> legacyGroup in (settings.WordReplacements ?? [])
                     .Where(x => !string.IsNullOrWhiteSpace(x.Match)).GroupBy(x => x.Replacement, StringComparer.Ordinal))
        {
            WordReplacementGroup? existing = groups.FirstOrDefault(x => x.Replacement.Equals(legacyGroup.Key, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new WordReplacementGroup { Id = legacyGroup.First().Id, Replacement = legacyGroup.Key };
                groups.Add(existing);
            }
            existing.Matches.AddRange(legacyGroup.Select(x => x.Match));
        }
        foreach (WordReplacementGroup group in groups)
        {
            group.Matches ??= [];
            group.Matches = NormalizeAliases(group.Matches);
        }

        return settings with
        {
            SchemaVersion = 4,
            CommandOverrides = new(StringComparer.OrdinalIgnoreCase),
            CommandAliases = aliases,
            CustomCommands = customCommands,
            WordReplacements = [],
            WordReplacementGroups = groups
        };
    }

    private static List<string> NormalizeAliases(IEnumerable<string> values) => values
        .Select(CommandPhraseValidator.Normalize)
        .Where(x => x.Length > 0 && x != "[unk]")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicJsonFile.WriteAsync(paths.SettingsFile, settings, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings were unreadable; defaults will be used.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}
