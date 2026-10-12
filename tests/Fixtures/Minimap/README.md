# 小地图测试资源

| 目录 | 内容 | 运行规则 |
| --- | --- | --- |
| `basic/` | 8 张 PNG 与 `manifest.json`，随 Git | 始终运行，缺图即失败 |
| `full/` | 本地完整包：`images/`、`indexes/`、`expected/`，Git 忽略 | 存在即追加，缺文件 / 坏图失败；整个目录不存在才跳过 |

小样本覆盖真实双端、放大偏移、全黑、尺寸不足、箭头缺失；期望圆盘与定位结果固定保存在 manifest，测试不重算或改写。

仓库根目录执行：

```powershell
.\scripts\test.ps1 -VisionAlgorithms
```

普通 `dotnet test GenshinVideoHelper.sln -c Release` 也包含视觉回归。小样本自动复制到输出目录，无需浏览器、原神、完整资源或原始录像。

来源：用户录屏 `Recording 2026-10-11 094750.mp4`；汉卿导航 `BV1MXfEY4EQ2` P2 17:08–18:06。原始录像留在本地 `experiments/`。

[测试说明](../../../docs/testing.md) · [视觉流程](../../../docs/vision.md) · [回归代码](../../GenshinVideoHelper.Infrastructure.Tests/Vision/MinimapFixtureTests.cs)
