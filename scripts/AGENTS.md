# 脚本

- 从 `$PSScriptRoot` 确定仓库位置，路径操作用 `-LiteralPath`；失败立即停止。
- 发布用 `publish.ps1`，临时编译成功再更新产物；包内带 README / LICENSE，不带用户数据，保留目标 `.gvh/`。
- 清理仅限经过绝对路径校验的目标目录和助手拥有的进程，禁止按进程名批量关闭。
- 后台进程用隐藏窗口；测试和发布保持可重复，不依赖个人浏览器配置。
- 验证 Release 构建、包内容、exe 启动与正常退出；版本、标签与源码对应。
- 本地打包不创建 GitHub Release；上传操作按用户明确要求执行。见 [Release](../docs/release.md)。
- GitHub 手动触发 `Release draft`，校验标签版本、测试、调用 `publish.ps1`；只上传 exe 为草稿，由维护者发布。
