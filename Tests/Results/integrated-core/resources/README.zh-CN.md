# 安全资源与 ICC 测试记录

日期：2026-10-22。使用 Unity 2018.4.36f1 自带 Mono/Roslyn、C# 6、`-warnaserror+`。本测试只调用纯托管核心，不启动 Unity，也不执行 GPU/Photoshop GUI。

实际结果：

- **76 项 C# 资源断言通过**，日志 `resource-tests.log`。
- **2 个 ICC profile 独立解析通过**：实际 LittleCMS sRGB fixture，以及从其矩阵/白点数据构建的 sampled sRGB TRC profile，由 Pillow/LittleCMS 重新读取。
- **2 个 PSD 独立验证通过**：Raw 与 RLE，使用 psd-tools 1.25.0，记录见 `independent-resources.log`。

有效 profile 的识别不依赖名字：修改 sRGB 描述文字仍通过；保留 sRGB 描述但换为 Adobe-RGB-like 色度矩阵则拒绝，gamma 2.2 近似也不会当成精确的分段 sRGB。测试同时覆盖错误 header、PCS/色彩模式、版本、重复 tag、部分重叠、不对齐、越界、超大计数、LUT、错误白点、坏 parametric curve、非单调 table、粗糙两端点 table 和截断数据。

资源测试覆盖 ResolutionInfo、ICC、PixelAspect 的白名单和数值范围。输出 PSD 经独立实现检查：ICC payload 完全一致，300×144 dpi、1.25 像素比例、VersionInfo 保留，原始图层 RGBA 和存储 merged 白底通道字节精确一致，独立图层重新合成在既定量化容差内。另验证非法 ICC 或重复资源会在文件写入前拒绝，已有输出不变、不留临时文件。

复现（先在自己的 Python 环境安装 `Tests/Standalone/requirements.txt`）：

```bash
UNITY_EDITOR_PATH="/path/to/Unity/Editor" \
  bash Tests/IntegratedCore/run-resource-tests.sh
```

本次实际使用 `PYTHONPATH=/workspace/work/python-deps`。Python、Unity 安装目录属于测试环境，不是导出插件的运行依赖。
