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
        string[] expected = ["en-us", "ru", "fr", "de", "es", "pt-br", "it", "nl", "uk", "sv", "cs", "pl"];
        Assert.Equal(expected.Order(), catalog.Languages.Select(x => x.Id).Order());
        Assert.Equal("vosk-model-uk-v3", catalog.Get("uk").ModelName);
        Assert.All(catalog.Languages, language => Assert.True(language.SizeBytes > 0));
        Assert.DoesNotContain(catalog.Languages, x => x.Id is "fil" or "ar");
    }

    [Fact]
    public void ParakeetCatalogHasVerifiedArtifactMetadataAndNoRuntimeProvider()
    {
        ModelCatalog catalog = ModelCatalog.LoadBundled();
        ModelArtifact v2 = catalog.Get("parakeet-v2");
        ModelArtifact v3 = catalog.Get("parakeet-v3");
        Assert.Equal("en", v2.DefaultLanguage);
        Assert.Equal("auto", v3.DefaultLanguage);
        Assert.Equal("CC-BY-4.0", v3.License);
        Assert.Equal("5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf", v3.ArchiveSha256);
        Assert.Equal(487170055, v3.EstimatedDownloadBytes);
        Assert.True(v3.Capabilities!.AutomaticLanguageDetection);
        Assert.Equal(25, v3.Capabilities.Languages!.Count);
        Assert.Equal(["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], v3.RequiredFiles);
        string json = File.ReadAllText(Path.Combine(FindRoot(), "src", "MetaVoiceType", "Resources", "model-catalog.json"));
        Assert.DoesNotContain("nemotron", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"acceleration\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"provider\"", json, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void MatcherMapsWordTimestampsToGlobalAudioSamplesWithoutUsingConfidence()
    {
        var phrases = new Dictionary<VoiceCommand, string> { [VoiceCommand.StopRecording] = "stop recording" };
        const string low = "{\"text\":\"stop recording\",\"confidence\":0.0001,\"result\":[{\"word\":\"stop\",\"start\":1.25,\"end\":1.5},{\"word\":\"recording\",\"start\":1.55,\"end\":2.0}]}";
        VoiceCommandMatch match = Assert.Single(VoskResultMatcher.Match(low, phrases, 32_000));
        Assert.Equal(52_000, match.AudioStartSample);
        Assert.Equal(64_000, match.AudioEndSample);
        Assert.Equal(0.0001, match.Confidence);
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
