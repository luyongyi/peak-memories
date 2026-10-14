# 开发与测试

从本仓库根目录执行命令。`global.json` 使用 .NET 10 SDK；多数测试目标为 `net8.0`，运行时还需要 .NET 8 Runtime（安装 .NET 8 SDK 也可以）。Windows 原生调用契约目标为 `net472`。

## 构建 Mod

使用本机合法安装的 PEAK 和 BepInEx，游戏目录应包含 `PEAK_Data/Managed/Assembly-CSharp.dll`、`BepInEx/core/BepInEx.dll` 和 `BepInEx/core/0Harmony.dll`。

```powershell
dotnet build PeakReplayLab.slnx -c Release "-p:PEAKGameRootDir=C:/Program Files (x86)/Steam/steamapps/common/PEAK/"
```

将路径改为自己的游戏目录，末尾保留 `/`。游戏 DLL 仅作为本机编译引用，不提交到仓库。封面资源位于仓库内 `assets/home-art/`。

## 独立测试

`AutomaticFullRunStartContract` 检查自动完整录制的启动资格和首帧失败恢复：首个有效帧前不创建文件，成功首帧随即入队；文件创建失败、手动停止及已开始录像的故障仍保留一次性停止规则。纯策略及状态机测试不替代存档恢复实机验收。

```powershell
dotnet run --project tests/AutomaticFullRunStartContract/AutomaticFullRunStartContract.csproj -c Release -p:TreatWarningsAsErrors=true
```

`NativeSegmentScopeContract` 在 Windows 直接编译关卡门控与真实箱子采集，使用原生对象替身验证未来关无完整读取、实际根激活、未选变体排除、预算公平性、携带物重归属、即时动作、退役与历史不变。不依赖游戏资源，不代表实机 CPU 或画面验收。

```powershell
dotnet run --project tests/NativeSegmentScopeContract/NativeSegmentScopeContract.csproj -c Release -p:TreatWarningsAsErrors=true
```

`EnvironmentSegmentScopeContract` 在 Windows 直接编译环境采集与关卡门控，检查风暴独立归属、共享熔岩目标、全局切关迷雾、分批初态和退役后的历史保留。使用 Unity 替身，不需要游戏资源。

```powershell
dotnet run --project tests/EnvironmentSegmentScopeContract/EnvironmentSegmentScopeContract.csproj -c Release -p:TreatWarningsAsErrors=true
```

`ReplayContract --scene-clock-only` 验证 Schema 22 场景真实采样时钟、独立分页、片段平移和有界远跳；完整回归还包含活动 30 Hz 调度、帧间隔抖动及休眠检查。`ActorNetworkContract` 直接编译队友骨骼采集与 Unity 替身，验证实际网络频率、状态边界和本地骨骼频率不变。`AudioSceneEventContract` 检查场景观察之间的短音效出生和零热路径分配；`EffectEventCaptureContract` 检查特效事件去重、独立运行身份及出生时钟；`SceneItemCadenceContract` 检查活动物品的骨骼、灯光、材质和休眠门控。

`AudioClipMetadataContract` 直接编译原生音频资源描述缓存，验证同一片段的固定属性只读一次、加载失败重试、对象替换和销毁。`NativeAppearanceContract` 另覆盖共享材质描述在单次采集内复用、下一轮重新观察与失败重试；这些替身检查不测量 Unity 主线程毫秒或声音听感。

`ReplayContract --colors-only` 验证 Schema 18 颜色语义、旧版兼容与独立跳转；`CratePlaybackContract` 直接编译箱子播放调度，使用 Unity 替身检查预算暂缓、跳转和恢复，不替代游戏内 GPU 画面检查。

`ReplayContract --creature-dormancy-only` 验证 Schema 20 生物实际观察时钟、甲虫睡眠与蜘蛛挂网事件休眠、同状态受击、片段时间平移和有界独立页跳转。`NativeCreatureObserverContract` 用真实 HarmonyX 验证原生方法完成后的通知及缓存状态适配器，不模拟 Unity 渲染或游戏 FPS。

Schema 21 原生待机与幽灵主体的数据契约随完整 `ReplayContract` 运行，覆盖停采时钟、动作切换、损坏描述符拒绝、片段平移和独立页反向跳转。`CreatureIdleAnimationContract` 在 Windows / net472 中用真实 HarmonyX 字段访问及 Unity 替身验证原生 clip 准入、绝对时间求值、未知资源继续采样和只能操作视觉副本。`NativeSpiderIdleLineContract` 验证克隆腹部驱动蛛丝、原生曲线复制与无历史重建；`StaticPlaybackContract` 验证暂停、换页、预算暂缓、动作切换和池复用中的待机缓存失效。

`ItemPlaybackContract` 和 `StaticPlaybackContract` 直接测试物品、放置物、互动实体及生物的生产播放调度，覆盖换页预算暂缓、暂停续作、跳转与池复用重置，以及不断变化的外观仍能推进。

以下可执行测试项目不需要游戏安装或游戏资产，可以逐个运行：

```powershell
dotnet run --project tests/ReplayContract/ReplayContract.csproj -c Release
dotnet run --project tests/NativeAppearanceContract/NativeAppearanceContract.csproj -c Release
dotnet run --project tests/CratePlaybackContract/CratePlaybackContract.csproj -c Release
dotnet run --project tests/ItemPlaybackContract/ItemPlaybackContract.csproj -c Release
dotnet run --project tests/StaticPlaybackContract/StaticPlaybackContract.csproj -c Release
dotnet run --project tests/NativeSpiderIdleLineContract/NativeSpiderIdleLineContract.csproj -c Release
dotnet run --project tests/ActorNetworkContract/ActorNetworkContract.csproj -c Release
dotnet run --project tests/AudioSceneEventContract/AudioSceneEventContract.csproj -c Release
dotnet run --project tests/AudioClipMetadataContract/AudioClipMetadataContract.csproj -c Release
dotnet run --project tests/EffectEventCaptureContract/EffectEventCaptureContract.csproj -c Release
dotnet run --project tests/SceneItemCadenceContract/SceneItemCadenceContract.csproj -c Release
dotnet run --project tests/NativeParameterCadenceContract/NativeParameterCadenceContract.csproj -c Release
dotnet run --project tests/ReplaySceneBudgetContract/ReplaySceneBudgetContract.csproj -c Release
dotnet run --project tests/NativeLoadingContract/NativeLoadingContract.csproj -c Release
dotnet run --project tests/ReplayViewContract/ReplayViewContract.csproj -c Release
dotnet run --project tests/ReplayNameplateContract/ReplayNameplateContract.csproj -c Release
dotnet run --project tests/ReplayAudioSpatialContract/ReplayAudioSpatialContract.csproj -c Release
dotnet run --project tests/TrajectoryContract/TrajectoryContract.csproj -c Release
dotnet run --project tests/TrajectoryUploadContract/TrajectoryUploadContract.csproj -c Release
```

GitHub Actions 在 Ubuntu 和 Windows 上安装 .NET 10 和 .NET 8，分别构建并运行独立测试项目；六个 net472 原生调用契约仅在 Windows 运行。CI 不构建包含游戏 DLL 引用的生产解决方案。Windows 覆盖 Windows 专用的文件操作检查，Linux 会跳过部分系统相关检查，两平台的通过数量可能不同。

测试验证数据格式、回放时钟、调度和资源配置；涉及 Unity 的项目使用小型替身。实际画面、声音与性能仍需在游戏内录制和回放验收。

`NativePlacementActivityContract` 使用 Windows / net472 与 Unity 替身，直接编译原生旗帜、生长物和脆岩钉的活动检测及灯光调度，验证 Scene tick 抖动不丢周期、同时间去重和静置后停止读取。

Windows 另有 `dotnet run --project tests/NativeCreatureObserverContract -c Release`，目标为 .NET Framework 4.7.2，使用与游戏相同的 HarmonyX 2.9 对受控协程实际打补丁，验证陷阱完成通知的顺序及异常隔离。它不依赖游戏安装，不模拟 Unity 画面；CI 仅在 Windows 运行，旧 Harmony 的 detour 不适用于 .NET 8 测试进程。

该项目也直接编译童军长活动门控，覆盖待命、原生传送开始/完成、目标丢失、半途开始录制、重复启停和退订；6000 次待命查询不重复查组件或读取位置。AppDomain 分配计数仅输出观测值，因包含其他线程而不作为硬断言。`ReplayContract --scoutmaster-lifecycle-only` 另外覆盖真实增量、独立共享页和分页跳转中的生物出生、消失与再次出现。

`dotnet run --project tests/NativeItemObserverContract -c Release` 同样仅在 Windows 运行，使用 .NET Framework 4.7.2 / HarmonyX 2.9 对与原生签名匹配的替身实际打补丁，覆盖物品生命周期、物理与网络唤醒、烹饪、碰撞、范围爆炸、背包和异常隔离。该项目直接编译生产 `ItemEventCapture.cs`，不验证 Unity 物理、画面或帧率。

物品休眠、对象节点差分和共享块随机跳转的专项测试位于 `ReplayContract`。可在参数 `--` 后分别传入 `--items-only`、`--nodes-only`、`--shared-only`；完整回归无需参数。离线抽样对比可传入 `--event-storage-inspect <原录像路径> <新的输出目录>`，只读原录像并将抽样录像及报告写入新目录；计时不代表游戏帧率。

`--joint-times-only` 验证骨骼时间分组与旧格式等价；`--inspection-helper-only` 验证对照工具的时间覆盖与时钟核验。离线对比加第四个参数 `--continuous` 会选择 0、300、900、1800 秒附近各约 30 秒的完整相交页，报告排除空档后的实际覆盖时长。预期人物关节由冻结的 0.8.11 编码器生成，采样时钟另做逐位比较；反向页与随机页均核对动作和独立特效历史，计时不包含这些核对工作。

`--paired-decode-inspect <旧抽样录像> <新抽样录像> <新的输出目录>` 在同一进程中交替读取相同的 12 个代表页，每页三轮，输出 `paired-decode.json`。两份录像须有相同的帧数、时间和页边界。各格式先完整读取所选页一次，首次触达结果单独保留；计时与线程分配只包含读取，并记录自然 GC 的暂停。全部计时结束后再核对字段及骨骼采样时刻，不清除系统文件缓存，也不强制垃圾回收。这是离线解码对照，不是冷盘延迟或游戏帧率测试。

`--paired-versioned-decode-inspect <旧版测试 DLL> <旧抽样录像> <新抽样录像> <新的输出目录>` 用隔离加载的旧版读取器和当前读取器做相同的配对对照，记录两份测试 DLL 的校验值。此处使用独立测试程序的 `ReplayContract.dll`，不加载游戏 Mod DLL。计时循环直接调用预建委托，不包含反射调用或数据比较；完整字段和骨骼时钟在计时后单独核对。

## 发布工具

维护者的一键构建、打包、Release 草稿及公开预发布流程见 [发布说明](releases.md)。`ReleaseVerifier` 使用 PE 元数据读取 DLL，不加载或执行游戏程序集；它的合成负面检查也在 Windows／Ubuntu CI 中运行：

```powershell
dotnet run --project tools/ReleaseVerifier/ReleaseVerifier.csproj -c Release -- self-test
```
