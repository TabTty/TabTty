using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TabTty.App;

/// <summary>Display text with native selection, keyboard copy, and UI Automation text ranges.</summary>
internal sealed class DialogText : TextBox
{
    internal DialogText(string text, double? contentHeight = null)
    {
        Text = text;
        IsReadOnly = true;
        IsUndoEnabled = false;
        AcceptsReturn = true;
        TextWrapping = TextWrapping.Wrap;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = contentHeight.HasValue ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (contentHeight is double height) MaxHeight = height;
        SetResourceReference(StyleProperty, contentHeight.HasValue ? "DialogLongText" : "DialogShortText");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DisplayPeer(this);

    private sealed class DisplayPeer(DialogText owner) : TextBoxAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? null : base.GetPattern(patternInterface);
    }
}
