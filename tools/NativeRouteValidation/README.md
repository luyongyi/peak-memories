# 原生路线自动验证工具

这是一套显式启用的本地测试工具：驱动真实 PEAK 角色传送、普通开箱、篝火交互与定点等待，观察原生事件，并核对独立保存的完整录像。它不属于正式模组，也不随日常游戏自动运行。测试伴随插件只有收到 `--peak-route-label` 等完整参数才启用。

路线采用游戏实际烘焙的 `Level_n`，不在运行中强制切换地图变体。`Level_0` 为 Shore → Tropics → Mesa → Volcano → TheKiln，`Level_9` 为 Shore → Roots → Alpine → Swamp。测试覆盖实际观察到的实例和成功触发的事件；未出现的生物、未完成的行为及未知关联均不能算通过。完整性清单见 [coverage-oracle-checklist.md](coverage-oracle-checklist.md)，原生调用设计见 [native-route-audit.md](native-route-audit.md)。

## 准备

需要已安装并可正常启动的 PEAK、Steam/BepInEx、待测的 `PeakReplayLab.dll`、.NET 8 SDK、PowerShell 7 和 Python 3。Python 脚本仅使用标准库。项目从仓库根目录的 `Directory.Build.props` 继承 `PEAKGameRootDir` 和 `ManagedDir`；构建时使用游戏本机程序集，不分发游戏代码或资产。

先退出 PEAK，在仓库根目录构建测试伴随插件：

```powershell
dotnet build tools/NativeRouteValidation/driver/RouteAuditProbe.csproj -c Release --nologo -v quiet
```

默认游戏路径为 `C:\Program Files (x86)\Steam\steamapps\common\PEAK`。安装在其他位置时，构建和启动都使用同一游戏目录；构建属性末尾保留斜杠：

```powershell
dotnet build tools/NativeRouteValidation/driver/RouteAuditProbe.csproj -c Release --nologo -v quiet -p:PEAKGameRootDir="D:/SteamLibrary/steamapps/common/PEAK/"
```

产物仍为 `artifacts/bin/RouteAuditProbe/release/PeakReplayLab.LocalRouteAudit.0827.dll`。`0827` 是测试工具标识，实际待测正式模组版本和 MVID 由每次会话记录。驱动引用游戏中已安装的正式 DLL；测试前应核对它与计划验收的版本一致。

## 启动受保护的路线

每次使用全新的 `Label`；工具拒绝覆盖同名 `sessions` 或 `launch-*` 目录，也拒绝在 PEAK 已运行时启动。

```powershell
& tools/NativeRouteValidation/launch-route.ps1 -Label route-level0-example01 -Scene Level_0 -FirstWait 150 -LaterWait 45 -FarWait 45 -Biomes 5
```

森蕈、雪山和雾沼的变体路线示例：

```powershell
& tools/NativeRouteValidation/launch-route.ps1 -Label route-level9-example01 -Scene Level_9 -FirstWait 30 -LaterWait 45 -FarWait 45 -Biomes 4
```

`FirstWait` 为首关基线停留秒数，`LaterWait` 为后续关基线停留，`FarWait` 为离开事件地点后的停留。最后一关等待 `max(FarWait, FinalWait)`，`FinalWait` 默认 120 秒；身份短复查可显式设成 15 秒，但不能据此宣称长时内存或增长稳定。`ObserveFromIndex` 可取 0–3，跳过更早关卡的定点观察，仍通过原生篝火逐关推进；这不能替代被跳过关卡的完整测试。

`-FaunaVisual` 显式开启青蛙／乌鸦／EarlyWorm 原生动画对照，输出 `fauna-visual.json`。每个状态只复制一次初始子节点及形变权重，后续用冻结基线重建当前姿态，不能通过复制当前原生骨骼取得假阳性。探针在同一原生帧比较位置、旋转、缩放、显隐、世界根及每项 blend-shape，并检查源对象权重、材质引用和着色器数值未被回放写入。等待阶段通过原生近身传送尝试乌鸦惊飞，随后以原生拾取和烹饪 API 验证 Worm 地面、手持、熟制三态；建议最后一关 `FinalWait` 至少 40 秒。未触发状态不算通过。此模式会增加测试工作和改变玩家位置，`PerformanceMeasurementValid=false`，不能将该次 CPU／体积数据用于稳态性能对比。性能路线应另跑且不带此开关。

启动器支持以下路径参数：

| 参数 | 含义 |
|---|---|
| `GameRoot` | 游戏安装目录，须包含 `PEAK.exe` 和 BepInEx |
| `SavePath` | 当前游戏实际使用的 `quicksave.peak`，默认当前用户的 `AppData/LocalLow/LandCrab/PEAK/quicksave.peak`；用于备份和哈希前置校验，不会改变游戏的存档位置 |
| `RepositoryRoot` | 源码仓库根目录；默认从工具目录向上寻找 `src/PeakReplayLab/PeakReplayLab.csproj` |

当前原生存档必须已存在，探针会将游戏自己的存档路径哈希与启动器提供值比对；传入其他文件不会绕过保护。路径参数示例：

```powershell
& tools/NativeRouteValidation/launch-route.ps1 -Label route-custom-example01 -Scene Level_9 -Biomes 4 -RepositoryRoot "C:/Work/PeakMemories" -GameRoot "D:/SteamLibrary/steamapps/common/PEAK" -SavePath "C:/Users/Example/AppData/LocalLow/LandCrab/PEAK/quicksave.peak"
```

启动器优先使用工具目录 `driver/PeakReplayLab.LocalRouteAudit.0827.dll` 中显式随包提供的伴随 DLL；无该文件时使用仓库 `artifacts` 产物。源码仓库不提交 DLL。

启动时备份快速存档与 BepInEx 配置，测试中阻止存档/配置和成就写入，在独立离线录像目录执行路线。正常结束后恢复会话设置并核对存档、配置哈希。工具会实际替换游戏插件目录内同名测试伴随 DLL，所以应仅用于测试机上的受控会话。

`watch-route.ps1` 随启动器自动在隐藏窗口运行。连续 **120 秒没有新的心跳或阶段文件**，或总运行超过 1500 秒，会核对进程名、PID 和启动时间后终止本次 PEAK 进程，写出 `watchdog.json`，随后再次核对磁盘保护结果。进程内路线另有 1400 秒上限。强制退出必须算失败，即使已写出的部分录像仍可读；不能以 watchdog 结束代替正常封盘与恢复通过。

## 输出与隐私

每次测试输出都在本工具目录：

- `sessions/<Label>/`：动作与阶段、1 Hz 工作计数/内存、录像、原生盘点、`summary.json`，以及退出后的 `disk-protection-proof.json`。
- `launch-<Label>/`：启动元数据、游戏原始日志、`protected-before/` 存档与配置备份，异常时的 watchdog 证据。

这两个目录由本工具 `.gitignore` 排除。**原始日志、存档/配置备份和原始录像属于私人测试数据，不应随工具或报告分发**；日志可能含账号或服务鉴权信息。对外只提供审查过的汇总报告和工具源码。保留本地保护备份直至确认测试结束后的哈希一致。

测试完成并确认游戏已退出后，将 `PeakReplayLab.LocalRouteAudit.0827.dll` 从游戏 `BepInEx/plugins` 移到本工具的 `retired-probes/` 或其他本地留档目录。日常游戏仅保留正式 `PeakReplayLab.dll`；不要移动其他插件。

## 退出后分析

等待 `summary.json` 生成且 PEAK 完全退出，再做逐页重解码、体积分析及构建离线检查器，避免这些工作争用测量期间的 CPU。

```powershell
python tools/NativeRouteValidation/analyze-route.py tools/NativeRouteValidation/sessions/route-level0-example01
python tools/NativeRouteValidation/recording-composition.py tools/NativeRouteValidation/sessions/route-level0-example01/summary.json tools/NativeRouteValidation/sessions/route-level0-example01/analysis
dotnet build tests/ReplayContract/ReplayContract.csproj -c Release --nologo -v quiet
dotnet build tools/NativeRouteValidation/verify/RouteVerify.csproj -c Release --nologo -v quiet
dotnet tools/NativeRouteValidation/verify/bin/Release/net8.0/RouteVerify.dll tools/NativeRouteValidation/sessions/route-level0-example01/summary.json tools/NativeRouteValidation/sessions/route-level0-example01/analysis
```

对比两次已完整结束、包含至少 100 秒雾沼 `FarIdle` 的路线，可运行 `compare-fauna.py <before-session> <after-session> <output.json>`。该工具要求末期生物计数一致，并分别报告压缩文件稳态增长、采集调用耗时和压缩前家族字段速率；它不将压缩前字节解释为压缩后模块占比。

只统计初态结束后的稳定类型占比，可运行 `python tools/NativeRouteValidation/steady-composition.py <session-directory> <output-directory>`。工具使用 FarIdle 心跳的原生时间减去录像 FirstCapture，只选完全位于窗口内的独立页，并统计原始 UTF-8 字段、配套 EffectHistory 及相邻页 SharedThrough 之差对应的首次写入共享块；旧共享初态不重复摊入。原始分类保留在 TrackGroups，青蛙、EarlyWorm 和曼德拉草的物品条目合并归入生物。每页原始字节及物理 offset 连续性均校验守恒；压缩前类型占比与可精确相加的物理 gzip 块占比分开报告。

`analyze-route.py` 默认把报告写入会话 `analysis/`，也可追加输出目录参数。组成分析和离线检查器接受 `summary.json` 及输出目录两个参数；它们读取 summary 中的原始录像绝对路径，移动录像时须维护本地路径关联。

成功必须同时检查正常原生路线完成、存档/配置保护、完整封盘、accepted/written 帧数对账、全部页面读回、原生事件匹配、关卡归属及播放跳转状态。离线检查器要求 `summary.Success=true` 和 `info.Complete=true`，不会将一个健康的已写前缀判为成功完整录像。

`RecordedFrogs` 和 `HighUseItems` 保留不同身份、Uses 集合及首次/末次观察时间。当前 Swamp 的 FrogTongue 独立原生盘点出现时，还检查已知原生 Frog 的 100,000,000 Uses 夹具；这是高计数兼容性检查，不能解释为青蛙全部动作已覆盖。

## 指标解释

`SampledCapture` 只计实际接受录像帧的 `RollingCapture.Tick`；`Capture` 还包含未到采样时钟的快速返回调用。主线程诊断读取另计 `Probe`，后台序列化不等于采集本身。平均毫秒、窗口内最大值和原生工作计数应一起看，低平均值不能证明没有短卡顿。

被测关卡先等待准入目录可靠、Pending／DeferredOrigins／PendingRebind 为 0，各轨道初态完成并稳定 1 秒，再开始基线；90 秒仍未满足则失败。末期独立原生场景盘点记录 GameObject ID 与 Item 组件 Instance，验证器按端点附近的实际保存帧逐一核对；同名独立根路径不能作为唯一身份。旧数据缺少 Item.Instance 时，只有唯一的精确路径绑定才允许辅助关联，并明确标注其来源。

录像增长为实际后台写入进度；短阶段受分页封盘影响，最终体积以完整文件对账为准。`QueueBytes` 是包含最近共享状态基线及进行中写入的队列预算，不能当成全部待写积压；真实待写帧数量看 `QueueFrames`，收尾看 accepted/written 和 UnwrittenFrames。录制滚动缓存与回放按需解码缓存是不同预算。

物理主页面、独立特效历史、共享块和头尾索引的字节可精确相加。模块字段百分比是压缩前实际 UTF-8 编码字节占比，**不是压缩后文件的模块占比**；共享 gzip 字典使字段压缩体积无法直接相加。

组成分析还按物品原生 `Prefab` 和生物 `Kind` 统计主页面实体条目的实际 UTF-8 片段。家族身份从共享实体块和累计的主页面完整/差分条目解析；未知身份明确计入 `<unknown>`。此分项只计页面数组中实际存储的条目，排除共享块及数组外壳，不能直接与包含共享块的模块百分比混用。每家族的字段字节与对象外壳相加等于条目总量；`PoseNodesValueRawBytes`、`NonNullLineValueRawBytes` 是已包含在条目中的嵌套子集，不能再次累加。JSON 同时保留逐关家族量和未知率，以区分 Frog 姿态、VoiceThief 等实际来源。

内存包含游戏、其他模组和 GC 垃圾，不是录像系统独占量。盘点未匹配项、未知/全局物品归属和未真实触发的行为都需明列。`Passed=true` 仅表示该路线已执行断言通过，`CoverageComplete=false` 保留全游戏、全部生物动作和多人场景尚未完全覆盖的事实。
