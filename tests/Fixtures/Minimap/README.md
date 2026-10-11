# 小地图视觉回归资源

用于测试生产 C# 的圆盘定位和 ORB 双端定位，不需要 Chrome、原神或联网。

- `images/`：813 张原生 PNG（用户、攻略和控制样本），约 470MB，本地保存并由 Git 忽略。
- `indexes/`：图片、采样时刻和来源索引。
- `expected/`：已认可的 arrow_first 固定输出；运行测试不会更新它们。

用户样本来自 `Recording 2026-10-11 094750.mp4`；攻略样本来自汉卿导航 `BV1MXfEY4EQ2` P2 的 17:08–18:06。原始录像、实验脚本和实验输出保留在 `experiments`，本测试只读取这里的资源。换电脑运行此专项时，需要一起复制 `images/`。

在项目根目录运行：

```powershell
.\scripts\test.ps1 -VisionAlgorithms
```

临时结果写入 `artifacts/test-run`，结束后删除；生成的浏览器测试视频和界面截图继续放在 `artifacts`。
