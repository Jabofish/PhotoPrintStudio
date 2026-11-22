using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;

namespace PhotoPrintStudio.App.Printing;

public sealed record PrinterDeviceSummary(
    string PrinterName,
    double HardMarginLeftMm,
    double HardMarginTopMm,
    bool DefaultLandscape,
    bool IsVirtualPrinter,
    IReadOnlyList<string> PaperNames);

public sealed class WindowsPrintService : IPrintService
{
    private const int DM_OUT_BUFFER = 0x02;
    private const int DM_IN_PROMPT = 0x04;
    private const int DM_IN_BUFFER = 0x08;
    private const int DM_SCALE = 0x10;
    // DEVMODEW 公共字段的字节偏移，与后面的驱动私有数据无关。
    private const int DevModeSizeOffset = 68;
    private const int DevModeDriverExtraOffset = 70;
    private const int DevModeFieldsOffset = 72;
    private const int DevModeScaleOffset = 84;
    private const uint GHND = 0x0042;
    private const int PhysicalWidth = 110;
    private const int PrintableWidth = 8;
    private const int PrintableHeight = 10;
    private const int PhysicalHeight = 111;
    private const int PhysicalOffsetX = 112;
    private const int PhysicalOffsetY = 113;

    private static readonly string[] KnownVirtualPrinterNames =
        ["Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote", "Send to OneNote",
         "Fax", "Adobe PDF", "PDFCreator", "CutePDF Writer", "doPDF", "Foxit PDF Printer",
         "PDF-XChange", "Bullzip PDF Printer", "PDF24", "导出为WPS PDF", "AnyDesk Printer"];

    internal static bool IsKnownVirtualPrinter(string printerName)
    {
        string queueName = printerName[(printerName.LastIndexOf('\\') + 1)..];
        return KnownVirtualPrinterNames.Any(name =>
            queueName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (queueName.Length > name.Length &&
             queueName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
             queueName[name.Length] is ' ' or '('));
    }

    private static readonly string[] BorderlessPaperKeywords =
        ["borderless", "edge-to-edge", "edge to edge", "full bleed", "full-bleed", "no margin", "marginless", "无边距", "无边框", "满幅", "满版", "全出血"];

    private readonly Dictionary<string, byte[]> _savedDevModeByPrinter = new(StringComparer.OrdinalIgnoreCase);

    public bool HasSavedDriverPreferences(string printerName)
    {
        lock (_savedDevModeByPrinter)
            return !string.IsNullOrWhiteSpace(printerName) && _savedDevModeByPrinter.ContainsKey(printerName);
    }

    public string GetSavedDevModeBase64(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return "";
        lock (_savedDevModeByPrinter)
        {
            return _savedDevModeByPrinter.TryGetValue(printerName, out byte[]? bytes) && bytes.Length > 0
                ? Convert.ToBase64String(bytes)
                : "";
        }
    }

    public void LoadSavedDevModeBase64(string printerName, string? base64)
    {
        if (string.IsNullOrWhiteSpace(printerName) || string.IsNullOrWhiteSpace(base64)) return;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            if (IsValidDevmode(bytes, (nuint)bytes.Length))
            {
                lock (_savedDevModeByPrinter)
                {
                    _savedDevModeByPrinter[printerName] = bytes;
                }
            }
        }
        catch (FormatException)
        {
            // Ignore malformed persisted DEVMODE
        }
    }

    public bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero)) return false;

        IntPtr pOut = IntPtr.Zero;
        IntPtr pIn = IntPtr.Zero;
        IntPtr hSettingsDevMode = IntPtr.Zero;
        bool settingsLocked = false;
        try
        {
            int needed = DocumentProperties(ownerHwnd, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (needed <= 0) return false;

            pOut = Marshal.AllocHGlobal(needed);
            byte[]? saved;
            lock (_savedDevModeByPrinter)
            {
                _savedDevModeByPrinter.TryGetValue(printerName, out saved);
            }
            if (saved != null && saved.Length == needed)
            {
                pIn = Marshal.AllocHGlobal(needed);
                Marshal.Copy(saved, 0, pIn, needed);
            }
            else
            {
                try
                {
                    var ps = new PrinterSettings { PrinterName = printerName };
                    hSettingsDevMode = ps.GetHdevmode();
                    if (hSettingsDevMode != IntPtr.Zero)
                    {
                        pIn = GlobalLock(hSettingsDevMode);
                        settingsLocked = pIn != IntPtr.Zero;
                    }
                }
                catch
                {
                    pIn = IntPtr.Zero;
                }
            }

            int mode = pIn != IntPtr.Zero
                ? (DM_IN_BUFFER | DM_OUT_BUFFER | DM_IN_PROMPT)
                : (DM_OUT_BUFFER | DM_IN_PROMPT);

            int rc = DocumentProperties(ownerHwnd, hPrinter, printerName, pOut, pIn, mode);
            if (rc == 1)
            {
                byte[] updated = new byte[needed];
                Marshal.Copy(pOut, updated, 0, needed);
                lock (_savedDevModeByPrinter)
                {
                    _savedDevModeByPrinter[printerName] = updated;
                }
                return true;
            }
            return false;
        }
        finally
        {
            if (settingsLocked && hSettingsDevMode != IntPtr.Zero) GlobalUnlock(hSettingsDevMode);
            if (hSettingsDevMode != IntPtr.Zero) GlobalFree(hSettingsDevMode);
            if (pIn != IntPtr.Zero && hSettingsDevMode == IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (pOut != IntPtr.Zero) Marshal.FreeHGlobal(pOut);
            ClosePrinter(hPrinter);
        }
    }

    private PaperSize? ApplySavedDevMode(PrintDocument doc, string printerName)
    {
        byte[]? bytes;
        lock (_savedDevModeByPrinter)
        {
            if (!_savedDevModeByPrinter.TryGetValue(printerName, out bytes) || bytes.Length == 0)
                return null;
        }

        if (!OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero)) return null;
        IntPtr hMem = IntPtr.Zero;
        try
        {
            int needed = DocumentProperties(IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (needed != bytes.Length) return null;
            hMem = GlobalAlloc(GHND, (UIntPtr)needed);
            if (hMem == IntPtr.Zero) return null;
            IntPtr ptr = GlobalLock(hMem);
            if (ptr != IntPtr.Zero)
            {
                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    if (DocumentProperties(IntPtr.Zero, printer, printerName, ptr, ptr, DM_IN_BUFFER | DM_OUT_BUFFER) != 1)
                        return null;
                }
                finally
                {
                    GlobalUnlock(hMem);
                }
                doc.PrinterSettings.SetHdevmode(hMem);
                return doc.PrinterSettings.DefaultPageSettings.PaperSize;
            }
        }
        catch
        {
            // Ignore incompatible DEVMODE across driver switches
        }
        finally
        {
            if (hMem != IntPtr.Zero) GlobalFree(hMem);
            ClosePrinter(printer);
        }
        return null;
    }

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        var list = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            list.Add(name);
        return list;
    }

    public string? GetDefaultPrinterName()
    {
        var settings = new PrinterSettings();
        return settings.IsValid ? settings.PrinterName : null;
    }

    public PrinterDeviceSummary? InspectPrinter(string printerName)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printerName };
            if (!settings.IsValid) return null;

            var page = settings.DefaultPageSettings;
            var papers = new List<string>();
            foreach (PaperSize ps in settings.PaperSizes)
                papers.Add($"{ps.PaperName} ({ps.Width / 100.0 * 25.4:0.#}×{ps.Height / 100.0 * 25.4:0.#}mm)");

            double hardX = Math.Round(page.HardMarginX / 100.0 * 25.4, 2);
            double hardY = Math.Round(page.HardMarginY / 100.0 * 25.4, 2);
            return new PrinterDeviceSummary(
                printerName,
                hardX,
                hardY,
                page.Landscape,
                IsKnownVirtualPrinter(printerName),
                papers);
        }
        catch
        {
            return null;
        }
    }

    public void PrintSheets(
        IReadOnlyList<PrintSheet> sheets,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        string printerName,
        PrinterCalibrationProfile calibration,
        short copies = 1,
        int printDpi = 300,
        bool duplexMirrorBackSide = false,
        double outputSharpeningAmount = 0.0,
        CancellationToken cancellationToken = default)
    {
        if (sheets.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();

        int pageIndex = 0;
        using var cache = new ExportService.BoundedExportBitmapCache(sheets, maxBytes: 384L * 1024 * 1024, maxEntries: 6);
        SKBitmap CachedResolve(PhotoAsset asset, PhotoEditState edit) => cache.Resolve(asset, edit, resolveFullResBitmap);

        using var doc = new PrintDocument();
        // 使用无弹窗 StandardPrintController，防止在后台 MTA 线程中创建 WinForms 状态对话框导致卡死或跨线程异常
        doc.PrintController = new StandardPrintController();
        doc.PrinterSettings.PrinterName = printerName;
        var savedPreferredPaper = ApplySavedDevMode(doc, printerName);
        SetActualSizeForPrintJob(doc.PrinterSettings);
        var systemDefaultPaper = savedPreferredPaper == null ? TryGetDefaultPaperSize(doc.PrinterSettings) : null;
        doc.PrinterSettings.Copies = 1;
        doc.DocumentName = $"PhotoPrintStudio ({sheets.Count}页)";

        var settingsByPaper = new Dictionary<(double Width, double Height, bool Borderless), PreparedOutput>();
        var outputs = new PreparedOutput[sheets.Count];
        for (int i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = sheets[i];
            var key = (Width: sheet.PaperWidthMm, Height: sheet.PaperHeightMm,
                Borderless: ShouldPreferBorderlessPaper(sheet, calibration));
            if (!settingsByPaper.TryGetValue(key, out var settings))
            {
                settings = PrepareOutput(doc.PrinterSettings, key.Width, key.Height,
                    savedPreferredPaper, systemDefaultPaper, key.Borderless, printDpi);
                settingsByPaper.Add(key, settings);
            }
            RequireMeasuredBorderless(sheet, printerName, settings, calibration);
            ValidatePrintableSlots(sheet, settings.PaperBounds, settings.PrintableBounds,
                calibration.ForOutput(settings.CalibrationKey), duplexMirrorBackSide, printDpi);
            outputs[i] = settings;
        }
        doc.PrinterSettings.Copies = Math.Max((short)1, copies);
        doc.DefaultPageSettings = outputs[0].Settings;

        doc.QueryPageSettings += (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                e.Cancel = true;
                return;
            }
            if (pageIndex < outputs.Length && !ReferenceEquals(e.PageSettings, outputs[pageIndex].Settings))
                e.PageSettings = outputs[pageIndex].Settings;
        };

        doc.PrintPage += (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                e.Cancel = true;
                e.HasMorePages = false;
                return;
            }
            if (pageIndex >= sheets.Count || e.Graphics == null)
            {
                e.HasMorePages = false;
                return;
            }

            var sheet = sheets[pageIndex];
            var renderOpts = new SheetRenderOptions
            {
                Dpi = printDpi,
                IsScreenPreview = false,
                ShowSafeMarginOverlay = false,
                ShowBleedOverlay = false,
                ShowIdGuidelinesOnSelected = false,
                PrinterCalibration = calibration.ForOutput(outputs[pageIndex].CalibrationKey),
                ApplyPrinterCalibrationTransform = true,
                ApplyPrinterColorCompensation = true,
                DuplexMirrorBackSide = duplexMirrorBackSide,
                OutputSharpeningAmount = outputSharpeningAmount
            };

            using var skBmp = SheetRenderer.RenderSheetBitmap(sheet, assetsById, CachedResolve, renderOpts);
            var (paperBounds, printableBounds) = ReadPaperGeometry(e.Graphics, e.PageSettings, e.PageBounds);
            VerifyPhysicalPaper(e.PageBounds, paperBounds);
            ValidatePrintableSlots(sheet, paperBounds, printableBounds, renderOpts.PrinterCalibration, duplexMirrorBackSide, printDpi);
            DrawBitmapWithinPaper(skBmp, sheet.PaperWidthMm, sheet.PaperHeightMm, e.Graphics, paperBounds);

            pageIndex++;
            e.HasMorePages = pageIndex < sheets.Count;
        };

        doc.Print();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string GetGeometryCalibrationKey(PrintSheet sheet, string printerName, PrinterCalibrationProfile calibration, int dpi)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        var savedPaper = ApplySavedDevMode(doc, printerName);
        SetActualSizeForPrintJob(doc.PrinterSettings);
        doc.PrinterSettings.Copies = 1;
        return PrepareOutput(doc.PrinterSettings, sheet.PaperWidthMm, sheet.PaperHeightMm, savedPaper,
            savedPaper == null ? TryGetDefaultPaperSize(doc.PrinterSettings) : null,
            ShouldPreferBorderlessPaper(sheet, calibration), dpi).CalibrationKey;
    }

    public void PrintCalibrationCard(
        double paperWidthMm,
        double paperHeightMm,
        string printerName,
        PrinterCalibrationProfile calibration,
        int dpi = 300)
    {
        using var doc = new PrintDocument();
        doc.PrintController = new StandardPrintController();
        doc.PrinterSettings.PrinterName = printerName;
        var savedPreferredPaper = ApplySavedDevMode(doc, printerName);
        SetActualSizeForPrintJob(doc.PrinterSettings);
        doc.PrinterSettings.Copies = 1;
        var systemDefaultPaper = savedPreferredPaper == null ? TryGetDefaultPaperSize(doc.PrinterSettings) : null;
        doc.DocumentName = "PhotoPrintStudio 物理校准测试卡";
        var output = PrepareOutput(
            doc.PrinterSettings,
            paperWidthMm,
            paperHeightMm,
            savedPreferredPaper,
            systemDefaultPaper,
            preferBorderless: calibration.DefaultBorderless,
            dpi);
        doc.DefaultPageSettings = output.Settings;
        if (calibration.GeometryTarget is { } target && target != output.CalibrationKey)
            throw new InvalidOperationException("纸型或驱动设置已改变，请重新打开校准向导。");
        var cardCalibration = calibration.GeometryTarget != null ? calibration : calibration.ForOutput(output.CalibrationKey);

        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null) return;
            using var skBmp = CalibrationCardRenderer.RenderCalibrationCard(paperWidthMm, paperHeightMm, dpi, cardCalibration);
            DrawSkBitmapToPrinterGraphics(skBmp, paperWidthMm, paperHeightMm, e);
            e.HasMorePages = false;
        };

        doc.Print();
    }

    private static void SetActualSizeForPrintJob(PrinterSettings settings)
    {
        IntPtr handle = settings.GetHdevmode();
        try
        {
            IntPtr mode = GlobalLock(handle);
            if (mode == IntPtr.Zero) return;
            bool changed;
            try { changed = SetActualSizeOnDevMode(mode); }
            finally { GlobalUnlock(handle); }
            if (changed) settings.SetHdevmode(handle);
        }
        finally { GlobalFree(handle); }
    }

    internal static bool SetActualSizeOnDevMode(IntPtr mode)
    {
        if (Marshal.ReadInt16(mode, DevModeSizeOffset) < DevModeScaleOffset + sizeof(short) ||
            (Marshal.ReadInt32(mode, DevModeFieldsOffset) & DM_SCALE) == 0 ||
            Marshal.ReadInt16(mode, DevModeScaleOffset) == 100)
            return false;

        Marshal.WriteInt16(mode, DevModeScaleOffset, 100);
        return true;
    }

    private static PaperSize? TryGetDefaultPaperSize(PrinterSettings settings)
    {
        try { return settings.DefaultPageSettings.PaperSize; }
        catch { return null; }
    }

    private sealed record PreparedOutput(PageSettings Settings, RectangleF PaperBounds, RectangleF PrintableBounds, string CalibrationKey);

    private static void RequireMeasuredBorderless(PrintSheet sheet, string printerName, PreparedOutput output,
        PrinterCalibrationProfile calibration)
    {
        if (IsKnownVirtualPrinter(printerName) ||
            calibration.GeometryByOutput.GetValueOrDefault(output.CalibrationKey) is { HasMeasuredScale: true })
            return;

        if (IsBorderlessPaperName(output.Settings.PaperSize.PaperName) ||
            output.PrintableBounds == output.PaperBounds)
            throw new InvalidOperationException($"{sheet.SheetName} 的当前无边距设置还没有实测尺寸。请在打印校准中用同样的纸型、方向和驱动设置打印测试卡，输入实测长度后保存。");
    }

    private static PreparedOutput PrepareOutput(
        PrinterSettings printerSettings,
        double paperWidthMm,
        double paperHeightMm,
        PaperSize? savedPreferredPaper,
        PaperSize? systemDefaultPaper,
        bool preferBorderless,
        int dpi)
    {
        var matched = FindMatchingPaperSize(
            printerSettings,
            paperWidthMm,
            paperHeightMm,
            savedPreferredPaper,
            systemDefaultPaper,
            preferBorderless);
        // Landscape 交换驱动纸型的宽高，有些纸型原始宽度已大于高度。
        var pageSettings = new PageSettings(printerSettings)
        {
            Margins = new Margins(0, 0, 0, 0),
            PaperSize = matched,
            Landscape = (paperWidthMm >= paperHeightMm) != (matched.Width > matched.Height)
        };
        using var measurement = printerSettings.CreateMeasurementGraphics(pageSettings);
        var (paperBounds, printableBounds) = ReadPaperGeometry(measurement, pageSettings, pageSettings.Bounds);
        VerifyPhysicalPaper(pageSettings.Bounds, paperBounds);
        return new(pageSettings, paperBounds, printableBounds,
            ReadCalibrationKey(printerSettings, pageSettings, paperWidthMm, paperHeightMm, dpi, preferBorderless));
    }

    private static string ReadCalibrationKey(PrinterSettings printer, PageSettings page, double width, double height, int dpi, bool preferBorderless)
    {
        // 包含原生驱动私有设置。驱动可在纸型名称相同的情况下启用不同的无边距扩张。
        var handle = printer.GetHdevmode(page);
        try
        {
            var mode = GlobalLock(handle);
            if (mode == IntPtr.Zero)
                throw new InvalidOperationException("无法读取打印驱动设置。");
            try
            {
                int length = (ushort)Marshal.ReadInt16(mode, DevModeSizeOffset) +
                    (ushort)Marshal.ReadInt16(mode, DevModeDriverExtraOffset);
                if (length < DevModeFieldsOffset + sizeof(int) || (nuint)length > GlobalSize(handle))
                    throw new InvalidOperationException("无法识别打印驱动设置。");
                var bytes = new byte[length];
                Marshal.Copy(mode, bytes, 0, length);
                return FormattableString.Invariant($"v1:{width:R}x{height:R}:{dpi}:{preferBorderless}:{Convert.ToHexString(SHA256.HashData(bytes))}");
            }
            finally { GlobalUnlock(handle); }
        }
        finally { GlobalFree(handle); }
    }

    internal static bool ShouldPreferBorderlessPaper(PrintSheet sheet, PrinterCalibrationProfile calibration)
    {
        if (calibration.DefaultBorderless) return true;
        if (sheet.Scenario is not (LayoutScenario.SinglePhoto or LayoutScenario.SixInchTwoInOne))
            return false;

        const double edgeToleranceMm = 0.2;
        return sheet.Slots.Any(slot => !string.IsNullOrEmpty(slot.PhotoAssetId) &&
            (slot.XMm - slot.BleedMm <= edgeToleranceMm ||
             slot.YMm - slot.BleedMm <= edgeToleranceMm ||
             slot.XMm + slot.WidthMm + slot.BleedMm >= sheet.PaperWidthMm - edgeToleranceMm ||
             slot.YMm + slot.HeightMm + slot.BleedMm >= sheet.PaperHeightMm - edgeToleranceMm));
    }

    internal static bool IsBorderlessPaperName(string? paperName)
    {
        if (string.IsNullOrWhiteSpace(paperName)) return false;
        foreach (string keyword in BorderlessPaperKeywords)
        {
            if (paperName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static PaperSize FindMatchingPaperSize(
        PrinterSettings settings,
        double widthMm,
        double heightMm,
        PaperSize? savedPreferredPaper,
        PaperSize? systemDefaultPaper,
        bool preferBorderless)
    {
        var available = new List<PaperSize>();
        foreach (PaperSize ps in settings.PaperSizes)
            available.Add(ps);
        return SelectPaperSize(available, widthMm, heightMm, savedPreferredPaper, systemDefaultPaper, preferBorderless);
    }

    internal static PaperSize SelectPaperSize(
        IEnumerable<PaperSize> availablePapers,
        double widthMm,
        double heightMm,
        PaperSize? savedPreferredPaper,
        PaperSize? systemDefaultPaper,
        bool preferBorderless)
    {
        double shortMm = Math.Min(widthMm, heightMm);
        double longMm = Math.Max(widthMm, heightMm);

        static (double paperShort, double paperLong, double shortDiff, double longDiff, double sumDiff) Measure(PaperSize paper, double sMm, double lMm)
        {
            double paperW = paper.Width / 100.0 * 25.4;
            double paperH = paper.Height / 100.0 * 25.4;
            double pShort = Math.Min(paperW, paperH);
            double pLong = Math.Max(paperW, paperH);
            double sDiff = Math.Abs(pShort - sMm);
            double lDiff = Math.Abs(pLong - lMm);
            return (pShort, pLong, sDiff, lDiff, sDiff + lDiff);
        }

        static bool IsBorderlessOverscan(double paperShort, double paperLong, double targetShort, double targetLong) =>
            paperShort >= targetShort - 0.5 &&
            paperLong >= targetLong - 0.5 &&
            paperShort <= targetShort * 1.07 + 0.5 &&
            paperLong <= targetLong * 1.07 + 0.5;

        // 驱动公制相纸（如 102×152 / 100×150）与 4×6 (101.6×152.4) 存在 0.8~4mm 标称差异，
        // 容差需覆盖公制/英制换算及无边距过扫描扩展，同时避免把 6 寸误匹配到 A6 (105×148) 或明信片 (100×148)
        bool IsAutoMatch(PaperSize paper, out double sumDiff, out bool isBorderless)
        {
            var m = Measure(paper, shortMm, longMm);
            sumDiff = m.sumDiff;
            isBorderless = IsBorderlessPaperName(paper.PaperName);
            if (m.shortDiff <= 3.0 && m.longDiff <= 3.0 && m.sumDiff <= 5.5)
                return true;
            // 无边距驱动纸型可能上报按比例扩大的过扫描物理幅面（通常每边 +2%~6%）
            return isBorderless && IsBorderlessOverscan(m.paperShort, m.paperLong, shortMm, longMm);
        }

        // 1. 优先尊重用户在软件内原生驱动首选项中保存的纸型（含私有无边距 DEVMODE 与明信片/公制/过扫描纸型）
        if (savedPreferredPaper != null)
        {
            var m = Measure(savedPreferredPaper, shortMm, longMm);
            if ((m.shortDiff <= 4.5 && m.longDiff <= 4.5 && m.sumDiff <= 6.5) ||
                IsBorderlessOverscan(m.paperShort, m.paperLong, shortMm, longMm))
                return savedPreferredPaper;
        }

        PaperSize? bestBorderless = null;
        double bestBorderlessDiff = double.MaxValue;
        PaperSize? bestStandard = null;
        double bestStandardDiff = double.MaxValue;

        foreach (var ps in availablePapers)
        {
            if (!IsAutoMatch(ps, out double diff, out bool isBorderless)) continue;
            if (isBorderless)
            {
                if (diff < bestBorderlessDiff - 1e-6)
                {
                    bestBorderlessDiff = diff;
                    bestBorderless = ps;
                }
            }
            else if (diff < bestStandardDiff - 1e-6)
            {
                bestStandardDiff = diff;
                bestStandard = ps;
            }
        }

        // 2. 未在软件内保存 DEVMODE 时，优先保留 Windows 系统默认首选项中已选中的匹配纸型
        //    （避免把用户在系统里配好的无边距纸型覆盖回有边距纸型）
        if (systemDefaultPaper != null && IsAutoMatch(systemDefaultPaper, out _, out bool defaultIsBorderless))
        {
            if (!preferBorderless || defaultIsBorderless || bestBorderless == null)
                return systemDefaultPaper;
        }

        // 3. 在驱动预设纸型中按是否满幅/无边距偏好择优
        if (preferBorderless)
        {
            if (bestBorderless != null) return bestBorderless;
            if (bestStandard != null) return bestStandard;
        }
        else
        {
            if (bestStandard != null) return bestStandard;
            if (bestBorderless != null) return bestBorderless;
        }

        // 4. 若驱动没有任何接近预设（如部分虚拟打印机），再回退自定义 PaperSize (单位 1/100 inch)
        int wHund = (int)Math.Round(shortMm / 25.4 * 100.0);
        int hHund = (int)Math.Round(longMm / 25.4 * 100.0);
        return new PaperSize("CustomPhoto", wHund, hHund);
    }

    internal static RectangleF ComputePrintDrawRect(
        double paperWidthMm,
        double paperHeightMm,
        float hardMarginX,
        float hardMarginY) => new(
            -hardMarginX / 100f,
            -hardMarginY / 100f,
            (float)(paperWidthMm / UnitConverter.MmPerInch),
            (float)(paperHeightMm / UnitConverter.MmPerInch));

    private static void DrawSkBitmapToPrinterGraphics(
        SKBitmap skBmp,
        double paperWidthMm,
        double paperHeightMm,
        PrintPageEventArgs e)
    {
        var (paperBounds, _) = ReadPaperGeometry(e.Graphics!, e.PageSettings, e.PageBounds);
        VerifyPhysicalPaper(e.PageBounds, paperBounds);
        DrawBitmapWithinPaper(skBmp, paperWidthMm, paperHeightMm, e.Graphics!, paperBounds);
    }

    private static (RectangleF Paper, RectangleF Printable) ReadPaperGeometry(Graphics g, PageSettings pageSettings, Rectangle pageBounds)
    {
        float dpiX = g.DpiX;
        float dpiY = g.DpiY;
        IntPtr hdc = g.GetHdc();
        try
        {
            int width = GetDeviceCaps(hdc, PhysicalWidth);
            int height = GetDeviceCaps(hdc, PhysicalHeight);
            if (width > 0 && height > 0)
            {
                var paper = new RectangleF(
                    -GetDeviceCaps(hdc, PhysicalOffsetX) / dpiX,
                    -GetDeviceCaps(hdc, PhysicalOffsetY) / dpiY,
                    width / dpiX,
                    height / dpiY);
                int printableW = GetDeviceCaps(hdc, PrintableWidth);
                int printableH = GetDeviceCaps(hdc, PrintableHeight);
                var printable = printableW > 0 && printableH > 0
                    ? RectangleF.Intersect(paper, new RectangleF(0, 0, printableW / dpiX, printableH / dpiY))
                    : paper;
                return (paper, printable);
            }
        }
        finally { g.ReleaseHdc(hdc); }

        var fallback = new RectangleF(
            -pageSettings.HardMarginX / 100f, -pageSettings.HardMarginY / 100f,
            pageBounds.Width / 100f, pageBounds.Height / 100f);
        return (fallback, fallback);
    }

    internal static void ValidatePrintableSlots(PrintSheet sheet, RectangleF paperBounds, RectangleF printableBounds,
        PrinterCalibrationProfile calibration, bool backSide, int dpi)
    {
        if (!FreeformLayoutMath.CanOutput(sheet))
            throw new InvalidOperationException($"{sheet.SheetName} 的照片框尺寸无效或超出了版面。");

        var (sx, sy, dx, dy) = calibration.GetGeometryTransform(backSide);
        double centerX = paperBounds.Left + paperBounds.Width / 2.0;
        double centerY = paperBounds.Top + paperBounds.Height / 2.0;
        double tolerance = 1.0 / dpi;
        foreach (var slot in sheet.Slots.Where(slot => !string.IsNullOrEmpty(slot.PhotoAssetId)))
        {
            double left = centerX + ((slot.XMm - sheet.PaperWidthMm / 2) * sx + dx) / UnitConverter.MmPerInch;
            double top = centerY + ((slot.YMm - sheet.PaperHeightMm / 2) * sy + dy) / UnitConverter.MmPerInch;
            double right = left + slot.WidthMm * sx / UnitConverter.MmPerInch;
            double bottom = top + slot.HeightMm * sy / UnitConverter.MmPerInch;
            if (left < printableBounds.Left - tolerance || top < printableBounds.Top - tolerance ||
                right > printableBounds.Right + tolerance || bottom > printableBounds.Bottom + tolerance)
                throw new InvalidOperationException($"{sheet.SheetName} 第 {slot.Index + 1} 格放不进当前可打印区域。请调整版式留白，或选择支持该纸型无边距的驱动设置。");
        }
    }

    internal static void VerifyPhysicalPaper(Rectangle expectedBounds, RectangleF actualBounds)
    {
        double width = expectedBounds.Width * UnitConverter.MmPerInch / 100.0;
        double height = expectedBounds.Height * UnitConverter.MmPerInch / 100.0;
        double actualWidth = actualBounds.Width * UnitConverter.MmPerInch;
        double actualHeight = actualBounds.Height * UnitConverter.MmPerInch;

        // 允许公制/英制纸型的标称差异和无边距模式略扩大的驱动画幅。
        static bool IsCompatible(double expected, double actual) =>
            actual >= expected - 3 && actual <= Math.Max(expected + 3, expected * 1.07 + 0.5);

        if (!IsCompatible(width, actualWidth) || !IsCompatible(height, actualHeight))
            throw new InvalidOperationException(
                $"驱动实际使用 {actualWidth:0.##} × {actualHeight:0.##} mm 纸张，与所选纸型 {width:0.##} × {height:0.##} mm 不符。请在打印机驱动首选项选择与版面一致的纸型。");
    }

    internal static void DrawBitmapWithinPaper(
        SKBitmap skBmp, double paperWidthMm, double paperHeightMm, Graphics g, RectangleF paperBounds)
    {
        var layout = ComputePrintDrawRect(paperWidthMm, paperHeightMm, 0, 0);
        layout.Offset(
            paperBounds.Left + (paperBounds.Width - layout.Width) * 0.5f,
            paperBounds.Top + (paperBounds.Height - layout.Height) * 0.5f);
        var destination = RectangleF.Intersect(layout, paperBounds);
        if (destination.Width <= 0 || destination.Height <= 0) return;

        float pixelsPerInchX = skBmp.Width / layout.Width;
        float pixelsPerInchY = skBmp.Height / layout.Height;
        var source = new RectangleF(
            (destination.Left - layout.Left) * pixelsPerInchX,
            (destination.Top - layout.Top) * pixelsPerInchY,
            destination.Width * pixelsPerInchX,
            destination.Height * pixelsPerInchY);

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.PageUnit = GraphicsUnit.Inch;
        g.SetClip(paperBounds, CombineMode.Intersect);

        using (var gdiBmp = ToGdiBitmap(skBmp))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            PointF[] destPoints =
            [
                new(destination.Left, destination.Top),
                new(destination.Right, destination.Top),
                new(destination.Left, destination.Bottom)
            ];
            g.DrawImage(gdiBmp, destPoints, source, GraphicsUnit.Pixel, attrs);
        }
        GC.KeepAlive(skBmp);
    }

    // SheetRenderer 输出 Premul BGRA8888，因此必须按 Format32bppPArgb 包装避免 GDI+ 二次预乘
    private static Bitmap ToGdiBitmap(SKBitmap skBmp) =>
        new(skBmp.Width, skBmp.Height, skBmp.RowBytes,
            skBmp.AlphaType == SKAlphaType.Premul ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb,
            skBmp.GetPixels());

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("winspool.drv", EntryPoint = "IsValidDevmodeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidDevmode(byte[] devMode, nuint size);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hwnd,
        IntPtr hPrinter,
        string pDeviceName,
        IntPtr pDevModeOutput,
        IntPtr pDevModeInput,
        int fMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}
