using System.Windows;
using System.Windows.Input;

namespace DittoShell.App.Views;

/// <summary>通用单行文本输入对话框（新建/重命名分组用）。</summary>
public partial class TextInputDialog : Window
{
    public string Value => InputBox.Text;

    public TextInputDialog(string title, string initial = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        InputBox.Text = initial;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>显示对话框；取消返回 null。</summary>
    public static string? Prompt(Window? owner, string title, string initial = "")
    {
        var dlg = new TextInputDialog(title, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
