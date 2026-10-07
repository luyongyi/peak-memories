# 开发与测试

从本仓库根目录执行命令。`global.json` 使用 .NET 10 SDK；当前测试目标为 `net8.0`，运行时还需要 .NET 8 Runtime（安装 .NET 8 SDK 也可以）。

## 构建 Mod

使用本机合法安装的 PEAK 和 BepInEx，游戏目录应包含 `PEAK_Data/Managed/Assembly-CSharp.dll`、`BepInEx/core/BepInEx.dll` 和 `BepInEx/core/0Harmony.dll`。

```powershell
dotnet build PeakReplayLab.slnx -c Release "-p:PEAKGameRootDir=C:/Program Files (x86)/Steam/steamapps/common/PEAK/"
```

将路径改为自己的游戏目录，末尾保留 `/`。游戏 DLL 仅作为本机编译引用，不提交到仓库。封面资源位于仓库内 `assets/home-art/`。

## 独立测试

以下八个可执行测试项目不需要游戏安装或游戏资产，可以逐个运行：

```powershell
dotnet run --project tests/ReplayContract/ReplayContract.csproj -c Release
dotnet run --project tests/NativeAppearanceContract/NativeAppearanceContract.csproj -c Release
dotnet run --project tests/NativeLoadingContract/NativeLoadingContract.csproj -c Release
dotnet run --project tests/ReplayViewContract/ReplayViewContract.csproj -c Release
dotnet run --project tests/ReplayNameplateContract/ReplayNameplateContract.csproj -c Release
dotnet run --project tests/ReplayAudioSpatialContract/ReplayAudioSpatialContract.csproj -c Release
dotnet run --project tests/TrajectoryContract/TrajectoryContract.csproj -c Release
dotnet run --project tests/TrajectoryUploadContract/TrajectoryUploadContract.csproj -c Release
```

GitHub Actions 在 Ubuntu 和 Windows 上安装 .NET 10 和 .NET 8，分别构建并运行这八个项目。CI 不构建包含游戏 DLL 引用的生产解决方案。Windows 覆盖 Windows 专用的文件操作检查，Linux 会跳过部分系统相关检查，两平台的通过数量可能不同。

测试验证数据格式、回放时钟、调度和资源配置；涉及 Unity 的项目使用小型替身。实际画面、声音与性能仍需在游戏内录制和回放验收。

## 发布工具

维护者的一键构建、打包、Release 草稿及公开预发布流程见 [发布说明](releases.md)。`ReleaseVerifier` 使用 PE 元数据读取 DLL，不加载或执行游戏程序集；它的合成负面检查也在 Windows／Ubuntu CI 中运行：

```powershell
dotnet run --project tools/ReleaseVerifier/ReleaseVerifier.csproj -c Release -- self-test
```
