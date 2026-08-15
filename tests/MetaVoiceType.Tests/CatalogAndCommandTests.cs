using System.Text.Json;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Models;
using MetaVoiceType.VoiceCommands;

namespace MetaVoiceType.Tests;

public sealed class CatalogAndCommandTests
{
    [Fact]
    public void VoskManagedGrammarIsUsedOnlyForAsciiCommandPhrases()
    {
        var ascii = VoiceCommandKeys.All.Keys.ToDictionary(command => command, _ => "start recording");
        var cyrillic = VoiceCommandKeys.All.Keys.ToDictionary(command => command, _ => "начать запись");

        Assert.True(VoskCommandRecognizer.SupportsManagedGrammar(ascii));
        Assert.False(VoskCommandRecognizer.SupportsManagedGrammar(cyrillic));
    }

    [Fact]
    public void VoiceCatalogContainsExactlySpecifiedLanguageSet()
    {
        VoiceCommandCatalog catalog = VoiceCommandCatalog.LoadBundled();
        string[] expected = ["en-us", "en-in", "zh-cn", "ru", "fr", "de", "es", "pt-br", "tr", "vi", "it", "nl", "ca", "fa", "ar-tn", "kk", "uk", "sv", "ja", "eo", "hi", "cs", "pl", "uz", "ko", "gu", "tg", "te", "ky", "ka"];
        Assert.Equal(expected.Order(), catalog.Languages.Select(x => x.Id).Order());
        Assert.Equal("vosk-model-uk-v3", catalog.Get("uk").ModelName);
        Assert.All(catalog.Languages, language => Assert.True(language.SizeBytes > 0));
        Assert.DoesNotContain(catalog.Languages, x => x.Id is "fil" or "ar");
    }

    [Fact]
    public void NemotronCatalogHasVerifiedArtifactMetadataAndNoRuntimeProvider()
    {
        ModelCatalog catalog = ModelCatalog.LoadBundled();
        Assert.Equal("auto", catalog.Nemotron.DefaultLanguage);
        Assert.Equal("OpenMDW-1.1", catalog.Nemotron.License);
        Assert.Equal(64, catalog.Nemotron.ArchiveSha256.Length);
        Assert.Equal(475271763, catalog.Nemotron.EstimatedDownloadBytes);
        Assert.Equal(19, catalog.Nemotron.Languages.TranscriptionReady.Count);
        Assert.Equal(13, catalog.Nemotron.Languages.BroadCoverage.Count);
        Assert.Equal(8, catalog.Nemotron.Languages.AdaptationReady.Count);
        string json = File.ReadAllText(Path.Combine(FindRoot(), "src", "MetaVoiceType", "Resources", "model-catalog.json"));
        Assert.DoesNotContain("acceleration", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatcherIgnoresConfidenceAndUsesAlternativeOrder()
    {
        var phrases = new Dictionary<VoiceCommand, string> { [VoiceCommand.StartRecording] = "start recording", [VoiceCommand.PasteHere] = "paste here" };
        string json = JsonSerializer.Serialize(new { alternatives = new[] { new { text = "start recording", confidence = 0.01 }, new { text = "paste here", confidence = 0.99 } } });
        VoiceCommandMatch match = Assert.Single(VoskResultMatcher.Match(json, phrases));
        Assert.Equal(VoiceCommand.StartRecording, match.Command);
    }

    [Fact]
    public void MatcherEmitsCommandsInSpokenOrderAndPrefersLongerPhrase()
    {
        var phrases = new Dictionary<VoiceCommand, string> { [VoiceCommand.StopRecording] = "stop", [VoiceCommand.CancelRecording] = "stop recording", [VoiceCommand.PasteHere] = "paste here" };
        const string json = "{\"text\":\"please stop recording then paste here\"}";
        IReadOnlyList<VoiceCommandMatch> matches = VoskResultMatcher.Match(json, phrases);
        Assert.Equal([VoiceCommand.CancelRecording, VoiceCommand.PasteHere], matches.Select(x => x.Command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[unk]")]
    public void InvalidCommandPhraseRejected(string invalid)
    {
        VoiceCommandLanguage language = VoiceCommandCatalog.LoadBundled().Get("en-us");
        var phrases = language.Commands.ToDictionary();
        phrases["startRecording"] = invalid;
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.Validate(phrases));
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
