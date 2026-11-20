using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
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
    private const uint GHND = 0x0042;

    private static readonly string[] KnownVirtualPrinterNames =
        ["Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote", "Send to OneNote",
         "Fax", "Adobe PDF", "PDFCreator", "CutePDF Writer", "doPDF", "Foxit PDF Printer",
         "PDF-XChange", "Bullzip PDF Printer", "PDF24"];

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
        var systemDefaultPaper = savedPreferredPaper == null ? TryGetDefaultPaperSize(doc.PrinterSettings) : null;
        doc.PrinterSettings.Copies = Math.Max((short)1, copies);
        doc.DocumentName = $"PhotoPrintStudio ({sheets.Count}页)";

        var firstSheet = sheets[0];
        ApplyPageSettings(
            doc.PrinterSettings,
            doc.DefaultPageSettings,
            firstSheet.PaperWidthMm,
            firstSheet.PaperHeightMm,
            savedPreferredPaper,
            systemDefaultPaper,
            preferBorderless: calibration.DefaultBorderless || SheetTouchesPaperEdge(firstSheet));

        doc.QueryPageSettings += (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                e.Cancel = true;
                return;
            }
            if (pageIndex < sheets.Count)
            {
                var current = sheets[pageIndex];
                ApplyPageSettings(
                    doc.PrinterSettings,
                    e.PageSettings,
                    current.PaperWidthMm,
                    current.PaperHeightMm,
                    savedPreferredPaper,
                    systemDefaultPaper,
                    preferBorderless: calibration.DefaultBorderless || SheetTouchesPaperEdge(current));
            }
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
                PrinterCalibration = calibration,
                ApplyPrinterCalibrationTransform = true,
                ApplyPrinterColorCompensation = true,
                DuplexMirrorBackSide = duplexMirrorBackSide,
                OutputSharpeningAmount = outputSharpeningAmount
            };

            bool allowOverscanFill = SheetTouchesPaperEdge(sheet) && Math.Abs(calibration.OverscanCompensationPercent - 100.0) < 0.05;
            using var skBmp = SheetRenderer.RenderSheetBitmap(sheet, assetsById, CachedResolve, renderOpts);
            DrawSkBitmapToPrinterGraphics(skBmp, sheet.PaperWidthMm, sheet.PaperHeightMm, e, allowOverscanFill);

            pageIndex++;
            e.HasMorePages = pageIndex < sheets.Count;
        };

        doc.Print();
        cancellationToken.ThrowIfCancellationRequested();
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
        var systemDefaultPaper = savedPreferredPaper == null ? TryGetDefaultPaperSize(doc.PrinterSettings) : null;
        doc.DocumentName = "PhotoPrintStudio 物理校准测试卡";
        ApplyPageSettings(
            doc.PrinterSettings,
            doc.DefaultPageSettings,
            paperWidthMm,
            paperHeightMm,
            savedPreferredPaper,
            systemDefaultPaper,
            preferBorderless: false);

        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null) return;
            using var skBmp = CalibrationCardRenderer.RenderCalibrationCard(paperWidthMm, paperHeightMm, dpi, calibration);
            DrawSkBitmapToPrinterGraphics(skBmp, paperWidthMm, paperHeightMm, e, allowBorderlessOverscanFill: false);
            e.HasMorePages = false;
        };

        doc.Print();
    }

    private static PaperSize? TryGetDefaultPaperSize(PrinterSettings settings)
    {
        try { return settings.DefaultPageSettings.PaperSize; }
        catch { return null; }
    }

    private static void ApplyPageSettings(
        PrinterSettings printerSettings,
        PageSettings pageSettings,
        double paperWidthMm,
        double paperHeightMm,
        PaperSize? savedPreferredPaper,
        PaperSize? systemDefaultPaper,
        bool preferBorderless)
    {
        pageSettings.Margins = new Margins(0, 0, 0, 0);
        bool wantLandscape = paperWidthMm >= paperHeightMm;
        if (pageSettings.Landscape != wantLandscape)
            pageSettings.Landscape = wantLandscape;

        var matched = FindMatchingPaperSize(
            printerSettings,
            paperWidthMm,
            paperHeightMm,
            savedPreferredPaper,
            systemDefaultPaper,
            preferBorderless);
        if (matched != null && !AreSamePaperSize(pageSettings.PaperSize, matched))
            pageSettings.PaperSize = matched;
    }

    internal static bool SheetTouchesPaperEdge(PrintSheet sheet)
    {
        const double edgeTolMm = 0.2;
        foreach (var slot in sheet.Slots)
        {
            if (slot.XMm - slot.BleedMm <= edgeTolMm ||
                slot.YMm - slot.BleedMm <= edgeTolMm ||
                slot.XMm + slot.WidthMm + slot.BleedMm >= sheet.PaperWidthMm - edgeTolMm ||
                slot.YMm + slot.HeightMm + slot.BleedMm >= sheet.PaperHeightMm - edgeTolMm)
                return true;
        }
        return false;
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

    private static bool AreSamePaperSize(PaperSize? a, PaperSize? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.RawKind == b.RawKind &&
               a.Width == b.Width &&
               a.Height == b.Height &&
               string.Equals(a.PaperName, b.PaperName, StringComparison.OrdinalIgnoreCase);
    }

    private static PaperSize? FindMatchingPaperSize(
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
        float hardMarginY,
        Rectangle pageBounds,
        bool allowBorderlessOverscanFill)
    {
        // 补偿物理硬边距，使 (0, 0) 精确对应物理纸张左上角
        float offsetX = -hardMarginX;
        float offsetY = -hardMarginY;
        float targetW = (float)(paperWidthMm / 25.4 * 100.0);
        float targetH = (float)(paperHeightMm / 25.4 * 100.0);

        // 当驱动已处于无边距模式（硬边距为 0）且上报了略大于标称尺寸的过扫描画幅时，
        // 满幅照片等比铺满并居中覆盖过扫描区，避免右侧/底侧留出细白边
        if (allowBorderlessOverscanFill &&
            hardMarginX <= 0.5f && hardMarginY <= 0.5f &&
            pageBounds.Width > 0 && pageBounds.Height > 0 &&
            (pageBounds.Width > targetW + 0.5f || pageBounds.Height > targetH + 0.5f))
        {
            float ratioW = pageBounds.Width / targetW;
            float ratioH = pageBounds.Height / targetH;
            if (ratioW >= 0.99f && ratioW <= 1.10f && ratioH >= 0.99f && ratioH <= 1.10f)
            {
                float cover = Math.Max(ratioW, ratioH);
                float drawW = targetW * cover;
                float drawH = targetH * cover;
                return new RectangleF(
                    (pageBounds.Width - drawW) * 0.5f,
                    (pageBounds.Height - drawH) * 0.5f,
                    drawW,
                    drawH);
            }
        }

        return new RectangleF(offsetX, offsetY, targetW, targetH);
    }

    private static void DrawSkBitmapToPrinterGraphics(
        SKBitmap skBmp,
        double paperWidthMm,
        double paperHeightMm,
        PrintPageEventArgs e,
        bool allowBorderlessOverscanFill)
    {
        var g = e.Graphics!;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.PageUnit = GraphicsUnit.Display; // 1 unit = 1/100 inch

        var drawRect = ComputePrintDrawRect(
            paperWidthMm,
            paperHeightMm,
            e.PageSettings.HardMarginX,
            e.PageSettings.HardMarginY,
            e.PageBounds,
            allowBorderlessOverscanFill);

        using (var gdiBmp = ToGdiBitmap(skBmp))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            PointF[] destPoints =
            [
                new(drawRect.Left, drawRect.Top),
                new(drawRect.Right, drawRect.Top),
                new(drawRect.Left, drawRect.Bottom)
            ];
            g.DrawImage(gdiBmp, destPoints, new RectangleF(0, 0, gdiBmp.Width, gdiBmp.Height), GraphicsUnit.Pixel, attrs);
        }
        GC.KeepAlive(skBmp);
    }

    // SheetRenderer 输出 Premul BGRA8888，因此必须按 Format32bppPArgb 包装避免 GDI+ 二次预乘
    private static Bitmap ToGdiBitmap(SKBitmap skBmp) =>
        new(skBmp.Width, skBmp.Height, skBmp.RowBytes,
            skBmp.AlphaType == SKAlphaType.Premul ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb,
            skBmp.GetPixels());

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
}
