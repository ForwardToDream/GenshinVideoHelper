# 项目约定

- 简体中文，简短说明结果、验证与实际限制。
- 不明确时向用户提具体问题；继续完成不受影响的部分。
- 默认亲自修改；用户要求委派时才使用子 Agent。
- 直接用补丁编辑，不覆盖用户改动，不为小改动新增框架。
- 修改前沿目标文件路径读取各级 `AGENTS.md`；子目录只补充局部约束。
- README 只写用户操作；开发说明放 [docs](docs/README.md)，行为变化同步文档。
- 构建：`dotnet build GenshinVideoHelper.sln -c Release`；按 [测试说明](docs/testing.md) 验证，如实报告未测项。
- 本地参考、实验、完整样本和用户数据不入库；固定小样本随 Git。
- GPL-3.0；发布包与对应版本源码保持一致。发布流程见 [Release](docs/release.md)。
