using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.App.Printing;

/// <summary>
/// 打印服务抽象接口，便于解耦 Windows GDI+ / Winspool 驱动调用并支持单元测试替换。
/// </summary>
public interface IPrintService
{
    bool HasSavedDriverPreferences(string printerName);
    string GetSavedDevModeBase64(string printerName);
    void LoadSavedDevModeBase64(string printerName, string? base64);
    bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd);
    IReadOnlyList<string> GetInstalledPrinters();
    string? GetDefaultPrinterName() => null;
    PrinterDeviceSummary? InspectPrinter(string printerName);
    string? GetGeometryCalibrationKey(PrintSheet sheet, string printerName, PrinterCalibrationProfile calibration, int dpi) => null;

    void PrintSheets(
        IReadOnlyList<PrintSheet> sheets,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        string printerName,
        PrinterCalibrationProfile calibration,
        short copies = 1,
        int printDpi = 300,
        bool duplexMirrorBackSide = false,
        double outputSharpeningAmount = 0.0,
        CancellationToken cancellationToken = default);

    void PrintCalibrationCard(
        double paperWidthMm,
        double paperHeightMm,
        string printerName,
        PrinterCalibrationProfile calibration,
        int dpi = 300);
}
