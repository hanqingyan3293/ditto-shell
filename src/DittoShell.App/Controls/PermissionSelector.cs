using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>
/// 权限选择器（照抄 DSH 的样子）。
/// 显示为：⚙ 完全权限 ▾
/// </summary>
public sealed class PermissionSelector : Button
{
    private AiPermissionLevel _current = AiPermissionLevel.Safe;

    public event Action<AiPermissionLevel>? LevelChanged;

    public AiPermissionLevel Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            UpdateLabel();
            LevelChanged?.Invoke(value);
        }
    }

    public PermissionSelector()
    {
        Height = 26;
        Padding = new Thickness(8, 0, 6, 0);
        FontSize = 11;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        Cursor = System.Windows.Input.Cursors.Hand;

        UpdateLabel();
        Click += (_, _) => ShowMenu();
    }

    private void UpdateLabel()
    {
        var (icon, text) = _current switch
        {
            AiPermissionLevel.ReadOnly => ("🔒", "只读"),
            AiPermissionLevel.FullAccess => ("⚡", "完全权限"),
            _ => ("🛡️", "安全")
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = icon, FontSize = 11, Margin = new Thickness(0, 0, 5, 0),
                                VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, FontSize = 11,
                                VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "▾", FontSize = 9, Margin = new Thickness(4, 0, 0, 0),
                                Opacity = 0.5,
                                VerticalAlignment = VerticalAlignment.Center }
            }
        };
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top
        };

        void Add(AiPermissionLevel level, string icon, string name, string desc)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"{icon} {name}",
                FontSize = 12,
                FontWeight = _current == level ? FontWeights.SemiBold : FontWeights.Normal
            });
            panel.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                Margin = new Thickness(0, 2, 0, 0)
            });

            var item = new MenuItem { Header = panel, Tag = level };
            item.Click += (_, _) => Current = level;
            menu.Items.Add(item);
        }

        Add(AiPermissionLevel.Safe, "🛡️", "安全", "改数据前弹确认（推荐）");
        Add(AiPermissionLevel.FullAccess, "⚡", "完全权限", "直接执行，不询问");
        menu.Items.Add(new Separator());
        Add(AiPermissionLevel.ReadOnly, "🔒", "只读", "只能查，不能改");

        menu.IsOpen = true;
    }
}
