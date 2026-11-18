using System.Windows;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>
/// AI 抽屉的确认与评审接线：
/// 1) 安全模式的写操作确认
/// 2) 计划模式的计划评审
/// </summary>
public partial class AiDrawerView
{
    /// <summary>本次会话已「不再询问」的工具。</summary>
    private readonly HashSet<string> _trustedTools = new();

    private void InitConfirmAndReview()
    {
        try
        {
            var tools = App.ServiceProvider?.GetService(typeof(AiTools)) as AiTools;
            if (tools is null) return;

            // ── 写操作确认 ──
            tools.OnConfirmWrite = async req =>
            {
                // 完全模式：直接允许
                if (tools.Permission == AiPermissionLevel.FullAccess) return true;

                // 已信任这个工具
                if (_trustedTools.Contains(req.Tool)) return true;

                // 弹窗（在 UI 线程）
                return await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new WriteConfirmDialog(
                        req.Tool,
                        BuildWriteSummary(req),
                        BuildWriteDetail(req))
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dlg.ShowDialog();

                    if (dlg.Approved && dlg.RememberChoice)
                        _trustedTools.Add(req.Tool);

                    return dlg.Approved;
                });
            };

            // ── 计划评审 ──
            tools.OnPlanReview = async plan =>
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new PlanReviewDialog(plan)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dlg.ShowDialog();

                    var approved = dlg.Decision == PlanModeSupport.PlanDecision.Approve;

                    if (approved || dlg.Decision == PlanModeSupport.PlanDecision.Cancel)
                    {
                        // 批准或取消都关掉计划模式
                        SetPlanMode(false);
                    }

                    return (approved, dlg.Feedback);
                });
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("InitConfirmAndReview: " + ex.Message);
        }
    }

    // ═══════ 文案构建 ═══════

    private static string BuildWriteSummary(AiTools.WriteRequest req) => req.Tool switch
    {
        "add_tag" => $"给 {req.Ids.Count} 条加标签「{req.Tag}」",
        "mark_private" => $"把 {req.Ids.Count} 条标记为隐私（AI 将看不到）",
        "move_to_group" => $"把 {req.Ids.Count} 条移到分组「{req.Group}」",
        "export" => $"导出 {req.Ids.Count} 条到文件",
        _ => $"执行 {req.Tool}（影响 {req.Ids.Count} 条）"
    };

    private static string BuildWriteDetail(AiTools.WriteRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"工具：{req.Tool}");
        sb.AppendLine($"条目数：{req.Ids.Count}");
        sb.AppendLine();

        if (req.Ids.Count > 0)
        {
            sb.AppendLine("涉及的条目 ID：");
            var shown = req.Ids.Take(20).ToList();
            sb.AppendLine(string.Join(", ", shown.Select(id => "#" + id)));
            if (req.Ids.Count > 20)
                sb.AppendLine($"…还有 {req.Ids.Count - 20} 条");
        }

        sb.AppendLine();
        sb.AppendLine("这个操作：");
        switch (req.Tool)
        {
            case "add_tag":
                sb.AppendLine("· 只加标签，不改内容、不改分组");
                sb.AppendLine("· 可以随时删标签");
                break;
            case "mark_private":
                sb.AppendLine("· 标记后 AI 将看不到这些条目");
                sb.AppendLine("· 列表里会打码显示");
                sb.AppendLine("· 可以随时取消");
                break;
            case "move_to_group":
                sb.AppendLine("· 修改条目的所属分组");
                sb.AppendLine("· 不删除任何内容");
                sb.AppendLine("· 可以再移回原分组");
                break;
            case "export":
                sb.AppendLine("· 只读取内容写文件，不改数据库");
                break;
        }

        return sb.ToString();
    }
}
