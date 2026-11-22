using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.Core.Preflight;

public enum PreflightSeverity
{
    Ok,
    Info,
    Warning,
    Error
}

public sealed record PreflightIssue(
    PreflightSeverity Severity,
    string Category,
    string Message,
    string? SlotId = null,
    string? QuickFixLabel = null)
{
    public bool HasQuickFix => QuickFixLabel != null;
}

public sealed record PreflightReport(
    PreflightSeverity OverallSeverity,
    IReadOnlyList<PreflightIssue> Issues);

public static class PreflightChecker
{
    public static PreflightReport InspectSheet(
        PrintSheet sheet,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        PrinterCalibrationProfile? printer = null,
        IReadOnlyDictionary<string, HistogramData>? histogramsByAssetId = null,
        bool? driverPaperIsLandscape = null,
        IReadOnlyDictionary<string, PortraitQualityDiagnostics>? portraitDiagnosticsByAssetId = null)
    {
        var issues = new List<PreflightIssue>();

        if (sheet.Slots.Count == 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error, "排版", "当前排版页没有任何有效照片格位。"));
            return new PreflightReport(PreflightSeverity.Error, issues);
        }

        int emptyCount = 0;
        double safeMargin = printer is { DefaultBorderless: true } ? 0.0 : (printer?.SafeMarginMm ?? 4.0);

        // 按素材 ID 聚合检查结果，彻底避免多格重复放同一张图时刷屏告警
        var lowResByAsset = new Dictionary<string, (PreflightSeverity sev, double minDpi, string name, List<int> slotNums, string worstSlotId)>();
        var unknownResAssets = new HashSet<string>();
        var edgeSlots = new List<(int num, string id)>();
        var checkedToneAssets = new HashSet<string>();
        var checkedPortraitAssets = new HashSet<string>();

        foreach (var slot in sheet.Slots)
        {
            if (slot.WidthMm <= 0 || slot.HeightMm <= 0 || slot.XMm < -0.01 || slot.YMm < -0.01 ||
                slot.XMm + slot.WidthMm > sheet.PaperWidthMm + 0.01 || slot.YMm + slot.HeightMm > sheet.PaperHeightMm + 0.01)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error, "排版",
                    $"格位 {slot.Index + 1} 的尺寸无效或超出纸张，请调整纸张、模板或边距。", slot.Id));
                continue;
            }

            if (!slot.HasValidPhotoArea)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error, "排版",
                    $"格位 {slot.Index + 1} 的拍立得相框没有可用的照片区域。", slot.Id));
                continue;
            }

            if (string.IsNullOrEmpty(slot.PhotoAssetId) || !assetsById.TryGetValue(slot.PhotoAssetId, out var asset))
            {
                emptyCount++;
                continue;
            }

            // 1. 有效物理分辨率检测 (按资产去重聚合)
            bool rotated = slot.IsQuarterTurnRotated;
            double targetWmm = slot.PolaroidStyle != null
                ? Math.Max(1, slot.WidthMm - (rotated
                    ? slot.PolaroidStyle.MarginTopMm + slot.PolaroidStyle.MarginBottomMm
                    : slot.PolaroidStyle.MarginLeftMm + slot.PolaroidStyle.MarginRightMm))
                : slot.WidthMm + slot.BleedMm * 2;
            double targetHmm = slot.PolaroidStyle != null
                ? Math.Max(1, slot.HeightMm - (rotated
                    ? slot.PolaroidStyle.MarginLeftMm + slot.PolaroidStyle.MarginRightMm
                    : slot.PolaroidStyle.MarginTopMm + slot.PolaroidStyle.MarginBottomMm))
                : slot.HeightMm + slot.BleedMm * 2;

            bool swapped = (asset.EditState.QuarterTurns % 2 != 0) ^ rotated;
            double srcW = swapped ? asset.PixelHeight : asset.PixelWidth;
            double srcH = swapped ? asset.PixelWidth : asset.PixelHeight;
            if (srcW > 0 && srcH > 0)
            {
                double zoom = Math.Clamp(asset.EditState.Zoom * slot.SlotZoom, 0.5, 8.0);
                double trueDpiW = (srcW / zoom) / (targetWmm / UnitConverter.MmPerInch);
                double trueDpiH = (srcH / zoom) / (targetHmm / UnitConverter.MmPerInch);
                double minDpi = (slot.EffectiveFitMode ?? asset.EditState.FitMode) == FitMode.Cover
                    ? Math.Min(trueDpiW, trueDpiH) : Math.Max(trueDpiW, trueDpiH);

                if (minDpi < 220)
                {
                    var sev = minDpi < 120 ? PreflightSeverity.Error : PreflightSeverity.Warning;
                    if (!lowResByAsset.TryGetValue(asset.Id, out var entry))
                    {
                        entry = (sev, minDpi, asset.DisplayName, [], slot.Id);
                        lowResByAsset[asset.Id] = entry;
                    }
                    entry.slotNums.Add(slot.Index + 1);
                    if (minDpi < entry.minDpi)
                    {
                        lowResByAsset[asset.Id] = (sev, minDpi, asset.DisplayName, entry.slotNums, slot.Id);
                    }
                }
            }
            else if (unknownResAssets.Add(asset.Id))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Warning,
                    "分辨率未知",
                    $"{asset.DisplayName} 缺少有效像素尺寸信息，无法验证冲印 DPI。",
                    slot.Id));
            }

            // 2. 打印机硬边距碰撞检查 (仅在物理硬件边距 > 0.2mm 且非无边距打印时提示，并聚合为一条)
            if (safeMargin > 0.2)
            {
                bool touchesEdge =
                    slot.XMm < safeMargin - 0.1 ||
                    slot.YMm < safeMargin - 0.1 ||
                    (slot.XMm + slot.WidthMm) > sheet.PaperWidthMm - safeMargin + 0.1 ||
                    (slot.YMm + slot.HeightMm) > sheet.PaperHeightMm - safeMargin + 0.1;

                if (touchesEdge)
                {
                    edgeSlots.Add((slot.Index + 1, slot.Id));
                }
            }

            // 3. 高光/暗部溢出提示 (按资产去重，且排除白底/高调证件人像背景误报)
            if (checkedToneAssets.Add(asset.Id) &&
                histogramsByAssetId != null &&
                histogramsByAssetId.TryGetValue(asset.Id, out var hist))
            {
                bool checkHighlights = sheet.Scenario != LayoutScenario.IdPhoto && !hist.LooksLikeWhiteBackdrop;
                if (checkHighlights && hist.InteriorHighlightClippedRatio > 0.16)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Info,
                        "高光溢出",
                        $"{asset.DisplayName} 主体区域有 {hist.InteriorHighlightClippedRatio:P0} 高光接近死白，建议略降曝光或拉回高光。",
                        slot.Id));
                }
                if (hist.ShadowClippedRatio > 0.18)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Info,
                        "暗部死黑",
                        $"{asset.DisplayName} 有 {hist.ShadowClippedRatio:P0} 区域陷入死黑，建议提亮阴影以防相纸糊黑。",
                        slot.Id));
                }
            }

            // 4. 证件照深度合规体检（姿态、闭眼/张嘴、高低肩、衣着与底色顺色、阴阳脸/偏暗）
            if (sheet.Scenario == LayoutScenario.IdPhoto &&
                checkedPortraitAssets.Add(asset.Id) &&
                portraitDiagnosticsByAssetId != null &&
                portraitDiagnosticsByAssetId.TryGetValue(asset.Id, out var diag))
            {
                if (diag.HasClothingBackgroundConflict)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Warning,
                        "衣底顺色",
                        $"{asset.DisplayName} 服装颜色与所选背景底色非常接近（如白底穿白衣），建议更换底色或叠加深色正装。",
                        slot.Id));
                }
                if (diag.AreEyesClosed)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Warning,
                        "闭眼提醒",
                        $"{asset.DisplayName} 检测到双眼接近闭合，证件照需正视镜头睁眼拍摄。",
                        slot.Id));
                }
                if (diag.IsMouthOpen)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Warning,
                        "表情提醒",
                        $"{asset.DisplayName} 检测到嘴部张开，多数正式证件照要求自然闭口。",
                        slot.Id));
                }
                if (diag.IsHeadTurned)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Warning,
                        "侧脸提醒",
                        $"{asset.DisplayName} 头部存在明显左右偏转，正式证件照建议使用正视免冠照。",
                        slot.Id));
                }
                if (diag.AreShouldersUneven)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Info,
                        "高低肩",
                        $"{asset.DisplayName} 检测到左右肩线倾斜约 {MathF.Abs(diag.ShoulderTiltDegrees):0.0}°，可微调水平角度或使用电子正装校正。",
                        slot.Id));
                }
                if (diag.HasUnevenLighting)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Info,
                        "光照不均",
                        $"{asset.DisplayName} 左右脸亮度差异较明显（阴阳脸），建议开启「影棚自动优化」均衡补光。",
                        slot.Id,
                        "开启影棚补光"));
                }
                else if (diag.IsFaceUnderexposed)
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Info,
                        "人脸偏暗",
                        $"{asset.DisplayName} 面部曝光偏暗，建议开启「影棚自动优化」自适应提亮肤色。",
                        slot.Id,
                        "开启影棚提亮"));
                }
            }
        }

        foreach (var kv in lowResByAsset.Values)
        {
            string slotDesc = FormatSlotNumbers(kv.slotNums);
            string msg = kv.sev == PreflightSeverity.Error
                ? $"{kv.name}（{slotDesc}）有效分辨率仅约 {kv.minDpi:0} DPI，打印将明显模糊。"
                : $"{kv.name}（{slotDesc}）有效分辨率约 {kv.minDpi:0} DPI（建议 ≥ 220 DPI）。";
            issues.Add(new PreflightIssue(kv.sev, kv.sev == PreflightSeverity.Error ? "分辨率过低" : "分辨率偏低", msg, kv.worstSlotId));
        }

        if (edgeSlots.Count > 0)
        {
            string slotDesc = FormatSlotNumbers(edgeSlots.Select(x => x.num));
            issues.Add(new PreflightIssue(
                PreflightSeverity.Warning,
                "安全边距",
                $"{slotDesc} 进入打印机硬件边距 ({safeMargin:0.#} mm) 内，直接打印四周会留白；需在「打印导出 → 设备校准 → 原生驱动首选项」开启无边距打印，已开启可勾选“已在驱动中开启无边距”。",
                edgeSlots[0].id));
        }

        if (emptyCount > 0)
        {
            issues.Add(new PreflightIssue(
                emptyCount == sheet.Slots.Count ? PreflightSeverity.Error : PreflightSeverity.Warning,
                "空白格位",
                $"当前页仍有 {emptyCount} 个空置格位未放入照片。"));
        }

        if (driverPaperIsLandscape.HasValue && driverPaperIsLandscape.Value != (sheet.PaperWidthMm >= sheet.PaperHeightMm))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Warning,
                "纸张方向",
                "当前画布方向与打印机驱动纸盒方向不一致，打印时将自动旋转匹配。"));
        }

        var overall = issues.Count == 0
            ? PreflightSeverity.Ok
            : issues.Max(i => i.Severity);

        return new PreflightReport(overall, issues);
    }

    private static string FormatSlotNumbers(IEnumerable<int> nums)
    {
        var list = nums.Distinct().OrderBy(x => x).ToList();
        if (list.Count == 0) return "格位";
        if (list.Count == 1) return $"格位 #{list[0]}";
        if (list.Count <= 4) return "格位 #" + string.Join(", #", list);
        return $"格位 #{list[0]}~#{list[^1]} (共 {list.Count} 格)";
    }
}
