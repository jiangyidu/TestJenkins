# Prefab to PSD Integrated 2.0.2

适配 Unity 2018.4.36f1 / .NET 4.x Equivalent，仅用于编辑器 UGUI。

1. 从旧版升级时，用本版完整 `Assets/PrefabToPsd` 内容覆盖旧插件目录；旧插件在 `Assets/ThirdParty/PrefabToPsd` 时覆盖原位置，不额外安装第二份脚本，也不要复制旧 DLL。
2. 等待编译并先运行 `Tools > Prefab to PSD > Run Self Tests`。通过后打开 `Export Window`，拖入有 RectTransform 的完整 UI 根或 Prefab。
3. 选择当前外观、美术交付、回导结构或严格原稿参考预设，确认画布尺寸。
4. 刷新只读预检；保持“提交前校验 PSD”开启，然后导出。
5. Project 多选 Prefab 后，可用窗口当前配置批量导出；重名文件会分配稳定后缀，取消停止后续项。
6. 本机 GPU 自测结果在工程 `Temp/PrefabToPsdSelfTests`。九宫格PPU测试失败时保留 `self-test-report.txt`；若生成，也一并提供 `sliced-ppu.diagnostics.txt`、`sliced-ppu.preview.png` 和 `sliced-ppu.psd`。若在更早的合成图阶段失败，则提供相应的 `composite-rle` 诊断文件。
7. `Create Demo Prefab` 创建 640×480 示例。

文字、九宫格、遮罩与效果导出为像素层；文字原文与布局保存在 JSON。结构预设的文字使用 `.img`，不会生成 Photoshop 原生文字，也不会把已烘焙九宫格标为 `@slice`。

原稿参考不改当前层序；像素只有边界和逐字节 RGBA 完全一致时才复用，不使用字体/颜色近似匹配。参考 PSD 不能作为输出文件。仅继承已校验的 Resolution、sRGB ICC 和1.0像素比例。

隐藏 Graphic 的像素不导出，状态和布局写入 JSON；有可见内容的 Prefab 可保留空文件夹。根变换按指定设计画布归一，所选子树之外的祖先遮罩与 CanvasGroup 不包含。

提交前先读回临时 PSD 校验，失败或取消不覆盖旧文件。JSON 在 PSD 之后独立提交；两者不是联合事务。

v2.0.1 加入半透明红色UI正控及隐藏后全透明负控，只使用通过校准的相机捕获路径；浮点纹理同时检查渲染和混合能力，必要时回退至 ARGBHalf。校准失败或源合成图全透明时停止写入，不把空PSD当作成功。

v2.0.2 修正非默认Canvas PPU九宫格边框：在克隆和停用Scaler前按原层级保存PPU意图，渲染副本时恢复；场景运行时PPU和停用／原嵌套Scaler不会被强套资产配置。原8像素边框断言与容差保留，并增加资产200PPU、场景200PPU及运行时300PPU测试。

正常使用不需要 Python、Photoshop、原 PsdTOUgui 或第三方 PSD DLL。需要有效 Unity 授权与浮点纹理图形设备；批处理不要使用 `-nographics`。

本环境的真实程序集编译与托管测试结果见源码 ZIP 的 `Docs/TestReport.zh-CN.md`；九宫格PPU说明见 `Docs/CanvasPpuFix.zh-CN.md`，历史空白捕获说明见 `Docs/BlankCaptureFix.zh-CN.md`。用户截图证明v2.0.1已推进到九宫格边框检查；它不证明后续全部自测或业务PSD通过。本环境没有执行GPU和Photoshop GUI，升级后请运行目标机自测。

完整源码 ZIP 包含 README、融合变更、实现说明、测试报告及复现脚本。
