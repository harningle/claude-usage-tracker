using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ClaudeUsageTracker.App.Views;

/// <summary>
/// Upstream <c>SettingToggle</c>: a title, an optional description and a trailing switch. The switch is a
/// check box, so it keeps keyboard access, and its accessible name is the title.
/// </summary>
public sealed class SettingToggle : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingToggle), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingToggle), new PropertyMetadata(null));

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(SettingToggle),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public SettingToggle()
    {
        Focusable = false;
        var title = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(StyleProperty, "Text.Base");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        var description = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        description.SetResourceReference(StyleProperty, "Text.Secondary");
        description.SetBinding(TextBlock.TextProperty, new Binding(nameof(Description)) { Source = this });
        // A toggle without a description takes one line, as upstream's does.
        description.SetBinding(VisibilityProperty, new Binding(nameof(Description)) { Source = this, Converter = Converters.NotNullToVisible });
        var text = new StackPanel { Children = { title, description } };
        var toggle = new CheckBox { Margin = new Thickness(16, 0, 0, 0) };
        toggle.SetResourceReference(StyleProperty, "Switch");
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(IsOn)) { Source = this, Mode = BindingMode.TwoWay });
        toggle.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Title)) { Source = this });
        toggle.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(Description)) { Source = this });
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        Content = grid;
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
}
