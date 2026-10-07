# 0.7.4 回放姓名、瀑布与蜜蜂声音修复

本次针对截图中的姓名错位、雨林瀑布静止和远处蜂鸣持续三个问题。修复集中在回放呈现，录像格式仍为 Schema 13，继续读取 Schema 10／11／12／13，主录制与环境采样频率保持原配置。

## 姓名跟随

原版 `Misc/PlayerName/PlayerNamePos` 由 `FollowBodypart.LateUpdate` 跟随 Head，再由 `Billboard` 朝向镜头。回放只复制 Transform 与渲染器；这个 UI 分支没有进入骨骼采样，因此复制的姓名锚点留在模板位置。截图里还保留了原场景白色姓名：原版的淡出使用 `Time.deltaTime`，冻结时间后无法淡出。

现在使用已经应用录像姿态的头部世界位置，每次显示投影都重新计算，名字底部留出 0.55 米，遇到可见帽子时至少位于帽子包围盒顶部上方 0.12 米。隐藏、停用及销毁的帽子不会影响位置。

原版姓名在回放期间隐藏，原生 `IsLookedAt.Update` 与 `UIPlayerNames.UpdateName` 只在回放呈现阶段暂停，避免指向隐藏的现场人物以及执行人物装扮逻辑。退出恢复各原生姓名原来的 `activeSelf`；构造失败和单项恢复异常也会继续清理。文字复用原版字体、材质和颜色。

## 瀑布流水

真实 `Waterfall_Spline` 材质使用 `W/Peak_Waterfall`。反汇编确认它的流水 UV 读取 `_TimeParameters.x`；回放将 `Time.timeScale` 设为 0 后，原版 shader 时间也停止了。`WaterFallConfig` 只设置水幕落点，不包含流水动画更新。

修复接入安装版 URP 的实际 shader 时间写入函数，只替换时间参数，由原版渲染器继续生成所有 shader 时间向量。流水跟随当前回放时间，暂停和等待读页时冻结，倍速与前后拖动同步；各镜头使用同一相位。退出会撤销 hook 并恢复进入前的全局向量，后续原生渲染继续正常更新时间。

瀑布的水雾粒子已经由现有特效轨道管理，本次没有再增加模拟器。原生推力、伤害、碰撞和音频脚本没有因流水动画被启用。

**相位边界：** 旧录像没有记录原始 shader 起始相位，本次采用回放场景初始化相位加录像时间。能恢复流动与时间控制，但不能保证纹理花纹恰好对应录制时同一帧。

## 蜜蜂声音

原版 `BeeSwarm` 两条循环源使用自定义衰减曲线，而旧回放只恢复 `rolloffMode` 和最小／最大距离，遗漏了曲线。实际资产曲线在约 1.25 米为满音量、2.5 米为一半、5 米为四分之一，最大距离 250 米处为零；这些参数使用原版资源恢复。

现在从经过审核的原生游戏音源中，按音效资源和空间配置匹配完整曲线及原生混音设置。声音池分配时恢复配置，避免继承前一个音效的曲线。已有录像可以受益，无须为了这三处显示修复重新录制。

如果当前游戏资源缺失曲线或同一匹配出现冲突，会保留录制距离并采用线性衰减，同时给出有次数限制的提示。不会任意挑选冲突资源，也不会增加每帧场景扫描或录制热采样。

## 验证与实机验收

Release 编译 **0 警告／0 错误**，共 **697 项检查通过**：

| 检查组 | 通过数量 | 范围 |
| --- | ---: | --- |
| ReplayContract | 613 | 数据、轨道、队列及回放时钟，含新增 6 项流水时间检查 |
| ReplayViewContract | 26 | 相机及原生 UI 复制与恢复 |
| NativeLoadingContract | 11 | 原版加载状态与失败清理 |
| NativeAppearanceContract | 30 | 原生材质与灯光数据 |
| ReplayNameplateContract | 9 | 头部／帽子锚点、镜头投影、原版姓名隐藏恢复及异常清理 |
| ReplayAudioSpatialContract | 8 | 蜂鸣曲线完整复制、冲突匹配、池复用、缺失降级和原生资源不变 |

测试直接链接生产实现；UI／音频组件使用小型 API 双，未执行 Unity 渲染或 DSP。额外反射确认安装版 URP 时间方法的参数位置和类型与 hook 一致。

构建版本 `0.7.4.0`，DLL SHA-256：`369D19A7E58A99DB92CAC728AB0887D2418415E9A77F72206E43F26DE63673ED`。构建和测试日志位于 `local/checks/replay-presentation-0.7.4/`。

使用 **生产 0.7.4 DLL** 回读用户最新的 0.7.3 完整录像：112 页、64,872 个唯一帧、1,099.253 秒，所有页面以逆序独立解码通过；含 1,198,380 个音频样本，其中 726,981 个为自定义空间衰减。原文件 SHA-256 回读前后均为 `9667013F1318759EB961C96911CAFC4D495C695B5EC83A79D19CECA68E6236C4`，原录像未改写。证据：`local/checks/replay-presentation-0.7.4/recording-read-proof.json`。

已于 **2026-10-07 12:09:45（北京时间）**，确认 PEAK 退出后安装 `0.7.4.0`。安装文件位于 `C:/Program Files (x86)/Steam/steamapps/common/PEAK/BepInEx/plugins/PeakReplayLab.dll`，SHA-256 与上面的 Release 构建一致。所有已有录像、回忆文件、配置及其他插件的前后哈希均相同。

0.7.3 DLL、配置及安装前日志已备份到 `local/archives/peak-replay-lab-0.7.3-before-0.7.4-presentation-20261007-120945/`。安装证据：`local/checks/replay-presentation-0.7.4/installation-0.7.4.json`。重新启动游戏后加载新版；游戏内视觉和听觉验收尚未执行。

原版证据位于仓库 `local/checks/`：`nameplate-0.7.3/native-name-anchors.json`、`waterfall-replay-0.7.3/waterfall-investigation.json`、`shader-clock-0.7.4/installed-urp-binding.json`、`replay-audio-0.7.3/bee-audio-assets.json`。

代码检查与原版资产／程序集验证不能替代游戏内视觉和听觉验收。建议使用用户这份已有录像检查：

1. 播放、暂停和拖动时姓名保持在人物帽子上方；切换自由镜头后没有第二处原版名字。
2. 瀑布随播放流动，暂停停止；前后拖动与倍速生效，退出后游戏流水继续正常。
3. 跟随镜头和自由镜头靠近、远离蜂群时，声音按原版曲线衰减；离开后不会保留无距离衰减的蜂鸣。
