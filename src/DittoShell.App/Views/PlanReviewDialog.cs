using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>计划评审对话框。</summary>
public sealed class PlanReviewDialog : Window
{
    public PlanModeSupport.PlanDecision Decision { get; private set; }
        = PlanModeSupport.PlanDecision.Cancel;

    public string? Feedback { get; private set; }

    public PlanReviewDialog(string plan)
    {
        Title = "执行计划";
        Width = 680;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ResizeMode = ResizeMode.CanResize;

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        title.Children.Add(new TextBlock
        {
            Text = "📋",
            FontSize = 18,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new TextBlock
        {
            Text = "AI 的执行计划",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        // 计划内容
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var planBox = new TextBox
        {
            Text = plan ?? "",
            IsReadOnly = true,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true
        };
        scroll.Content = planBox;
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        // 反馈输入
        var feedbackPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        feedbackPanel.Children.Add(new TextBlock
        {
            Text = "修改意见（点「继续规划」时会发给 AI）",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var feedbackBox = new TextBox
        {
            Height = 60,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        feedbackPanel.Children.Add(feedbackBox);
        Grid.SetRow(feedbackPanel, 2);
        grid.Children.Add(feedbackPanel);

        // 按钮
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 32, IsCancel = true };
        cancelBtn.Click += (_, _) => { Decision = PlanModeSupport.PlanDecision.Cancel; Close(); };

        var reviseBtn = new Button
        {
            Content = "继续规划",
            Width = 100,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        reviseBtn.Click += (_, _) =>
        {
            Decision = PlanModeSupport.PlanDecision.Revise;
            Feedback = feedbackBox.Text?.Trim();
            Close();
        };

        var approveBtn = new Button
        {
            Content = "批准并执行",
            Width = 110,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };
        approveBtn.Click += (_, _) => { Decision = PlanModeSupport.PlanDecision.Approve; Close(); };

        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(reviseBtn);
        buttons.Children.Add(approveBtn);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        Content = grid;
    }
}
