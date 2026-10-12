# 视觉流程

[文档目录](README.md) · [开发](development.md) · [测试](testing.md)

目标：将攻略玩家的位置投影到用户小地图。匹配对象是两盘的**地形**；箭头只帮助定位攻略圆心，方向取白色视野扇形。

## 入口与坐标

| 入口 | 职责 |
| --- | --- |
| [MainWindow.Vision](../src/GenshinVideoHelper.App/Views/MainWindow.Vision.cs) | 跟随后绑定游戏、设置、失焦及会话切换 |
| [MinimapFollowCoordinator](../src/GenshinVideoHelper.Core/Application/MinimapFollowCoordinator.cs) | 采样、单请求、最新攻略帧、结果版本与生命周期 |
| [GameWindowCapture](../src/GenshinVideoHelper.App/Native/GameWindowCapture.cs) | WGC / Direct3D11 游戏帧与 GPU 裁剪 |
| [VideoFrameSource](../src/GenshinVideoHelper.Infrastructure/Browser/VideoFrameSource.cs) / `VideoFrameCapture.js` | 解码视频原生像素、帧标识、局部采集 |
| [MinimapAnalyzer](../src/GenshinVideoHelper.Infrastructure/Vision/MinimapAnalyzer.cs) | 圆盘归一化、地形配准、方向、选色 |
| [MinimapOverlay](../src/GenshinVideoHelper.App/Native/MinimapOverlay.cs) | 用户小地图上的位置点与方向条 |

`VisionFrame.Region` 是完整源图坐标；游戏使用 client 物理像素，标记转换到屏幕坐标。方向使用弧度，0 向右、π/2 向下。浮窗大小与 WPF DIP 不参与 CV。像素缓冲独占、池化，用完必须释放。

## 采集

**用户端固定裁剪。** 1080p 圆心 `(168,125)`、半径 `106`，按 client 高度等比缩放；WGC 在 GPU 裁小地图区域，不检测用户箭头、圆盘或边缘。

**攻略端原生采集。** 从浏览器解码视频取像素，不截缩小后的画中画：

1. 初始搜索 `width / 3 × height / 2`，即左 1/3、上 1/2。
2. 找到圆盘后，`CapturePlan` 只取圆盘及 1.12 倍边距，并绑定源 epoch。
3. Canvas 取原生 RGBA，经 CDP 传输，C# 转 BGRA；不走 PNG 编解码。
4. 相同解码帧以 `FrameId` 确认，复用像素、归一化攻略盘及 ORB 特征。
5. 局部裁剪找不到圆盘时，下次回全搜索范围；仍有有效攻略盘时先沿用。完整搜索确认无盘时撤销标记。

单次采集限 300 万像素，CDP 响应限 16 MB、命令默认超时 8 秒；这些是传输预算，不是标记年龄限制。

采集需匹配当前 BV、P、媒体与画中画。跳转、换媒体或源尺寸变化更新 epoch；关闭画中画或源无效返回空帧。1080p 是推荐画质，不用插值放大冒充高清。

## 攻略圆盘：arrow_first

唯一实现：[GuideDiskDetector](../src/GenshinVideoHelper.Infrastructure/Vision/GuideDiskDetector.cs)。

1. 限制搜索区域，粗搜索宽度最多 320 像素。
2. HSV 筛青色连通域，以面积、填充率、形状排除圆形 UI 图标。
3. 回原生像素校验三角轮廓，兼容箭头放大与 360° 旋转。
4. **以箭头质心固定圆心**；根据面积估计半径，搜索径向梯度支持。
5. 半径下限来自同源高度的默认游戏圆盘，允许 8% 边缘容差；限定完整圆，不拟合椭圆。

箭头不参与地形配准，不把箭头朝向当视线。圆盘半径沿用该算法，不加入其他圆盘检测器或额外外圈拒绝机制。

## 地形配准与投影

两盘归一化为 `212×212`，圆心 `(106,106)`、地图半径 `100`。掩码排除中心箭头、白色 / 青色图标、顶部及外缘，保留地形。

[TerrainRegistration](../src/GenshinVideoHelper.Infrastructure/Vision/TerrainRegistration.cs) 使用 CLAHE → ORB → Hamming KNN → RANSAC 部分仿射；支持平移、旋转与等比缩放，不使用 SIFT 或全世界地图库。

当前关键检查如下；全部在归一化坐标中计算，参数以实现为准。

| 项目 | 条件 |
| --- | --- |
| ORB | 最多 700 特征，8 层，缩放 1.12 |
| 候选对应 | 比率 `< 0.78`，目标特征去重，至少 8 个 |
| RANSAC | 重投影阈值 2 像素 |
| 内点 | 至少 8 个，且占候选至少 25% |
| 残差 / 分布 | 中位残差 `< 1.5`，两端内点凸包面积均至少 500 |
| 尺度 / 位置 | 缩放 0.65–1.5，指导点在半径 94 内 |
| 重叠 | 有效共同像素至少 1800 |
| 地形一致 | 全局高通相关性至少 0.50，至少 6 个有效局部分区且相关性中位数至少 0.50 |

变换将攻略点映射到用户盘：

```text
x' = a*x - b*y + tx
y' = b*x + a*y + ty
```

将攻略圆心 `(106,106)` 代入，再换算到用户 client 坐标，即指导位置。不能直接标用户圆心，不能拿箭头位置差代替地形变换。

## 方向与颜色

[CameraDirection](../src/GenshinVideoHelper.Infrastructure/Vision/CameraDirection.cs) 在半径 22–76 的极坐标中搜索白色扇形双边界，拟合射线，验证交点、开角和峰值优势。只输出可信方向；失败仍画位置点。

方向随地形变换旋转。标记从用户地图局部颜色中挑选对比明显的颜色，并保留颜色稳定性；位置点与方向条一起呈现。

## 调度与标记生命周期

默认间隔 100ms，可设 50–200ms。一个分析 worker、一个在途攻略请求，只保留最新完成的攻略帧；游戏当前帧继续采样和匹配，不等攻略采集完成。

| 情况 | 行为 |
| --- | --- |
| 成功配准 | 更新位置点；可信方向才更新方向条 |
| 攻略采集慢 | 使用最后完成的有效攻略帧，继续匹配当前游戏地图 |
| 短暂特征不足或配准失败 | 保留上次有效标记 |
| 局部攻略裁剪失效 | 下一帧重新全区域搜索，避免立即闪退标记 |
| 明确无盘、全黑、变换无效、可见范围 / 重叠不足 | 清除标记 |
| 游戏失焦、换源、跳转、画中画关闭、停止跟随 | 清除标记并使旧结果失效 |

`MapAnalysis.RetainMarker` 区分暂时失败与明确拒绝。generation / epoch 隔离源身份；不按固定 250ms 时间丢弃帧，不设置标记年龄过期，也不堆积待处理帧。

OpenCV 单线程，游戏 GPU 裁剪，攻略局部采集；相同攻略帧复用归一化图和特征。优化先核实采集、传输、缓存命中与资源释放，再调整算法。

回归入口见 [测试](testing.md#视觉资源)。原始实验在本地 `experiments/`；仓库开发和测试以此文档、实现与入库样本为依据。
