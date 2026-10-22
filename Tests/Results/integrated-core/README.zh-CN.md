# 融合版本纯托管核心测试

日期：2026-10-22。目标工具链为 Unity 2018.4.36f1 自带 Mono、MCS/Roslyn，语言级别 C# 6，警告视为错误。未启动 Unity 编辑器或 Photoshop。

统一重放脚本实际成功执行：`Tests/IntegratedCore/run-integrated-core-tests.sh`。完整输出见 `full-run.log`，机器记录与源码输入哈希见 `summary.json`。

| 项目 | 实际通过结果 | 记录 |
| --- | --- | --- |
| 原 PSD writer/raster/PackBits 回归 | 267 项断言 | `baseline/core.log` |
| 严格 reader、预提交验证及原子回滚 | 103 项断言 | `reader/results.log` |
| 安全资源与 ICC | 76 项断言 | `resources/resource-tests.log` |
| psd-tools 独立基础 PSD | 5 个文件 | `baseline/interop.log` |
| psd-tools 独立资源 PSD | 2 个 Raw/RLE 文件 | `resources/independent-resources.log` |
| Pillow/LittleCMS 独立 ICC | 2 个 profile | 同上 |

合计 **446 项纯托管断言**，不应解释为 446 个 Unity 场景或 GPU 测试。

新增 reader 检查 Raw/RLE、Unicode 中文与 emoji、层级顺序、隐显、opacity、bounds、原像素及 merged 白底编码。拒绝截断、异常尺寸/记录/section、重复正 ID、不平衡组、非法 UTF-16、坏 PackBits、行表和通道长度不一致、超预算解码及未知像素压缩。参考 Unicode 名称允许合法尾 NUL 终止符，拒绝中间 NUL。

复用资格回归覆盖隐藏层、祖先隐藏/透明度、clipping、矢量蒙版、效果和智能对象，避免绕过烘焙语义。

`PsdWriter.Write` 新增的临时文件验证回调发生在最终提交之前。测试同时验证显式/自动 layer ID、完整文件读回，以及回调异常或故意损坏 merged alpha 时旧输出不变、临时文件清理。`PsdOutputVerifier` 比较图层原始 RGBA 和存储 merged 白底通道，独立 psd-tools 则避免仅由同一实现自证。

资源规则详见 `resources/README.zh-CN.md`。ICC 认可基于 sRGB 数值矩阵/白点/TRC，不依据描述名称；负例中的 Adobe-RGB-like 是构造的非 sRGB 矩阵变异，不是假称使用真实 Adobe RGB 配置文件。

诊断说明：`reader/malformed*.psd`、`eligibility.psd`、`protected.bin` 等是故意构造的错误/保护输入，不是供导入使用的生产 PSD 示例。有效验证文件为 `reader/verified-Raw.psd`、`verified-Rle.psd`，以及基础和资源 fixtures。

复现：先安装 `Tests/Standalone/requirements.txt`，再从交付根目录执行：

```bash
UNITY_EDITOR_PATH="/path/to/Unity/Editor" \
  bash Tests/IntegratedCore/run-integrated-core-tests.sh
```

本轮实际使用 `UNITY_EDITOR_PATH=/workspace/work/toolchain/Editor`，`PYTHONPATH=/workspace/work/python-deps`，`PYTHON=python`。这些是验证环境选择，不是插件运行依赖。
