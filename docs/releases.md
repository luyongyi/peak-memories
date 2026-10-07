# 构建、验证与发布

Mod 必须使用本机合法安装的 PEAK／BepInEx 编译引用。[PEAKModding 官方模板](https://github.com/PEAKModding/BepInExTemplate#publishing-via-github-actions)也注明缺少游戏 GameLibs 包，不能直接在 GitHub Actions 编译发布。本项目在本机构建，云端只运行独立测试、检查上传包和发布。

本机构建需要 PowerShell 7、Git、.NET 10 SDK 和 .NET 8 Runtime。上传或发布时还需要 GitHub CLI，并通过 `gh auth login` 登录仓库所有者账号。游戏目录需要包含 `PEAK_Data/Managed/Assembly-CSharp.dll`、`BepInEx/core/BepInEx.dll` 和 `BepInEx/core/0Harmony.dll`。

发布前将源码、版本和发布工具的修改提交并推送到 `main`。脚本要求干净的工作区，核对本地提交与远端提交，记录实际游戏编译引用的哈希，并核对项目版本、插件声明与 DLL 元数据，绑定 `vX.Y.Z` 标签与实际构建提交。版本不一致会停止；已有同名附件内容不同也会停止，不覆盖已发布版本。

从仓库根目录执行：

```powershell
# 默认只在本机生成发布包，不上传或公开发布。
pwsh -File scripts/release.ps1

# 上传草稿和四个附件，等待 GitHub 云端验证，保持草稿。
pwsh -File scripts/release.ps1 -Draft

# 验证成功后公开为实验性预发布，不设为 latest。
pwsh -File scripts/release.ps1 -Publish

# 显式指定版本和游戏位置。
pwsh -File scripts/release.ps1 -Tag v0.7.4 -GameRoot "D:/SteamLibrary/steamapps/common/PEAK/" -Draft
```

`-Draft` 与 `-Publish` 互斥；省略 `-Tag` 时使用项目版本。`-Publish` 明确授权公开预发布版本。脚本不会自动安装 DLL、删除游戏文件或注册后台 runner。

ZIP `PeakReplayLab-X.Y.Z.zip` 仅包含 `PeakReplayLab.dll`、安装说明 `README-INSTALL.md`、`build-manifest.json` 与包内 `SHA256SUMS.txt`。Release 仅上传四个附件：该 ZIP、同字节的独立 DLL、同字节的构建清单，以及校验 ZIP／DLL／清单的外部 `SHA256SUMS.txt`。不包含 PEAK／Unity／BepInEx 的原始 DLL、提取资源、日志、配置、私人录像或本机绝对路径。完整产物结构和版本信息由发布验证器检查。

`release.yml` 只接受仓库所有者从 `main` 发起的手动请求，由本机脚本自动提交标签、发布意图和唯一请求编号。它在 GitHub 托管的 Windows 环境运行六组 contract 测试及发布验证器自检，核对远端标签、提交、ZIP 文件白名单、DLL 元数据、构建清单和 SHA256。发布作业在独立 Ubuntu 环境再次核对标签、Release 身份和四个附件字节，随后才公开预发布。直接推送标签不会在云端编译 Mod；默认 `publish=false` 不会公开草稿。

当前版本 `v0.7.4` 可先使用 `-Draft` 验证整个流程。查看脚本输出的对应 Actions run，确认六组测试、验证器自检与上传包检查全部通过；脚本按唯一请求编号等待该次运行，不将其他运行的成功当作此次验收。已公开的同版本 Release 通过验证后保留现状，不重复发布或替换附件。

云端验证通过说明源码绑定、独立测试和产物完整性通过；Unity 中的录制、回放画面、声音与性能仍需按对应版本验收报告实测。验证失败保留草稿供检查，修复后提交新的版本再发布。
