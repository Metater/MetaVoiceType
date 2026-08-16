using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.UI.ViewModels;

public partial class AliasEntryViewModel(string value = "") : ObservableObject
{
    [ObservableProperty] public partial string Value { get; set; } = value;
}

public partial class AliasListEditorViewModel : ObservableObject
{
    public AliasListEditorViewModel(IEnumerable<string>? values = null)
    {
        foreach (string value in values ?? []) AddValue(value);
    }

    public ObservableCollection<AliasEntryViewModel> Items { get; } = [];
    public event EventHandler? Changed;
    public IReadOnlyList<string> Values => Items.Select(x => x.Value).ToArray();

    public void Replace(IEnumerable<string> values)
    {
        Items.Clear();
        foreach (string value in values) AddValue(value);
        OnPropertyChanged(nameof(Values));
    }

    private void AddValue(string value)
    {
        var item = new AliasEntryViewModel(value);
        item.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Values)); Changed?.Invoke(this, EventArgs.Empty); };
        Items.Add(item);
    }

    [RelayCommand]
    private void Add()
    {
        AddValue("");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Remove(AliasEntryViewModel item)
    {
        if (Items.Count <= 1) { item.Value = ""; return; }
        Items.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CommandAliasEditorViewModel
{
    public CommandAliasEditorViewModel(VoiceCommand command, string label)
    {
        Command = command; Label = label;
    }
    public VoiceCommand Command { get; }
    public string Label { get; }
    public AliasListEditorViewModel Aliases { get; } = new();
}

public partial class ReplacementGroupEditorViewModel : ObservableObject
{
    public ReplacementGroupEditorViewModel(WordReplacementGroup group)
    {
        Id = group.Id; Replacement = group.Replacement; Matches.Replace(group.Matches);
    }
    public string Id { get; }
    [ObservableProperty] public partial string Replacement { get; set; }
    public AliasListEditorViewModel Matches { get; } = new();
    public WordReplacementGroup ToModel() => new() { Id = Id, Replacement = Replacement, Matches = Matches.Values.ToList() };
}
