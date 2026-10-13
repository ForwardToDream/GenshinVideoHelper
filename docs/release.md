# Release

[文档目录](README.md) · [测试](testing.md)

发布 **Windows x64 单文件 exe**，不内置 .NET。用户安装 **.NET 8 Desktop Runtime x64**，无需 SDK。GitHub 只上传 `GenshinVideoHelper.exe`；源码由对应标签提供。

`publish.ps1` 使用 `--self-contained false`，依赖系统共享运行时。发布时用 Costura 压缩嵌入依赖，再由 SDK 打成一个 exe；不生成第二个 exe。托管依赖从内存加载，原生 OpenCV 缓存 DLL 后加载。普通开发构建不启用此流程。WPF 不裁剪；OpenCV 使用 `runtime.win.slim`，保留 ORB、图像处理和几何估计。[单文件说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[Costura](https://github.com/Fody/Costura)。

缺少桌面运行时时，由 exe 的原生 .NET apphost 显示安装提示并打开微软下载页，无需先加载 C#。App 的运行时入口为 `Microsoft.WindowsDesktop.App`，由桌面运行时解析 Core 依赖，缺框架提示直接指向 Desktop Runtime。安装后用户重新启动；不自动安装。Release 说明附下载入口。[启动提示](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch)。

## 版本

[Directory.Build.props](../Directory.Build.props) 统一版本，当前 `0.1.0`；标签为 `v0.1.0`。后续发版递增三段数字，标签、exe 和源码版本一致。`publish.ps1` 校验 exe 版本，不创建标签、不上传。

## 本地验证

关闭目标目录的旧助手，在仓库根目录执行：

```powershell
.\scripts\test.ps1
.\scripts\publish.ps1
```

exe 位于 `artifacts/GenshinVideoHelper/GenshinVideoHelper.exe`。本地脚本仍生成 zip 便于测试；GitHub 不上传它。按改动补跑专项，验证单独 exe 的启动、退出与使用说明。目标目录已有 `.gvh/` 保留，用户数据不上传。

发布脚本先运行 `GenshinVideoHelper.PackageTests`：隔离加载压缩依赖、原生 OpenCV、真实图片 ORB，以及缺少 .NET / Desktop Runtime 时的原生安装入口。失败不覆盖现有发布包；检查包内只有 exe、README 和 LICENSE。

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

每次启动后台检查一次；设置页可手动重试。有新版仅在版本号左侧显示红色“可更新”，没有新版或检查失败则隐藏。同时只允许一次请求，6 秒超时；退出取消检查，不等待网络。不弹窗，不影响跟随。

使用公开仓库 `ForwardToDream/GenshinVideoHelper` 的 `GET /repos/ForwardToDream/GenshinVideoHelper/releases/latest`，不需要账号或令牌。只比较正式版本的三段数字；较旧或相同版本不提示。尚未正式发布时 404 视为暂无更新；不读草稿、预发布，不下载附件。[GitHub 接口](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)。

UI 读取程序集版本；[IUpdateChecker](../src/GenshinVideoHelper.Core/Contracts/UpdateServices.cs) 定义契约，[GitHubUpdateChecker](../src/GenshinVideoHelper.Infrastructure/Updates/GitHubUpdateChecker.cs) 处理 HTTP，[MainWindow.Updates](../src/GenshinVideoHelper.App/Views/MainWindow.Updates.cs) 管检查与提示。仅用户点击后打开 Release 页，手动下载并退出助手后替换 exe，保留 `.gvh/`。
