# 开发规格（10 份）

> 状态：**待用户确认** · 生成于 2026-11-15
> 这是 preflight 的最终产物。确认后即可进入编码阶段。

---

## 文档索引

| # | 文件 | 内容 | 什么时候看 |
|---|---|---|---|
| 01 | [decision-lock](01-decision-lock.md) | 50 条决策 + 已作废方案 | 想知道"为什么这么定" |
| 02 | [feature-matrix](02-feature-matrix.md) | 功能分级 P0/P1/P2/P3 + 验收标准 | 想知道"到底做哪些" |
| 03 | [architecture](03-architecture.md) | 4 层架构、目录结构、接口契约、线程模型 | 开始写代码前 |
| 04 | [data-spec](04-data-spec.md) | 只读/写入契约、sidecar schema、搜索方案 | 碰数据时 |
| 05 | [ui-spec](05-ui-spec.md) | 颜色 token、尺寸、状态、快捷键、动效 | 写界面时 |
| 06 | [security](06-security.md) | 威胁模型、信任边界、敏感识别、AI 边界 | 碰安全相关时 |
| 07 | [extensibility](07-extensibility.md) | AI 厂商/语言/主题 扩展点 | 想加东西时 |
| 08 | [edge-cases](08-edge-cases.md) | 47 条边界与异常 | 写每个功能前对照 |
| 09 | [test-spec](09-test-spec.md) | 性能基准、20 个单测、手动清单 | 验收时 |
| 10 | [risk-report](10-risk-report.md) | 17 条风险 + 应急预案 | 出问题时 |

---

## 关键结论速览

| 项 | 结论 |
|---|---|
| 技术栈 | WPF + .NET 8，单进程双窗口 |
| 搜索 | **LIKE 全表扫描**（FTS5 已因中文失效被否决） |
| 数据 | `Ditto.db` 只读；写仅 4 字段；sidecar 必须 UTF-16LE |
| 界面 | 三界面：快速面板 428×556 / 主界面 780×642 / Ditto 原界面（隐藏） |
| 视觉 | 浅色平铺，主色 `#1677FF`，品牌紫 `#722ED1` |
| AI | OpenAI 兼容，默认 `deepseek-flash`，默认关闭 |
| 许可证 | GPL v3 |

---

## 相关文件

- 原型：`docs/mockups/`（A2 主界面 / E 快速面板 / F 斜杠面板 / flows 三界面关系）
- 验证程序：`tools/verify-v2/`（WPF 视觉可行性）
- 决策记录：`.ai/03-决策记录.md`
- 技术勘察：`.ai/02-技术勘察.md`
