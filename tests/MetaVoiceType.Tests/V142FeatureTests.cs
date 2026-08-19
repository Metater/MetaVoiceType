using System.ComponentModel;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.VoiceCommands;

namespace MetaVoiceType.Tests;

public sealed class V142FeatureTests
{
    [Fact]
    public void FreshInstallAndDiagnosticDefaultsUseEnglishParakeet()
    {
        Assert.Equal(DictationMode.English, new AppSettings().DictationMode);
        Assert.Equal("en", StartupOptions.Parse([]).DictationLanguage);
    }

    [Fact]
    public void BuiltInCatalogIncludesPasteAndSendAndEnterForEveryLanguage()
    {
        Assert.Equal("pasteRecordingAndSend", VoiceCommandKeys.All[VoiceCommand.PasteRecordingAndSend]);
        Assert.Equal("sendEnter", VoiceCommandKeys.All[VoiceCommand.SendEnter]);
        Assert.All(VoiceCommandCatalog.LoadBundled().Languages, language =>
        {
            Assert.Contains("pasteRecordingAndSend", language.Commands.Keys);
            Assert.Contains("sendEnter", language.Commands.Keys);
        });
    }

    [Fact]
    public void CustomCommandNameAndPrimaryAliasNotifyEveryBoundListImmediately()
    {
        var command = new CustomVoiceCommand();
        var changes = new List<string?>();
        ((INotifyPropertyChanged)command).PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        command.Name = "Send report";
        command.Phrase = "send the report";

        Assert.Contains(nameof(CustomVoiceCommand.Name), changes);
        Assert.Contains(nameof(CustomVoiceCommand.Phrase), changes);
    }
}
