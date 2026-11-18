using System.Text.Json;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>计划模式支持：exit_plan_mode 工具 + 提示词注入。</summary>
public static class PlanModeSupport
{
    /// <summary>计划模式下注入的提示词段。</summary>
    public const string PolicySection = """
【当前是计划模式】

你只能调用只读工具（get_context / search / search_regex / filter / sort / page /
get_detail / get_details / list_groups / list_tags / list_apps / count /
group_by / timeline / find_similar / detect_sensitive）。

写工具（organize / move_to_group / add_tag / mark_private / export）已被系统禁用，
调用会返回「计划模式禁止」。

工作方式：
1. 先充分探索数据（该查几次就查几次，不要急着下结论）
2. 想清楚执行方案：要改什么、影响多少条、有没有风险
3. 调用 exit_plan_mode 提交完整计划
4. 用户批准后才开始执行

计划应包含：
- 现状（你查到了什么）
- 方案（分几步，每步做什么）
- 影响（会改多少条，有没有不可逆操作）
- 风险（可能出问题的地方，以及怎么避免）
""";

    /// <summary>exit_plan_mode 的工具定义。</summary>
    public static AgentTool Definition => new()
    {
        Name = "exit_plan_mode",
        Description = "提交执行计划给用户评审。计划模式下必须调这个才能结束规划。",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "plan": {
              "type": "string",
              "description": "完整的执行计划（Markdown 格式），包含现状、方案、影响、风险"
            }
          },
          "required": ["plan"]
        }
        """
    };

    /// <summary>把计划交给用户评审。返回用户的决定。</summary>
    public sealed class ReviewRequest
    {
        public string Plan { get; init; } = "";
        public TaskCompletionSource<PlanDecision> Completion { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public enum PlanDecision
    {
        /// <summary>批准，开始执行。</summary>
        Approve,

        /// <summary>继续规划（带反馈）。</summary>
        Revise,

        /// <summary>取消。</summary>
        Cancel
    }

    /// <summary>评审结果（含用户反馈）。</summary>
    public sealed class ReviewResult
    {
        public PlanDecision Decision { get; init; }
        public string? Feedback { get; init; }
    }
}
