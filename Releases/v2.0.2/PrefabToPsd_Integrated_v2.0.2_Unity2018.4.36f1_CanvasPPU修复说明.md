# v2.0.2 九宫格Canvas参考PPU修复

目标版本：Unity 2018.4.36f1，UGUI。日期：2026-10-22（Asia/Shanghai）。

## 新截图说明了什么

用户v2.0.1自测已通过红蓝像素、遮罩和CanvasGroup检查，并通过九宫格整体边界 `8,8 64x32`。当前失败点为8像素左边框：预期R=55，却读到中心颜色R=24。

这证明该次自测已经有实际可见像素，失败点从空白捕获推进到了非默认Canvas PPU。它不能证明后续全部自测、v2.0.2新增GPU用例或业务PSD已经通过。

## 已确认的原因

`Canvas.referencePixelsPerUnit` 是运行时native状态，不是Prefab中持久化的Scaler配置。测试资产保存了 `CanvasScaler.referencePixelsPerUnit=200`，但直接读该资产Canvas时，native值可能仍为默认100。旧代码快照了100；克隆挂入host后Canvas成为嵌套Canvas，随后Scaler被禁用，其配置200不能再应用。

Sprite边框为4像素、Sprite PPU为100时，Canvas PPU为200应得到 `4×200÷100=8` 个设计像素的边框。误用100后只剩4像素，因此距左边缘6像素的测试点采到中心色。整体矩形仍是64×32，尺寸检查不会发现这一问题。

## v2.0.2的处理规则

在实例化、重挂父节点和禁用Scaler之前，从源对象的原层级保存PPU：

- 持久化Prefab资产中，原本是根Canvas且Scaler启用时，按序列化配置解析。World Space、Constant Pixel Size及Scale With Screen Size采用参考PPU；屏幕Constant Physical Size采用“参考PPU×物理单位DPI÷默认Sprite DPI”。World Space按Scaler的World Canvas路径处理，不套用屏幕物理尺寸模式。
- 场景对象保留实际native PPU；Scaler停用或原本属于嵌套Canvas时，也不把Scaler配置强套到native值上。
- 保存的值恢复到host和对应Canvas副本，并在激活、捕获校准及渲染阶段恢复，避免Scaler的生命周期重置影响九宫格。源资产和场景对象保持不变。

原8像素边框的像素预期和容差没有降低。PPU诊断会记录源native值、Scaler配置、解析值和捕获Canvas值。

## 验证状态

v2.0.2统一回归六项任务均退出0：16个正式脚本、63个真实Unity程序集、C#6编译通过；751项托管断言、130项原导入器读取检查通过，另有7个独立PSD与2个ICC profile验证。新增48项PPU断言属于纯策略测试，没有启动Unity或读取真实GPU画面。

编辑器自测还新增三类GPU用例：Prefab资产200PPU的8像素边框、场景native200PPU的8像素边框、停用Scaler且runtime300PPU的12像素边框。这三类用例在本环境没有执行，仍需在发生故障的Unity机器验证。完整结果见 [测试报告](TestReport.zh-CN.md)。

## 升级与复测

当前项目插件位于 `Assets/ThirdParty/PrefabToPsd`。从新版源码或升级包取插件内容，覆盖该原目录，保留业务资产和已有导出文件；不要再在 `Assets/PrefabToPsd` 安装第二份同命名空间脚本，也不要用旧DLL代替源码。

1. 等待编译，执行 `Tools > Prefab to PSD > Run Self Tests`。
2. 确认报告开头的导出器版本为 `2.0.2`，检查三类PPU用例和后续测试结果。
3. 全部自测通过后，再导出原先的业务UI，并检查PSD预览及内容图层。

如果PPU测试仍失败，请提供工程 `Temp/PrefabToPsdSelfTests` 里的 `self-test-report.txt`，以及已生成的 `sliced-ppu.diagnostics.txt`、`sliced-ppu.preview.png`、`sliced-ppu.psd`。场景／runtime用例使用 `sliced-scene-ppu`／`sliced-runtime-ppu` 前缀，也请保留相关失败文件。没有运行到相应阶段时，图像文件可能尚未生成。

新版下载位于仓库 `Releases/v2.0.2/`：完整交付包为 `PrefabToPsd_Integrated_v2.0.2_Unity2018.4.36f1_Delivery.zip`，升级包为同前缀的 `_Upgrade.zip`。历史空白捕获处理保留在 [BlankCaptureFix.zh-CN.md](BlankCaptureFix.zh-CN.md)。
