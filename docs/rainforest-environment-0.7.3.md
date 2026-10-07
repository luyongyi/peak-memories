# 雨林环境录制中断排查与修复

日期：2026-10-07，游戏 Build 25667990，原 Mod 0.7.2。异常为 `capture-interrupted: Invalid recorded environment state.`。

## 故障证据

一份未完成录像样本（文件名已匿名）已逐页只读检查：25,022 个已接受帧、44 页、424.000661 秒全部可读；原日志显示 accepted=written=25,022，unwritten=0。录制在 416.303507 秒切入段 1，约 7.697 秒后因环境校验异常封存为 partial。

原文件 SHA256：`DD575590D14D7DC9F1FDDFEE9536EBA231C32DA951C2BB9AD74BF422FAF7B3D8`，检查前后一致。最后 30 帧的雾 Enable=1、Reveal=0、Origin=0、Moving=false、Arrived=true；原拒绝样本未写入，具体现场数值无法恢复。随后通过原生协程、真实资源曲线及已安装 DLL 校验器复现了同一异常。

完整环境范围报告（本地证据：`local/checks/rainforest-env-0.7.2/result.json`，不随源码分发）、末尾 30 帧（本地证据：`local/checks/rainforest-env-0.7.2/last-30-frames.json`，不随源码分发）、原日志（本地证据：`local/checks/rainforest-env-0.7.2/LogOutput-original.log`，不随源码分发）、原性能报告（本地证据：`local/checks/rainforest-env-0.7.2/performance-original.json`，不随源码分发） 均保留。

## 原生边界调查

根因是原版揭雾曲线允许 Enable 轻微超过 1，而原 Mod 强制要求 0～1。只读提取安装游戏 `data.unity3d`，22 个关卡的 fogFadeCurve 完全相同：第一段两个端点都是 1，第二个端点入切线为 -0.0771202743。未加权 Hermite 插值在该段内部严格大于 1，峰值 1.0011342。`WaitForReveal()` 第一帧先累加 deltaTime，再直接写曲线结果：40 FPS 时 Enable≈1.0000185，60 FPS 时≈1.0000083。

直接反射调用已安装 0.7.2 DLL 的真实校验器，Enable=1 通过，上述两个值和曲线峰值都抛出原始同一异常。这条链解释了记录停在 Enable=1、Reveal=0：下一次正常揭雾观察即越过旧校验上界；没有必要假定先发生一段下降轨迹。

原版 MapHandler 的换段协程先增加段号，再等待雾缩至玩家附近，等待 1 秒并切换区域，随后等待 0.5 秒才开始 WaitForReveal，最后更新雾 Origin。实际录像同样先在 416.3035 秒变为段 1，421.8866 秒雾尺寸降至 29.9953，424.0007 秒前 Origin 仍为 0；与正常揭雾首次采样遭旧校验拒绝的时序吻合。

另有原生 `DisableFog()` 在 while(c<t) 内先累加时间再写 ENABLE=1-c/t，结束前一帧可短暂为负。游戏最大帧时长 0.166666657 秒时，此路径下界约 -0.166667。Inspector 的 `[Range(0,1)]` 属性只约束编辑器输入，不约束这些运行时写入。实际 Reveal 曲线为 2t−t²，在 0～1 内，因此保留 Reveal 原有范围。

原始资源曲线（本地证据：`local/checks/rainforest-env-0.7.2/range-audit/fog-curves-assets.json`，不随源码分发）、曲线计算（本地证据：`local/checks/rainforest-env-0.7.2/range-audit/fog-curves-evaluation.json`，不随源码分发）、原已安装 DLL 复现（本地证据：`local/checks/rainforest-env-0.7.2/range-audit/installed-validator-reproduction.json`，不随源码分发）。这不是实时 Unity 运行测试，现场失败帧的精确数值仍不可恢复。

风进度及风倒计时实际为原生 Clamp01 后的比例，雨雪因子与 GlobalWind 也经过 Clamp01／Lerp；此次没有证据需要改这些字段的语义。新的校验应保留合法有限过渡值，同时继续拒绝 NaN、Infinity、异常巨大值、坏向量、重复来源及无效观察时钟。

## 修复与验证

0.7.3 保持 Schema 13、原主采样最高 60 Hz、环境连续采样 20 Hz、原队列及页面限额。Enable 使用有界 [-1,2] 过冲包络，保留真实值，覆盖原生上冲及关闭时负值；Reveal 仍要求 0～1。异常消息补充字段、数值、范围与来源，失败诊断 JSON 保存最后一次原始环境观察，日志保留调用栈。诊断仅在失败分支格式化或异步写入。

Release 构建使用 `-warnaserror`，0 警告、0 错误。674 项检查全部通过：ReplayContract 607、ReplayViewContract 26、NativeLoadingContract 11、NativeAppearanceContract 30。新增 3 项回归涵盖实际 40／60 FPS 曲线值、曲线峰值、关闭末帧负值、codec 精确保真、生产 writer 跨段／分页／重基准保存及坏数值拒绝。原有“坏雾强度”用例按新文档包络调整为 2.01，仍要求拒绝。

另直接反射调用最终构建的 0.7.3 DLL，12 个用例通过：原生 30／40／60／120 FPS 首帧、曲线峰值及合法负末帧被接受，校验前后 float32 位完全一致；±100、NaN、Infinity 被拒绝，且包含新诊断字段。候选程序集 SHA256：`89642BEA840AAC9B813D63BF83203FA3AAEFAD1D0A15E1969FFFEAE4D3458FFF`。旧版与新版实际 DLL 对照（本地证据：`local/checks/rainforest-env-0.7.2/native-range-repro.json`，不随源码分发）

构建日志（本地证据：`local/checks/rainforest-env-0.7.2/release-build-0.7.3.log`，不随源码分发）、主契约（本地证据：`local/checks/rainforest-env-0.7.2/ReplayContract-0.7.3.log`，不随源码分发）、视图（本地证据：`local/checks/rainforest-env-0.7.2/ReplayViewContract-0.7.3.log`，不随源码分发）、加载（本地证据：`local/checks/rainforest-env-0.7.2/NativeLoadingContract-0.7.3.log`，不随源码分发）、外观（本地证据：`local/checks/rainforest-env-0.7.2/NativeAppearanceContract-0.7.3.log`，不随源码分发）。

最终 0.7.3 数据读取器重新逐页读取原故障录像，25,022 个唯一帧、44 页均通过；原 SHA256 不变，partial 仍标记未完成。最终旧录像读回（本地证据：`local/checks/rainforest-env-0.7.2/final-reader-0.7.3/result.json`，不随源码分发）

这些是实际资源／DLL 校验与生产数据契约检查，尚未在 Unity 内重新走完整雨林，因此不能宣称实机重测通过。

## 安装

已在游戏关闭时安装 0.7.3 至 `C:/Program Files (x86)/Steam/steamapps/common/PEAK/BepInEx/plugins/PeakReplayLab.dll`。版本 `0.7.3.0`，安装后 SHA256 与上述最终构建一致。

原 0.7.2 DLL、配置、故障日志和性能报告已备份在本机忽略的 `local/archives/` 目录。配置、另一个录制插件、五份原录像、保留的故障日志与性能报告前后哈希一致。安装记录保留在本机，不随源码分发。

## 本次实机性能观察

原故障诊断中，Capture 共 25,023 次：平均 0.868 ms，最近 256 次的 P95=1.571 ms、P99=2.994 ms，累计最大 135.965 ms。这一次的平均与 P95 低于 2／4 ms 目标，但存在明显尖峰，且录制发生异常，不能作为整体验收完成。

最近 8,192 个录制时渲染帧的平均 FPS=206.04、1% low=38.04、0.1% low=14.28；没有停录对照样本，不能把尖峰全归因于 Mod，也不能断言 low 帧已经改善。Unity 返回的线程分配计数全为 0，不能据此声称实际零分配。
