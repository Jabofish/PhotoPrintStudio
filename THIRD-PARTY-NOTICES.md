# 第三方许可与来源

[MIT](LICENSE) 适用于光影印坊的自有代码和文档。以下第三方组件保留原许可及版权。便携 ZIP 附带本清单、模型许可和已分发库的许可声明。

## 离线模型

源代码仓库以 [模型清单](assets/models/models.manifest.json) 记录指定发布或提交、文件名与 SHA-256。构建脚本下载并校验这些文件。便携包按以下条目分发对应的权重。

| 便携包文件 | 上游来源及版本 | 许可 |
| --- | --- | --- |
| `pas-photo-matting-v1.onnx` | didik-maulana 的 [Pas Photo Matting v1.0.0](https://github.com/didik-maulana/pas-photo-matting/releases/tag/v1.0.0)，上游文件名为 `modnet_v2.onnx)。该版本的许可范围见[上游说明](https://github.com/didik-maulana/pas-photo-matting#licensing)。 | [Apache-2.0](assets/licenses/Pas-Photo-Matting-Apache-2.0.txt) |
| `hivision_modnet.onnx` | [HivisionIDPhotos ONNX 模型](https://huggingface.co/TheEeeeLin/HivisionIDPhotos_matting/tree/034769305faf641ad94edfac654aba13be06e816)，基于 [ZHKKKe/MODNet](https://github.com/ZHKKKe/MODNet)。 | [Apache-2.0](assets/licenses/Apache-2.0.txt) |
| `birefnet-lite-matting-768-fp16.onnx` | ZhengPeng 的 [BiRefNet](https://github.com/ZhengPeng7/BiRefNet)，使用 [FixedSeed/lattice-vision 的 768 FP16 ONNX 转换](https://huggingface.co/FixedSeed/lattice-vision/tree/05b78163725b64f55203468f26117cffd2d0de0b)。[上游权重许可说明](https://github.com/ZhengPeng7/BiRefNet/issues/316)。 | [MIT](assets/licenses/BiRefNet-MIT.txt) |
| `face_detection_yunet_2023mar.onnx` | Shiqi Yu 的 YuNet，使用 [OpenCV 发布的 ONNX](https://huggingface.co/opencv/face_detection_yunet/tree/3cc26e7f1014a5ee5d74a42acee58bafc9d0a310)。 | [MIT](assets/licenses/YuNet-MIT.txt) |
| `face_landmarks.onnx` | Google [MediaPipe Face Mesh](https://github.com/google-ai-edge/mediapipe)，使用 [naklitechie 转换的 478 点 ONNX](https://huggingface.co/naklitechie/face-landmarks-onnx/tree/575c33816c840c5b156398adb485e4f8d138adc2)。 | [Apache-2.0](assets/licenses/Apache-2.0.txt) |

## 运行依赖

| 组件 | 来源与许可 |
| --- | --- |
| .NET Runtime、Windows Desktop Runtime 和 WPF | [.NET Runtime](https://github.com/dotnet/runtime)、[WPF](https://github.com/dotnet/wpf)，MIT，含第三方声明 |
| CommunityToolkit.Mvvm | [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet)，MIT，含第三方声明 |
| SkiaSharp、SkiaSharp.NativeAssets.Win32 | [SkiaSharp](https://github.com/mono/SkiaSharp)，MIT，底层 Skia 和其他原生组件许可见包内第三方声明 |
| Microsoft.ML.OnnxRuntime、Microsoft.ML.OnnxRuntime.Managed | [ONNX Runtime](https://github.com/microsoft/onnxruntime)，MIT，含第三方声明 |
| System.Numerics.Tensors（ONNX Runtime 间接依赖） | [.NET Runtime](https://github.com/dotnet/runtime)，MIT，含第三方声明 |
| MetadataExtractor | [Metadata Extractor for .NET](https://github.com/drewnoakes/metadata-extractor-dotnet)，[Apache-2.0](assets/licenses/MetadataExtractor-Apache-2.0.txt) |
| XmpCore（MetadataExtractor 间接依赖） | [Drew Noakes 等维护的 .NET 移植](https://github.com/drewnoakes/xmp-core-dotnet/tree/6.1.10.1)，最初由 Yakov Danila 和 Nathanael Jones 移植，使用 [Adobe XMP SDK 的 BSD 许可](assets/licenses/XmpCore-BSD-3-Clause.txt) |

实际 NuGet 版本见项目内的锁定文件。测试和性能基准的依赖仅用于开发验证，不随便携版分发。包内的 .NET WPF 原生文件来自 Windows Desktop Runtime；系统 Visual C++ x64 Redistributable 由 Microsoft 单独提供。

## 截图和名称

文档截图中的示例人像和风景由项目内的绘图代码生成。纸张、打印机和相纸格式名称用于说明兼容的尺寸、设备或用途，相关商标归各自权利人所有。
