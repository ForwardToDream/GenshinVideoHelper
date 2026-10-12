# 源码

- Core 管契约、规则和流程；Infrastructure 管外部实现；App 管 WPF 与 Windows 能力，依赖指向 Core。
- 在 `AppServices` 装配实例和所有权；新外部能力经 Core 契约注入。
- 会话状态由 UI 同步上下文拥有；耗时采集、分析在 worker，禁止同步阻塞 UI。
- 新请求取消旧请求；结果和事件校验版本及源身份，旧结果不得恢复已撤销状态。
- 所有者释放帧、Mat、Stream、句柄、订阅和 CTS；观察后台任务，缓存有容量与失效条件。
- 详细流程见 [开发](../docs/development.md)，各项目约束见其 `AGENTS.md`。
