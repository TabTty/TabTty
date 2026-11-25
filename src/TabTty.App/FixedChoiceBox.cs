using System.ComponentModel;
using System.Windows.Controls;

namespace TabTty.App;

internal sealed class FixedChoiceBox : ComboBox
{
    private Option[] options = [];

    internal FixedChoiceBox()
    {
        DisplayMemberPath = nameof(Option.Label);
        SelectedValuePath = nameof(Option.Id);
    }

    internal string? SelectedId
    {
        get => SelectedValue as string;
        set => SelectedValue = value;
    }

    internal void SetChoices(string[] ids, string[] labels, string? selection)
    {
        if (options.Select(option => option.Id).SequenceEqual(ids))
        {
            for (int index = 0; index < options.Length; index++) options[index].Relabel(labels[index]);
        }
        else
        {
            options = ids.Select((id, index) => new Option(id, labels[index])).ToArray();
            ItemsSource = options;
        }
        if (SelectedId != selection) SelectedId = selection;
    }

    private sealed class Option(string id, string label) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Label { get; private set; } = label;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Relabel(string value)
        {
            if (Label == value) return;
            Label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }
}
