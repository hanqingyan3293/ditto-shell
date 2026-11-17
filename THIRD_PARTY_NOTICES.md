# Ditto Shell 第三方组件与许可证

本项目以 **GPL v3** 发布。以下是内置或依赖的第三方组件。

---

## 运行时依赖

| 组件 | 许可证 | 用途 |
|---|---|---|
| .NET 8 / WPF | MIT | 应用运行时与界面框架 |
| Microsoft.Data.Sqlite | MIT | 只读访问 Ditto.db，读写 sidecar.db |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | SQLite 原生绑定 |
| CommunityToolkit.Mvvm | MIT | MVVM 源生成器 |
| Microsoft.Extensions.DependencyInjection | MIT | 依赖注入 |
| Microsoft.Extensions.Configuration.Json | MIT | 配置读取 |
| **HtmlRenderer.WPF** | **MIT** | 预览区渲染 HTML 富文本（1.6.1，约 500KB） |

## 图标

| 组件 | 许可证 | 用途 |
|---|---|---|
| **Lucide Icons** | **ISC** | 界面图标（55 个，已转成 XAML 几何数据内置） |

Lucide 许可证原文：<https://github.com/lucide-icons/lucide/blob/main/LICENSE>
ISC 许可证允许商业使用、修改、分发，要求保留版权声明。

## 设计参考

- 界面视觉参考 [snow-shot](https://github.com/mg-chao/snow-shot)（GPL-3.0-or-later）的浅色平铺风格与 Ant Design 设计体系。
- **未复制任何 snow-shot 的代码**，仅参考其设计规范（颜色、间距、圆角）。

## 交互对象

- [Ditto](https://github.com/sabrogden/Ditto)（GPL v3）—— 本项目**不修改、不链接、不分发** Ditto，
  仅通过命令行（`/paste`）、窗口消息（`WM_USER+n`）和只读数据库文件与其交互。
  用户需自行安装 Ditto。

---

## 字体

界面使用系统字体，不打包字体文件：

- 中文：Microsoft YaHei UI（Windows 自带）
- 英文：Segoe UI（Windows 自带）
- 等宽：Cascadia Code / Consolas（Windows 自带）
