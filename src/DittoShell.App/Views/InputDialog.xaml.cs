using System.Windows;

namespace DittoShell.App.Views;

/// <summary>简单的文本输入对话框。</summary>
public partial class InputDialog : Window
{
    public string Value { get; private set; } = "";

    public InputDialog(string title, string initial)
    {
        Title = title;
        Width = 380;
        Height = 160;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var label = new System.Windows.Controls.TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12
        };
        System.Windows.Controls.Grid.SetRow(label, 0);
        grid.Children.Add(label);

        var box = new System.Windows.Controls.TextBox
        {
            Text = initial,
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var panel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var ok = new System.Windows.Controls.Button
        {
            Content = "确定",
            Width = 74,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        ok.Click += (_, _) => { Value = box.Text.Trim(); DialogResult = true; };

        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 74,
            Height = 28,
            IsCancel = true
        };

        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(panel, 2);
        grid.Children.Add(panel);

        Content = grid;
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
