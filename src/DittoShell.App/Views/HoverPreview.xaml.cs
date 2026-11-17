using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DittoShell.Domain.Models;

namespace DittoShell.App.Views;

/// <summary>鼠标悬停 ~600ms 后弹出的信息卡片：大图 + 文本前几行 + 元信息。</summary>
public partial class HoverPreview : UserControl
{
    public HoverPreview()
    {
        InitializeComponent();
    }

    public void Show(ClipDetail detail, BitmapSource? image, Brush kindBrush, string kindLabel, string timeText)
    {
        KindText.Text = kindLabel;
        KindBadge.Background = kindBrush;

        var text = detail.FullText ?? "";
        bool hasText = !string.IsNullOrWhiteSpace(text) && text != "CF_DIB";

        if (!hasText)
        {
            BodyHost.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            BodyHost.Visibility = System.Windows.Visibility.Visible;
            BodyText.Text = text.Length > 400 ? text[..400] + " …" : text;
        }

        var size = detail.SizeBytes >= 1_048_576
            ? $"{detail.SizeBytes / 1_048_576.0:F1} MB"
            : $"{detail.SizeBytes:N0} B";
        var formats = detail.Formats.Count > 0 ? string.Join(" · ", detail.Formats) : "—";
        MetaText.Text = $"{timeText} · {size} · {formats}";

        if (image is not null)
        {
            PreviewImage.Source = image;
            ImageHost.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            PreviewImage.Source = null;
            ImageHost.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}
