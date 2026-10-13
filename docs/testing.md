# 测试

[文档目录](README.md) · [开发](development.md) · [视觉流程](vision.md)

## 基础回归

Windows 与 .NET 8 SDK；在仓库根目录执行：

```powershell
.\scripts\test.ps1
```

默认运行 Core、Infrastructure 单元测试，包括入库视觉样本；不要求浏览器、原神、B 站账号或本地录像。首次构建需要联网恢复 NuGet 包。

也可直接执行 `dotnet test GenshinVideoHelper.sln -c Release`。普通单元测试不承接真实桌面与网络联调。

## 专项

`scripts/test.ps1` 先跑基础回归，再按参数运行 Smoke；`-VisionAlgorithms` 只筛选视觉单元测试。可以组合参数，任一步失败即停止。

| 参数 | 验证 | 额外条件 |
| --- | --- | --- |
| `-Preview` | 预选、分 P、异步预加载、错误重试 | Windows 桌面 |
| `-History` | 手动跟随历史、持久化、取消、两次删除、下拉尺寸 | Windows 桌面；模拟浏览器 |
| `-ProgressUi` | 进度编辑、自动完成、悬停时间、分组滚动、默认 / 最小布局 | Windows 桌面；模拟分集 |
| `-RenderUi` | 导航、默认 / 最小尺寸、下拉、库搜索、错误状态 | Windows 桌面 |
| `-Library` | 新增 / 编辑 / 取消、JSON 保存、库刷新、搜索、紧凑布局与滚动 | Windows 桌面，无浏览器 / 网络 |
| `-PipShell` | 无视频启动外框、透明穿过、启闭、复位、布局记忆与退出 | Windows 桌面，无浏览器 / 网络 |
| `-Login` | 匿名提示、取消、旧请求隔离、高清反馈 | Windows 桌面；模拟账号 |
| `-Browser` | CDP、播放、暂停、跳转边界、倍速、静音、单视频复用与连点 | Chrome / Edge、FFmpeg |
| `-Pip` | 原生画中画、统一外框、热键、显隐、布局记忆 | Windows 桌面、浏览器、FFmpeg |
| `-Lifecycle` | 托盘、退出、热键释放、专用进程隔离 | Windows 桌面、浏览器、FFmpeg |
| `-VisionAlgorithms` | arrow_first、地形 ORB、固定用户圆盘、反例 | 无额外资源要求 |
| `-VisionBrowser` | 原生视频帧、裁剪、帧复用、1080p 播放采集 | Windows 桌面、浏览器、FFmpeg |
| `-Vision` | WGC、标记窗口、持续显示与原生视频帧 | Windows 桌面、浏览器、FFmpeg |
| `-Follow` | B 站 P3 → P4、自动播放 / 画中画、换集与快捷键 | Windows 桌面、浏览器、FFmpeg、联网 |
| `-Progress` | 实际播放进度、跳转、续播、持久化 | Windows 桌面、浏览器、联网 |
| `-Latency` | 启动、预热、关闭预算 | Windows 桌面、浏览器、联网 |
| `-Background` | 其他程序前台时后台跟随、焦点隔离 | Windows 桌面、浏览器、联网 |
| `-BrowserSwitch` | Chrome / Edge 切换、P2 → P3、独立配置与退出 | Windows 桌面、两种浏览器、联网 |

浏览器专项默认 Chrome，加 `-Edge` 测 Edge；`-BrowserSwitch` 固定覆盖两者。B 站专项受网络、视频及账号权限影响，失败需区分环境与实现。

```powershell
.\scripts\test.ps1 -Browser -Pip -VisionBrowser
.\scripts\test.ps1 -Browser -Pip -VisionBrowser -Edge
.\scripts\test.ps1 -Preview -RenderUi -Login
.\scripts\test.ps1 -Follow -Background -BrowserSwitch
```

桌面专项会操作测试窗口与热键；使用独立配置和专用浏览器，不复用个人配置。性能结果用于对比，不把短采集延迟转为丢弃标记规则。

## 视觉资源

| 目录 | 规则 |
| --- | --- |
| `tests/Fixtures/Minimap/basic/` | 8 张 PNG + `manifest.json`，随 Git 提交，始终测试 |
| `tests/Fixtures/Minimap/full/` | 本地完整包，Git 忽略；存在就追加测试，缺文件或坏图失败 |
| `experiments/` | 本地原始录像与探索脚本，不作为基础测试依赖 |

小样本复制到测试输出目录。期望结果固定，测试不得现场重算或自动改写；完整包只有整个目录不存在时才跳过。素材来源、目录结构见 [资源说明](../tests/Fixtures/Minimap/README.md)。

必要案例：真实双端、攻略放大 / 偏移、全黑、箭头缺失、尺寸不足、不相干地形；既验圆盘与投影精度，也验拒绝错误标记。

## 按改动选检查

| 改动 | 重点 |
| --- | --- |
| 跟随 / 浏览器 | 单视频、快速连点、取消、换集、待生效选择、旧事件隔离 |
| 播放 / 登录 | 暂停、边界跳转、倍速、静音、匿名与未知状态、实际解码尺寸 |
| 浮窗 / 热键 | 拖动缩放、重启恢复、三态整体显隐、手动关闭、冲突与释放 |
| 进度 / 存储 | 真实区间、跳转不累计、手动优先、撤销、坏文件与备份 |
| CV / 采集 | basic 必测、可选 full、播放与暂停、裁剪 / 换源 / 失焦、短暂失配保留 |
| UI | 默认 1020×730、最小 860×600，下拉、折叠、长标题、错误状态 |
| 发布 | Release 构建、包内容、启动与正常退出、`.gvh` 保留、个人浏览器不受影响 |

`publish.ps1` 自动运行 `tests/GenshinVideoHelper.PackageTests`。该项目不引用业务程序集，验证压缩资源加载、OpenCV / ORB 和 .NET 8 原生安装入口，防止开发目录中的 DLL 掩盖缺失依赖。

真实游戏联调需用户配合；没有执行时如实说明，不能用离线样本或模拟窗口结果替代实测结论。

## 产物与清理

测试临时目录由 `TestArtifacts` 创建在 `artifacts/test-run`，使用后删除。UI 截图留在 `artifacts/ui-*.png`，性能报告留在 `artifacts/latency`。

新增测试使用同一机制。递归删除前验证绝对路径在预期目录内；失败不能通过删样本、重写期望、放宽无关阈值或吞异常解决。
