using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using static TabTty.App.App;

namespace TabTty.App;

/// <summary>A named dialog value, with spacing after the whole field.</summary>
internal sealed class DialogField : StackPanel
{
    internal const double Spacing = 16;
    private readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap };

    internal DialogField(string caption, FrameworkElement value, FrameworkElement? labelTarget = null, bool inline = false)
    {
        Margin = new Thickness(0, 0, 0, Spacing);
        SetCaption(caption);
        Panel content = this;
        if (inline)
        {
            this.caption.Margin = new Thickness(0, 0, 4, 0);
            content = new WrapPanel();
            Children.Add(content);
        }
        content.Children.Add(this.caption);
        content.Children.Add(value);
        labelTarget ??= value;
        AutomationProperties.SetLabeledBy(labelTarget, this.caption);
        SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = labelTarget });
    }

    internal void SetCaption(string name) => caption.Text = string.Format(Text("DialogFieldCaption"), name);
}
