# GenshinVideoHelper

Windows 原神 B站攻略跟随助手：分集选择、浏览器画中画和全局快捷键。

## 使用

需要 Windows 10 2004 或更高版本 / Windows 11（64 位）和 Chrome 或 Edge；发布版自带 .NET 运行时。

双击 `GenshinVideoHelper.exe`（开发目录也可用 `start.cmd`） → 选库或输入链接 → 选分集 → 开始跟随。再次选中同一张地图时会定位到第一个未完成的分集，并从上次停下的位置继续。

- 设置页选择自动、Chrome 或 Edge，自动优先使用 Chrome；改选在下次“开始跟随”时生效，当前播放和换集继续使用原浏览器。
- 开始跟随时，未登录会提示“去登录 / 继续跟随”。在助手的浏览器中登录后尝试切到 1080p，并检查实际画质；两种浏览器分别保存登录状态。
- 地图识别默认开启，点击“开始跟随”后自动在原神小地图标记指导位置和可信视线；在“进度”下方的“地图识别”页调整频率与标记。默认 100ms，切出游戏时暂停；游戏使用默认小地图布局，攻略小地图需位于左 1/3、上 1/2。两端需有重叠地形，建议攻略使用 1080p。
- 最小化收进托盘，双击恢复；退出同时关闭专用浏览器和画中画。
- 画中画使用统一蓝色细边框，拖动横条或视频移动，拖动边缘调整大小。位置和大小自动保存在 `.gvh/config.json`，下次开启或换集时恢复；首次使用在左下角，“放回左下角”可复位。鼠标靠近时整体隐藏，离开显示；Alt+4 切换锁定显示、强制隐藏、鼠标避让。右上角关闭后不会自动重开。
- “进度”页显示每张地图、每一集看到了哪里。只统计真正播放过的部分，拖动或跳转跳过的不算；一集播放满 90% 才自动完成。
- 进度可以手动修正：在时间轴上拖选或填写时间来标记已看/未看，修改续播点，单集或批量标记完成，改错了可以撤销。
- 用户数据在 exe 同目录的 `.gvh/`：`config.json` 设置与绑定、`progress.json` 观看进度、`Chrome/` 与 `Edge/` 专用浏览器配置。搬动程序时一起带上 `.gvh`。
- 日志在 `.gvh/logs/app.log`，单个文件 1 MB，最多保留 4 个；排查问题时可把设置里的 `LogLevel` 改为 `Debug`。

| 默认快捷键 | 操作 |
| --- | --- |
| Alt+1 / 2 / 3 | 后退 / 暂停继续 / 前进 |
| Alt+← / → | 上一集 / 下一集 |
| Alt+P | 画中画开关 |
| Alt+Home | 放回左下角 |
| Alt+End | 静音开关 |
| Alt+4 | 切换浮窗显隐模式，长按不重复 |

所有绑定可在“快捷键”页修改。游戏建议使用无边框窗口。

## 开发

需要 .NET SDK 8+。Core 管应用流程与契约，Infrastructure 管浏览器、网络与存储，App 管界面与 Windows 功能；测试按层分开。

```powershell
dotnet build GenshinVideoHelper.sln -c Release
.\scripts\test.ps1
.\scripts\publish.ps1
```

发布目录：`artifacts/GenshinVideoHelper`；分发包：`artifacts/GenshinVideoHelper-win-x64.zip`（不含用户数据）。专项参数：`-Preview -Browser -Pip -Follow -Lifecycle -RenderUi -Progress -Latency -Background -Login -Vision -VisionBrowser -VisionAlgorithms`；浏览器专项默认 Chrome，加 `-Edge` 使用 Edge，需已安装对应浏览器，本地媒体需 FFmpeg。`-BrowserSwitch` 验证两浏览器切换及 B 站 P2 → P3，需联网。离线视觉回归必测 `tests/Fixtures/Minimap/basic` 的 8 张资源，存在本地 `full` 时追加。测试临时目录自动删除；延迟报告保留在 `artifacts/latency`。

参考：[PCL](https://github.com/Meloong-Git/PCL)（龙腾猫跃，视觉） · [BGI](https://github.com/babalae/better-genshin-impact)（babalae，技术与组织）。
