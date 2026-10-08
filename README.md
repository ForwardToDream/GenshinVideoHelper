# GenshinVideoHelper

Windows 原神 B站攻略跟随助手：分集选择、Chrome 画中画和全局快捷键。

## 使用

需要 Windows 10/11、Chrome 和 .NET 8 Desktop Runtime。

双击 `start.cmd` → 选库或输入链接 → 选分集 → 开始跟随。

- 最小化收进托盘，双击恢复；退出同时关闭专用 Chrome 和画中画。
- 鼠标进入浮窗四边各扩展 20% 的区域时隐藏，离开显示；主动关闭后不会自动重开。
- 设置与绑定保存在根目录 `GenshinVideoHelper.settings.json`。

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

发布目录：`artifacts/GenshinVideoHelper`。专项测试参数：`-Preview -Browser -Pip -Follow -Lifecycle -RenderUi -Latency`；本地浏览器专项需要 FFmpeg。`-Latency` 需要 Chrome 和联网， 单独记录启动、画中画与退出各阶段耗时至 `artifacts/latency`，超出体验阈值时提示警告。

参考：[PCL](https://github.com/Meloong-Git/PCL)（龙腾猫跃，视觉） · [BGI](https://github.com/babalae/better-genshin-impact)（babalae，技术与组织）。
