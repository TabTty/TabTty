using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TabTty.App;

internal static partial class Program
{
    private static async Task CheckPopupAppearanceAsync(App app)
    {
#pragma warning disable WPF0001 // Exercise the same Fluent theme switch as Settings.
        var previousTheme = app.ThemeMode;
        var normal = new ComboBox();
        var fixedChoice = new FixedChoiceBox();
        fixedChoice.SetChoices(["system", "dark"], ["System", "Dark"], "system");
        var editable = new ComboBox { IsEditable = true, Text = "COM7" };
        var fields = new StackPanel { Children = { normal, fixedChoice, editable } };
        var window = new Window { Content = fields, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            app.ThemeMode = ThemeMode.Dark;
            window.Show();
            var dark = ChoiceColors();
            app.ThemeMode = ThemeMode.Light;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var light = ChoiceColors();
            if (light.Foreground == dark.Foreground || light.FixedPopup == dark.FixedPopup ||
                light.EditablePopup == dark.EditablePopup || fixedChoice.SelectedId != "system" || editable.Text != "COM7")
                throw new InvalidOperationException("The drop-downs did not apply the theme and retain their values.");

            (Color Foreground, Color FixedPopup, Color EditablePopup) ChoiceColors()
            {
                window.UpdateLayout();
                if (normal.Style is null || !ReferenceEquals(normal.Style, fixedChoice.Style) ||
                    !ReferenceEquals(normal.Template, fixedChoice.Template) ||
                    fixedChoice.Foreground is not SolidColorBrush foreground)
                    throw new InvalidOperationException("The fixed-choice drop-down did not use the themed ComboBox style.");
                return (foreground.Color, PopupBackground(fixedChoice), PopupBackground(editable));
            }

            var command = new MenuItem { Header = "Copy" };
            var focus = new MenuItem { Header = "Focus Terminal Pane", Items = { new MenuItem { Header = "Pane 1" } } };
            var split = new MenuItem { Header = "Split Terminal Pane" };
            var panes = new MenuItem { Header = "Terminal Panes", Items = { split, focus } };
            var menu = new ContextMenu { PlacementTarget = fields, Items = { command, panes } };
            fields.ContextMenu = menu;
            App.ApplyContextSubmenuTemplates(menu);

            menu.IsOpen = true;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                CheckAlignment(command, panes);
                panes.IsSubmenuOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                CheckAlignment(split, focus);
                double outerLeft = HeaderLeft(panes, panes);
                split.IsCheckable = true;
                split.IsChecked = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                CheckAlignment(split, focus);
                CheckAlignment(command, panes);
                if (Math.Abs(HeaderLeft(panes, panes) - outerLeft) > 1)
                    throw new InvalidOperationException("A nested checkmark changed the outer menu indentation.");
            }
            finally { menu.IsOpen = false; }
        }
        finally
        {
            window.Close();
            app.ThemeMode = previousTheme;
        }
#pragma warning restore WPF0001

        static Color PopupBackground(ComboBox choice)
        {
            if (choice.Template.FindName("PART_Popup", choice) is not Popup { Child: Border { Background: SolidColorBrush brush } })
                throw new InvalidOperationException("The drop-down did not have a themed popup background.");
            return brush.Color;
        }

        static void CheckAlignment(MenuItem command, MenuItem submenu)
        {
            double offset = HeaderLeft(submenu, submenu) - HeaderLeft(command, command);
            if (Math.Abs(offset) > 1)
                throw new InvalidOperationException($"The submenu label '{submenu.Header}' is offset by {offset:F1} DIPs.");
        }

        static double HeaderLeft(DependencyObject visual, MenuItem item)
        {
            if (visual is ContentPresenter { ContentSource: "Header" } label)
                return label.TranslatePoint(new Point(), item).X;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            {
                double offset = HeaderLeft(VisualTreeHelper.GetChild(visual, index), item);
                if (!double.IsNaN(offset)) return offset;
            }
            if (visual == item)
                throw new InvalidOperationException($"The menu label '{item.Header}' did not receive a visual.");
            return double.NaN;
        }
    }
}
