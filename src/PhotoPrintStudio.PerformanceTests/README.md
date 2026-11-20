# 预览性能回归验证

这个独立检查程序使用真实 WPF Dispatcher、MainViewModel、图像处理和 ONNX 模型，验证参数调整到预览提交的延迟。它不调用物理打印，也不需要新增 NuGet 包。

先获取模型，在独立的输出目录中运行。测试照片和 JSON 报告请保存在 Git 管理范围之外：

~~~powershell
pwsh -File scripts/fetch-models.ps1
$perfOutput = Join-Path $PWD 'artifacts/performance-check'
dotnet build src/PhotoPrintStudio.PerformanceTests -c Release -o $perfOutput
& (Join-Path $perfOutput 'PhotoPrintStudio.PerformanceTests.exe') '<测试照片绝对路径>' (Join-Path $perfOutput 'report.json') (Join-Path $PWD 'assets/models')
~~~

覆盖排版、单图、分屏对比，曝光连续调整、AI 换底、柔肤、清晰度、锐化、平移、素材切换和同图多格导出。检查期间不要同时运行构建或其他基准。

报告记录总耗时、设置参数耗时、最后一次调整后的等待时间、进程 CPU 时间和托管分配量。程序同时验证：

- 连续拖动有中间预览，最后一次参数的直方图与直接处理一致。
- 平移复用调色结果，切换素材不会发布旧上下文的结果。
- 同图连续多格导出只处理一次；连续 20 次调整的分配量有界。
- 五组独立图像处理输出的 SHA-256，可与旧版本逐字节对比。

任一回归检查失败时退出码为 1。报告中的耗时只代表当前照片、机器与负载，AI 首次推理和后续命中缓存分别记录。
