# Release

[文档目录](README.md) · [测试](testing.md)

发布 **Windows x64 自带运行时的单文件 exe**。GitHub 只上传 `GenshinVideoHelper.exe`；源码由对应标签提供。

## 版本

[Directory.Build.props](../Directory.Build.props) 统一版本，当前 `0.1.0`；标签为 `v0.1.0`。后续发版递增三段数字，标签、exe 和源码版本一致。`publish.ps1` 校验 exe 版本，不创建标签、不上传。

## 本地验证

关闭目标目录的旧助手，在仓库根目录执行：

```powershell
.\scripts\test.ps1
.\scripts\publish.ps1
```

exe 位于 `artifacts/GenshinVideoHelper/GenshinVideoHelper.exe`。本地脚本仍生成 zip 便于测试；GitHub 不上传它。按改动补跑专项，验证单独 exe 的启动、退出与使用说明。目标目录已有 `.gvh/` 保留，用户数据不上传。

## 上传草稿

1. 修改版本，测试后提交源码并推送 `main`。工作流须先进入默认分支。
2. 为该提交创建并推送标签：

```powershell
git tag -a v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

3. 在 GitHub **Actions → Release draft → Run workflow** 输入已推送的标签。
4. [release.yml](../.github/workflows/release.yml) 检出标签，校验版本，运行基础回归，再调用 `publish.ps1`，仅上传 exe 并创建草稿。
5. 在 **Releases** 检查附件、下载后试运行、编辑更新说明，最后点击 **Publish release**，设为最新正式版。

工作流使用内置 `GITHUB_TOKEN` 的 `contents: write` 权限，无需个人令牌。不因普通提交或标签推送自动运行，不覆盖已有 Release。草稿不会触发客户端更新提示。[手动触发规则](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)、[Release 创建参数](https://cli.github.com/manual/gh_release_create)。

## 启动更新检查

每次启动后台检查一次；设置页可手动重试。同时只允许一次请求，6 秒超时；退出取消检查，不等待网络。无网络、限流或失败仅记录日志，不弹窗，不影响跟随。

使用公开仓库 `ForwardToDream/GenshinVideoHelper` 的 `GET /repos/ForwardToDream/GenshinVideoHelper/releases/latest`，不需要账号或令牌。只比较正式版本的三段数字；较旧或相同版本不提示。尚未正式发布时 404 视为暂无更新；不读草稿、预发布，不下载附件。[GitHub 接口](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)。

UI 读取程序集版本；[IUpdateChecker](../src/GenshinVideoHelper.Core/Contracts/UpdateServices.cs) 定义契约，[GitHubUpdateChecker](../src/GenshinVideoHelper.Infrastructure/Updates/GitHubUpdateChecker.cs) 处理 HTTP，[MainWindow.Updates](../src/GenshinVideoHelper.App/Views/MainWindow.Updates.cs) 管检查与提示。仅用户点击后打开 Release 页，手动下载并退出助手后替换 exe，保留 `.gvh/`。
