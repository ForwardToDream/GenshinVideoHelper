# GenshinVideoHelper

Windows 原神 B站攻略跟随助手：分集选择、Chrome 画中画和全局快捷键。

## 使用

需要 Windows 10/11、Chrome 和 .NET 8 Desktop Runtime。

双击 `start.cmd` → 选库或输入链接 → 选分集 → 开始跟随。再次选中同一张地图时会定位到第一个未完成的分集，并从上次停下的位置继续。

- 最小化收进托盘，双击恢复；退出同时关闭专用 Chrome 和画中画。
- 鼠标进入浮窗四边各扩展 20% 的区域时隐藏，离开显示；主动关闭后不会自动重开。
- “进度”页显示每张地图、每一集看到了哪里。只统计真正播放过的部分，拖动或跳转跳过的不算；一集播放满 90% 才自动完成。
- 进度可以手动修正：在时间轴上拖选或填写时间来标记已看/未看，修改续播点，单集或批量标记完成，改错了可以撤销。
- 设置与绑定保存在根目录 `GenshinVideoHelper.settings.json`，观看进度保存在同目录的 `GenshinVideoHelper.progress.json`。
- 日志在 `%LOCALAPPDATA%\GenshinVideoHelper\logs\app.log`，单个文件 1 MB，最多保留 4 个；排查问题时可把设置里的 `LogLevel` 改为 `Debug`。

| 默认快捷键 | 操作 |
| --- | --- |
| Alt+1 / 2 / 3 | 后退 / 暂停继续 / 前进 |
| Alt+← / → | 上一集 / 下一集 |
| Alt+P | 画中画开关 |
| Alt+Home | 放回左下角 |
| Alt+End | 静音开关 |
| 按住 ~ / 反引号键 | 反转隐藏状态，松开恢复 |

所有绑定可在“快捷键”页修改。游戏建议使用无边框窗口。

## 开发

需要 .NET SDK 8+。Core 管应用流程与契约，Infrastructure 管浏览器、网络与存储，App 管界面与 Windows 功能；测试按层分开。

```powershell
dotnet build GenshinVideoHelper.sln -c Release
.\scripts\test.ps1
.\scripts\publish.ps1
```

发布目录：`artifacts/GenshinVideoHelper`。专项测试参数：`-Preview -Browser -Pip -Follow -Lifecycle -RenderUi -Progress -Latency -Background`；本地浏览器专项需要 FFmpeg。`-Progress` 用真实视频验证进度记录与续播，需要 Chrome 和联网。测试临时目录在 `artifacts/test-run`，结束时自动删除。`-Latency` 需要 Chrome 和联网， 单独记录启动、画中画与退出各阶段耗时至 `artifacts/latency`（保留最近 10 次），超出体验阈值时提示警告。

参考：[PCL](https://github.com/Meloong-Git/PCL)（龙腾猫跃，视觉） · [BGI](https://github.com/babalae/better-genshin-impact)（babalae，技术与组织）。
