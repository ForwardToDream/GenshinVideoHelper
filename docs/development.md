# 开发

[文档目录](README.md) · [测试](testing.md) · [视觉流程](vision.md) · [协作约定](../AGENTS.md)

## 环境与运行

Windows 10 2004+ / Windows 11，64 位；.NET 8 SDK、PowerShell、Git。浏览器联调安装 Chrome 或 Edge；本地视频专项另需 FFmpeg。

```powershell
git clone https://github.com/ForwardToDream/GenshinVideoHelper.git
cd GenshinVideoHelper
dotnet build GenshinVideoHelper.sln -c Release
.\scripts\run.ps1
```

`start.cmd` 也可启动开发版。数据位于实际 exe 输出目录的 `.gvh/`，不取决于终端工作目录。

App 使用 WPF、Windows SDK 19041、Direct3D11；Infrastructure 使用 OpenCvSharp 和嵌入式 JavaScript。包版本见各项目的 `.csproj`。

## 分层

| 目录 | 职责 |
| --- | --- |
| `src/GenshinVideoHelper.Core` | 模型、契约、应用流程、进度规则；不依赖平台实现 |
| `src/GenshinVideoHelper.Infrastructure` | Chromium / CDP、B 站接口、JSON 存储、日志、OpenCV |
| `src/GenshinVideoHelper.App` | WPF 界面、原生窗口、快捷键、托盘、WGC / DWM |
| `tests/GenshinVideoHelper.Core.Tests`、`tests/GenshinVideoHelper.Infrastructure.Tests` | 分层单元测试与离线视觉回归 |
| `tests/GenshinVideoHelper.Smoke` | 浏览器、桌面、联调与性能专项 |
| `tests/Fixtures/Minimap` | 入库小样本与可选本地完整样本 |
| `scripts` | 启动、测试、发布 |
| `docs` | 可随仓库读取的开发说明 |
| `reference`、`experiments`、`artifacts` | 本地参考、实验、产物；Git 忽略 |

依赖：**App → Infrastructure → Core**。外部能力通过 Core 契约进入应用流程；实例装配和所有权在 [AppServices](../src/GenshinVideoHelper.App/Composition/AppServices.cs)。界面当前使用 `Views/MainWindow.*` 分文件承接交互。

## 开发约定

公共规则放根目录，局部规则放模块目录，适用于该目录及子目录；有冲突时更深层规则优先。只在存在独立约束的地方放文件。

| AGENTS.md | 范围 |
| --- | --- |
| [根目录](../AGENTS.md) | 工作方式、验证、文档与许可 |
| [src](../src/AGENTS.md) | 分层、并发与资源所有权 |
| [Core](../src/GenshinVideoHelper.Core/AGENTS.md) | 身份、进度、地图调度 |
| [Infrastructure](../src/GenshinVideoHelper.Infrastructure/AGENTS.md) | 浏览器会话、存储与日志 |
| [Vision](../src/GenshinVideoHelper.Infrastructure/Vision/AGENTS.md) | arrow_first、ORB 与失配行为 |
| [App](../src/GenshinVideoHelper.App/AGENTS.md) | 面板、浮窗、快捷键、退出 |
| [tests](../tests/AGENTS.md) | 固定样本、独立配置与验证边界 |
| [scripts](../scripts/AGENTS.md) | 打包、数据保留与安全清理 |
| [docs](AGENTS.md) | 文档分工、来源与链接 |

Codex 启动时按仓库根到当前工作目录合并指令，不预加载所有子目录；从根目录改模块时须先读取目标路径上的约定。[OpenAI 文档](https://learn.chatgpt.com/docs/agent-configuration/agents-md)建议保持规则简短，将模块规则放到相关目录。

## 跟随与浏览器

| 入口 | 责任 |
| --- | --- |
| [FollowCoordinator](../src/GenshinVideoHelper.Core/Application/FollowCoordinator.cs) | 打开、换集、媒体就绪、播放控制、自动画中画、进度采样 |
| [FollowSession](../src/GenshinVideoHelper.Core/Application/FollowSession.cs) | 请求版本、取消、身份、自动流程抑制 |
| [EpisodePreviewService](../src/GenshinVideoHelper.Core/Application/EpisodePreviewService.cs) / `EpisodeCache` | 开始前读取分集，复用元数据 |
| [BrowserSessionManager](../src/GenshinVideoHelper.Infrastructure/Browser/BrowserSessionManager.cs) | 浏览器选择、当前会话、旧会话清理 |
| [ChromiumBrowser](../src/GenshinVideoHelper.Infrastructure/Browser/ChromiumBrowser.cs) | 专用进程、唯一视频标签页、后台导航、进程归属 |
| [VideoController](../src/GenshinVideoHelper.Infrastructure/Browser/VideoController.cs) | 媒体命令、事件、账号与实际解码画质 |

1. 预选视频只更新地址和分集预览，保持当前播放。
2. 开始跟随核实浏览器安装，应用保存的选择；同浏览器复用唯一视频页，换视频导航该页。
3. 等待身份与媒体就绪，分别执行 `ensurePlay`、`ensurePip`，恢复浮窗布局。
4. 换 P 使用当前会话，重复选择当前 P 不重播；网站连播由媒体状态同步。
5. 新请求取消旧请求；异步结果校验版本与身份，旧事件不能覆盖新选择。

身份是 **BV + P**；CID 用 `long`。分集优先取页面元数据，缺失时取 B 站接口；地址的来源参数不参与选择。

安装检测检查 App Paths 注册表及系统、用户安装目录，过滤无效路径。自动选择 Chrome > Edge，只依据安装状态；网络或视频失败不会换浏览器。设置变更在下次开始跟随生效，当前换集、登录仍用当前会话。

Chrome 与 Edge 共用 CDP、媒体命令、登录检查、高清采集及浮窗实现，分别使用 `.gvh/Chrome`、`.gvh/Edge`。启动只预热；跟随窗口最小化加载，不抢前台。连接复用，媒体事件驱动刷新，轮询承担补偿。

切换浏览器先撤销旧浮窗、标记与请求，异步清理旧专用进程。快速切回同一浏览器须等待其旧配置会话退出。创建标签页须保留已创建的目标 ID，取消响应不能遗留额外视频页。

账号检查异步运行：明确匿名才提示；用户选择“去登录”后才激活专用浏览器。申请 1080p 后按实际解码尺寸确认结果，换 P 不重复提示。

## 浮窗、快捷键与退出

- 一个 WPF 蓝色外框承载浏览器画中画；DWM 合成实时画面，拖动、缩放操作作用于整个外框。
- 完整外框的位置、尺寸使用屏幕物理像素保存，恢复时限制在可见工作区；程序定位与显隐不覆盖用户布局。
- 三种显隐模式整体作用于视频和横条；鼠标避让使用扩展 20% 的范围。快捷键单次触发，长按不循环。
- 手动关闭浮窗抑制当前自动流程；显隐只是窗口可见性，不改变播放状态。
- 热键按项注册修饰键与主键，报告占用，保留成功项，退出释放。默认绑定见 [用户说明](../README.md#快捷键)。
- 最小化到托盘；退出先隐藏界面，并行刷新数据与清理专用浏览器，预算 2.5 秒。终止进程必须有保留的进程身份或配置目录验证，不能按进程名批量关闭。

## 进度

[ProgressTracker](../src/GenshinVideoHelper.Core/Progress/ProgressTracker.cs) 只累计通过身份校验的连续播放采样，保存每集已看区间的并集。跳转、暂停、断连及身份变更切断采样连续性。

[WatchProgressService](../src/GenshinVideoHelper.Core/Progress/WatchProgressService.cs) 管覆盖率、续播点、手动状态、批量操作与撤销。默认覆盖率 90% 才自动完成；不能按播放头、后续分集或最大 P 推断已看内容。手动完成 / 未完成优先，恢复自动判定后才由覆盖率决定。

## 数据与默认值

统一使用 [ApplicationPaths](../src/GenshinVideoHelper.Infrastructure/Settings/ApplicationPaths.cs)，根目录为 `AppContext.BaseDirectory/.gvh`。

| 路径 | 内容 |
| --- | --- |
| `config.json` | `AppSettings`、热键、库选择、最近分 P 链接、浮窗布局 |
| `progress.json` | 每集已看区间、续播点、手动状态 |
| `Chrome/`、`Edge/` | 各浏览器专用配置与登录状态 |
| `logs/app.log` | 应用日志；1 MB 滚动，当前文件加 3 个旧文件 |

配置与进度使用临时文件替换写入。坏配置回退默认值，保存前备份原文件；进度有上一份备份及坏文件保留机制。不要在 UI 里拼路径或自行写 JSON。

| 配置字段 | 默认 / 范围 |
| --- | --- |
| `BrowserPreference` | `Auto`；可选 `Chrome`、`Edge` |
| `SelectedVideoLibraryId` | `hanqing-zero-start`；`null` 表示手动模式 |
| `VideoUrl` | 空；记录最近选择，不是独立“默认视频”功能 |
| `SeekSeconds` | 5；归一化到 1–60 |
| `PipWidth`、`PipMargin` | 420 / 20；280–800 / 0–100 |
| `PipPlacement` | 空；用户移动、缩放后保存完整外框布局 |
| `HotkeysEnabled`、`Hotkeys` | 开启；按动作保存组合键字符串 |
| `LogLevel` | `Info`；诊断可改为 `Debug` |
| `ProgressCompletionPercent` | 90；50–100 |
| `Vision.Enabled`、`Vision.ShowDirection` | 开启 / 开启 |
| `Vision.IntervalMs` | 100；50–200 |
| `Vision.MarkerSize` | 5；3–9 |

以上是存储模型范围；界面提供的选项可更少。倍速作用于当前播放器，不是独立保存字段。枚举按字符串序列化，缺字段使用默认值，保存前校验绑定与设置。

日志统一走 `AppLog` → `FileLogSink`：有界队列、独立写线程、重复合并、按大小滚动。轮询只记录变化，不记录 Cookie 或页面正文。

## 增加视频库

在 [Library/Data](../src/GenshinVideoHelper.Infrastructure/Library/Data) 复制现有 JSON，修改 `Id`、`Name`、`CreatorName`、`CreatorUrl`、`Description`、`Videos`。

- `Id` 只含小写字母、数字、连字符，库间唯一。
- UP 链接使用 `https://space.bilibili.com/数字/`。
- 每项提供 `Number`、`Title`、`Bvid`，序号为正，序号和 BV 均不重复。
- 文件自动嵌入程序集，按序号排序；新库补解析测试。启动页选库、搜索和视频选择沿用现有流程。

## 发布与提交

本地打包与 GitHub 发布见 [Release](release.md)，按改动运行 [专项测试](testing.md)。

提交只包含源码、文档和固定小样本。排除用户配置、浏览器目录、日志、录像、完整样本及发布产物。修改行为时同步用户说明、相关开发文档与 [AGENTS.md](../AGENTS.md)。许可证见 [GPL-3.0](../LICENSE)。
