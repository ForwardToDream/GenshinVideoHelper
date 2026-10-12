# Release

[文档目录](README.md) · [测试](testing.md)

当前流程：本地构建、手动上传。`publish.ps1` 不创建 Git 标签，也不上传 GitHub。

## 版本

正式发布前，在根目录 [Directory.Build.props](../Directory.Build.props) 的 `PropertyGroup` 中设置统一版本，例如：

```xml
<Version>0.1.0</Version>
```

Git 标签对应 `v0.1.0`。这里仅为示例；实际版本由维护者确定。标签必须指向生成发布包的源码提交。

## 本地打包

先设置版本并提交对应源码，再关闭目标发布目录中的旧助手，在仓库根目录执行：

```powershell
.\scripts\test.ps1
.\scripts\publish.ps1
```

| 产物 | 用途 |
| --- | --- |
| `artifacts/GenshinVideoHelper/GenshinVideoHelper.exe` | 自带运行时的 Windows x64 单文件程序，可直接运行 |
| `artifacts/GenshinVideoHelper-win-x64.zip` | 分发包，包含 exe、用户 README 与 GPL-3.0 LICENSE |

脚本先编译到独立暂存目录，成功后打包并更新本地发布目录。已有 `.gvh/` 保留；zip 不包含用户配置、日志或浏览器登录数据。

按改动补跑专项，检查 exe 启动、正常退出及包内容。发布前推送本次打包的源码提交；不要把 exe、zip 或 `.gvh/` 加入 Git。代码有改动则重新提交、测试和打包。

## GitHub 发布

准备好后，对本次已验证的提交打标签并推送；以下命令仅示例：

```powershell
git tag -a v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

1. 打开仓库 **Releases → Draft a new release**，选择 `v0.1.0` 标签。
2. 填版本标题和简短更新说明。
3. 上传 `GenshinVideoHelper-win-x64.zip`；需要单文件下载时可同时上传 `GenshinVideoHelper.exe`。
4. 测试版本勾选 **pre-release**；准备好后 **Publish release**，暂存用 **Save draft**。

GitHub 自动附加的 Source code 下载提供该标签的源码；可运行程序需自行附上。发布后 README 的 Releases 入口即可下载附件。[GitHub 发布说明](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)、[Release 与标签](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。
