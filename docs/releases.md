# 构建、验证与发布

Mod 必须使用本机合法安装的 PEAK／BepInEx 编译引用。[PEAKModding 官方模板](https://github.com/PEAKModding/BepInExTemplate#publishing-via-github-actions)也注明缺少游戏 GameLibs 包，不能直接在 GitHub Actions 编译发布。本项目在本机构建，GitHub 云端验证源码与上传包，最后由本机脚本公开发布。

本机构建需要 PowerShell 7、Git、.NET 10 SDK 和 .NET 8 Runtime。上传或发布时还需要 GitHub CLI，并通过 `gh auth login` 登录仓库所有者账号。游戏目录需要包含 `PEAK_Data/Managed/Assembly-CSharp.dll`、`BepInEx/core/BepInEx.dll` 和 `BepInEx/core/0Harmony.dll`。

准备新版本时，同步更新 `PeakReplayLab.csproj` 的 `Version`、`Plugin.cs` 的 `BepInPlugin` 版本，以及 `ReplayData.cs` 的 `ReplayHeader.Recorder` 版本，然后提交并推送到 `main`。普通构建要求工作区干净、HEAD 与已有版本标签相同，并核对远端提交、DLL 元数据和实际游戏编译引用的哈希。版本或源码提交不一致会停止；已有同名附件内容不同也会停止，不覆盖附件或移动已有标签。

从仓库根目录执行：

```powershell
# 默认只在本机生成发布包，不上传或公开发布。
pwsh -File scripts/release.ps1

# 上传草稿和四个附件，等待 GitHub 云端验证，保持草稿。
pwsh -File scripts/release.ps1 -Draft

# 准备下一个新版本并提交推送后：验证成功再由本机公开为预发布。
pwsh -File scripts/release.ps1 -Publish

# 新版本显式指定标签和游戏位置，项目的三个版本字段需先更新。
pwsh -File scripts/release.ps1 -Tag v0.7.5 -GameRoot "D:/SteamLibrary/steamapps/common/PEAK/" -Draft
```

`-Draft` 与 `-Publish` 互斥；省略 `-Tag` 时使用项目版本。`-Publish` 明确授权公开预发布版本。脚本不会自动安装 DLL、删除游戏文件或注册后台 runner。

已有 `v0.7.4` 草稿可用恢复模式继续验证或发布：

```powershell
# 重新验证现有草稿，保持不公开。
pwsh -File scripts/release.ps1 -Resume -Tag v0.7.4 -Draft

# 重新验证同一组附件，通过后由本机公开为实验性预发布。
pwsh -File scripts/release.ps1 -Resume -Tag v0.7.4 -Publish
```

`-Resume` 必须同时指定 `-Tag vX.Y.Z` 和 `-Draft`／`-Publish`。它核对现有四个附件、Release 身份和标签提交，再由云端检出标签的原始源码验证；不重新编译、不上传替换附件，也不需要游戏目录。当前 `v0.7.4` 保留原始源码提交和已有附件，后续 `main` 的发布工具修复不会改动这个标签。

ZIP `PeakReplayLab-X.Y.Z.zip` 仅包含 `PeakReplayLab.dll`、安装说明 `README-INSTALL.md`、`build-manifest.json` 与包内 `SHA256SUMS.txt`。Release 仅上传四个附件：该 ZIP、同字节的独立 DLL、同字节的构建清单，以及校验 ZIP／DLL／清单的外部 `SHA256SUMS.txt`。不包含 PEAK／Unity／BepInEx 的原始 DLL、提取资源、日志、配置、私人录像或本机绝对路径。完整产物结构和版本信息由发布验证器检查。

`release.yml` 只接受仓库所有者从 `main` 发起的手动请求，由本机脚本自动提交标签和唯一请求编号。它在 GitHub 托管的 Windows 环境运行六组 contract 测试及发布验证器自检，核对远端标签、提交、ZIP 文件白名单、DLL 元数据、构建清单和 SHA256。工作流的 `contents: write` 用于读取草稿；云端没有发布作业，保留的 `publish` 输入仅兼容原调用，即使传入 `true` 也不会公开发布。直接推送标签不会在云端编译 Mod。

脚本按唯一请求编号等待对应 Actions run，确认六组测试、验证器自检与上传包检查全部通过。`-Draft` 至此保留草稿；`-Publish` 再由本机核对标签、Release 身份和四个附件的哈希，用已登录的 `gh` 公开为实验性预发布，不设为 latest。这样无需向 Actions 额外配置发布 secret，也能继续发布源码标签与当前 `main` 工作流不同的已验证草稿。已公开的同版本 Release 通过验证后保留现状，不重复发布或替换附件。

云端验证通过说明源码绑定、独立测试和产物完整性通过；Unity 中的录制、回放画面、声音与性能仍需按对应版本验收报告实测。验证失败保留草稿供检查，修复后提交新的版本再发布。
