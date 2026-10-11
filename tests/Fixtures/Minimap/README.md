# 小地图视觉回归资源

- `basic/`：8 张原生 PNG 和 `manifest.json`，纳入 Git；包含真实用户/攻略帧、放大偏移、全黑、尺寸不足和箭头缺失。所有视觉回归都必须测试它们。
- `full/`：本地完整包（`images/`、`indexes/`、`expected/`），813 张图片，约 470MB，Git 忽略。存在时自动追加完整回归；缺文件或坏图会失败，目录不存在才跳过。

普通 `dotnet test GenshinVideoHelper.sln -c Release` 已包含这两类回归。只跑视觉算法：

```powershell
.\scripts\test.ps1 -VisionAlgorithms
```

测试位于 `GenshinVideoHelper.Infrastructure.Tests/Vision`。小样本随测试项目复制到输出目录，无需 Chrome、原神、原始录像或 `experiments`。期望结果固定保存，运行测试不会更新。其他开发者克隆仓库即可测小样本；复制整个 `full/` 包后自动扩展。

来源：用户录屏 `Recording 2026-10-11 094750.mp4`，汉卿导航 `BV1MXfEY4EQ2` P2 17:08–18:06。原始录像与实验脚本留在 `experiments`。
