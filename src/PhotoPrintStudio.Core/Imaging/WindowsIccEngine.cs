using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.Core.Imaging;

public readonly record struct SoftProofOptions(
    SoftProofRenderingIntent Intent = SoftProofRenderingIntent.RelativeColorimetric,
    bool SimulatePaperWhite = false,
    bool ShowGamutWarning = false)
{
    public SoftProofOptions() : this(
        SoftProofRenderingIntent.RelativeColorimetric,
        SimulatePaperWhite: false,
        ShowGamutWarning: false)
    {
    }

    public static SoftProofOptions Default => new(
        SoftProofRenderingIntent.RelativeColorimetric,
        SimulatePaperWhite: false,
        ShowGamutWarning: false);
}

public sealed record IccProfileInfo(
    bool IsValid,
    string ColorSpace,
    string DeviceClass,
    string EngineName);

/// <summary>
/// 工业级色彩管理引擎：在 Windows 上通过系统原生 WCS/ICM (mscms.dll) 完整支持 RGB 矩阵、RGB 3D CLUT 与 CMYK 3D CLUT ICC (v2/v4) 软打样、
/// 渲染意图、纸白模拟与超色域检测；并提供标准 sRGB ICC 配置字节流供导出文件嵌入。
/// </summary>
public static unsafe class WindowsIccEngine
{
    private const uint PROFILE_MEMBUFFER = 2;
    private const uint PROFILE_READ = 1;
    private const uint FILE_SHARE_READ = 1;
    private const uint OPEN_EXISTING = 3;

    private const uint BEST_MODE = 0x00000003;
    private const uint ENABLE_GAMUT_CHECKING = 0x00010000;
    private const uint WCS_ALWAYS = 0x00200000;
    private const uint BM_xRGBQUADS = 0x0008;

    private static readonly Lazy<byte[]> StandardSrgbIcc = new(LoadOrBuildStandardSrgbIcc);

    public static byte[] GetStandardSrgbIccBytes() => StandardSrgbIcc.Value;

    public static IccProfileInfo InspectProfileHeader(ReadOnlySpan<byte> iccBytes)
    {
        if (iccBytes.Length < 128)
            return new IccProfileInfo(false, "Unknown", "Unknown", "None");

        // ICC 规范：字节 36..39 必须为魔数 "acsp"
        if (iccBytes[36] != (byte)'a' || iccBytes[37] != (byte)'c' ||
            iccBytes[38] != (byte)'s' || iccBytes[39] != (byte)'p')
            return new IccProfileInfo(false, "Unknown", "Unknown", "None");

        string deviceClass = System.Text.Encoding.ASCII.GetString(iccBytes.Slice(12, 4)).Trim();
        string colorSpace = System.Text.Encoding.ASCII.GetString(iccBytes.Slice(16, 4)).Trim();
        return new IccProfileInfo(true, colorSpace, deviceClass, OperatingSystem.IsWindows() ? "Windows WCS/ICM" : "Skia");
    }

    public static bool TryValidateSoftProofProfile(byte[] iccBytes)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var info = InspectProfileHeader(iccBytes);
        if (!info.IsValid) return false;

        IntPtr hSrgb = IntPtr.Zero;
        IntPtr hTarget = IntPtr.Zero;
        IntPtr hTransform = IntPtr.Zero;
        try
        {
            hSrgb = OpenMemoryProfile(GetStandardSrgbIccBytes());
            hTarget = OpenMemoryProfile(iccBytes);
            if (hSrgb == IntPtr.Zero || hTarget == IntPtr.Zero) return false;

            hTransform = CreateProofTransform(hSrgb, hTarget, SoftProofOptions.Default);
            return hTransform != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hTransform != IntPtr.Zero) DeleteColorTransform(hTransform);
            if (hTarget != IntPtr.Zero) CloseColorProfile(hTarget);
            if (hSrgb != IntPtr.Zero) CloseColorProfile(hSrgb);
        }
    }

    /// <summary>
    /// 使用 Windows WCS/ICM 对 33×33×33 sRGB 网格一次性烘焙软打样 3D LUT 及可选超色域掩码，
    /// 完整支持 RGB/CMYK CLUT 配置文件、4 种渲染意图与纸白模拟。
    /// </summary>
    public static bool TryBakeSoftProofLut33(
        byte[] targetIccBytes,
        int lutDim,
        SoftProofOptions options,
        out float[] lut33,
        out byte[]? gamutMask33)
    {
        lut33 = [];
        gamutMask33 = null;
        if (!OperatingSystem.IsWindows() || !InspectProfileHeader(targetIccBytes).IsValid)
            return false;

        IntPtr hSrgb = IntPtr.Zero;
        IntPtr hTarget = IntPtr.Zero;
        IntPtr hProofTransform = IntPtr.Zero;
        IntPtr hGamutTransform = IntPtr.Zero;

        try
        {
            hSrgb = OpenMemoryProfile(GetStandardSrgbIccBytes());
            hTarget = OpenMemoryProfile(targetIccBytes);
            if (hSrgb == IntPtr.Zero || hTarget == IntPtr.Zero)
                return false;

            hProofTransform = CreateProofTransform(hSrgb, hTarget, options);
            if (hProofTransform == IntPtr.Zero)
                return false;

            int n = lutDim;
            int width = n * n;
            int height = n;
            int pixelCount = width * height;
            int stride = width * 4;
            byte[] srcGrid = new byte[pixelCount * 4];
            byte[] dstGrid = new byte[pixelCount * 4];

            // 构建 BGRX 网格（行=b, 列=g*n+r），与 ExifAndColorHelper 索引约定一致
            for (int b = 0; b < n; b++)
            {
                byte bv = (byte)Math.Round(b * 255.0 / (n - 1));
                int rowOff = b * stride;
                for (int g = 0; g < n; g++)
                {
                    byte gv = (byte)Math.Round(g * 255.0 / (n - 1));
                    for (int r = 0; r < n; r++)
                    {
                        byte rv = (byte)Math.Round(r * 255.0 / (n - 1));
                        int px = rowOff + (g * n + r) * 4;
                        srcGrid[px] = bv;
                        srcGrid[px + 1] = gv;
                        srcGrid[px + 2] = rv;
                        srcGrid[px + 3] = 255;
                    }
                }
            }

            fixed (byte* pSrc = srcGrid)
            fixed (byte* pDst = dstGrid)
            {
                if (!TranslateBitmapBits(
                        hProofTransform,
                        (IntPtr)pSrc,
                        BM_xRGBQUADS,
                        (uint)width,
                        (uint)height,
                        (uint)stride,
                        (IntPtr)pDst,
                        BM_xRGBQUADS,
                        (uint)stride,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    return false;
                }
            }

            byte[]? rawGamut = null;
            byte[]? gamutRoundTripGrid = null;
            if (options.ShowGamutWarning)
            {
                // 优先尝试原生 ICM gamt 表检测；由于多数 RGB 矩阵或 WCS 配置文件不含 gamt 标签或返回全 0，
                // 同步参考相对比色往返的 CIE Lab 色差。
                IntPtr[] gamutProfiles = [hSrgb, hTarget];
                uint[] gamutIntents = [(uint)options.Intent];
                hGamutTransform = CreateMultiProfileTransform(
                    gamutProfiles,
                    2,
                    gamutIntents,
                    1,
                    BEST_MODE | ENABLE_GAMUT_CHECKING,
                    0);

                if (hGamutTransform != IntPtr.Zero)
                {
                    rawGamut = new byte[pixelCount];
                    fixed (byte* pSrc = srcGrid)
                    {
                        if (!CheckBitmapBits(
                                hGamutTransform,
                                (IntPtr)pSrc,
                                BM_xRGBQUADS,
                                (uint)width,
                                (uint)height,
                                (uint)stride,
                                rawGamut,
                                IntPtr.Zero,
                                IntPtr.Zero))
                        {
                            rawGamut = null;
                        }
                    }
                }

                if (options.Intent == SoftProofRenderingIntent.RelativeColorimetric && !options.SimulatePaperWhite)
                {
                    gamutRoundTripGrid = dstGrid;
                }
                else
                {
                    IntPtr hRelProof = CreateProofTransform(hSrgb, hTarget, SoftProofOptions.Default);
                    if (hRelProof != IntPtr.Zero)
                    {
                        try
                        {
                            gamutRoundTripGrid = new byte[pixelCount * 4];
                            fixed (byte* pSrc = srcGrid)
                            fixed (byte* pRelDst = gamutRoundTripGrid)
                            {
                                if (!TranslateBitmapBits(
                                        hRelProof,
                                        (IntPtr)pSrc,
                                        BM_xRGBQUADS,
                                        (uint)width,
                                        (uint)height,
                                        (uint)stride,
                                        (IntPtr)pRelDst,
                                        BM_xRGBQUADS,
                                        (uint)stride,
                                        IntPtr.Zero,
                                        IntPtr.Zero))
                                {
                                    gamutRoundTripGrid = null;
                                }
                            }
                        }
                        finally
                        {
                            DeleteColorTransform(hRelProof);
                        }
                    }
                }
            }

            float[] baked = new float[n * n * n * 3];
            byte[]? bakedGamut = options.ShowGamutWarning ? new byte[n * n * n] : null;
            for (int b = 0; b < n; b++)
            {
                int rowOff = b * stride;
                int gRowOff = b * width;
                for (int g = 0; g < n; g++)
                {
                    for (int r = 0; r < n; r++)
                    {
                        int cell = g * n + r;
                        int px = rowOff + cell * 4;
                        int lutCell = (r * n + g) * n + b;
                        int dstIdx = lutCell * 3;
                        baked[dstIdx] = dstGrid[px + 2];
                        baked[dstIdx + 1] = dstGrid[px + 1];
                        baked[dstIdx + 2] = dstGrid[px];

                        if (bakedGamut != null)
                        {
                            byte maskVal = rawGamut != null ? rawGamut[gRowOff + cell] : (byte)0;
                            if (gamutRoundTripGrid != null)
                            {
                                byte labMask = ComputeOutOfGamutScore(
                                    srcGrid[px + 2], srcGrid[px + 1], srcGrid[px],
                                    gamutRoundTripGrid[px + 2], gamutRoundTripGrid[px + 1], gamutRoundTripGrid[px]);
                                if (labMask > maskVal) maskVal = labMask;
                            }
                            bakedGamut[lutCell] = maskVal;
                        }
                    }
                }
            }

            lut33 = baked;
            gamutMask33 = bakedGamut;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hGamutTransform != IntPtr.Zero) DeleteColorTransform(hGamutTransform);
            if (hProofTransform != IntPtr.Zero) DeleteColorTransform(hProofTransform);
            if (hTarget != IntPtr.Zero) CloseColorProfile(hTarget);
            if (hSrgb != IntPtr.Zero) CloseColorProfile(hSrgb);
        }
    }

    private static readonly double[] SrgbByteToLinear = BuildSrgbByteToLinearLut();

    private static double[] BuildSrgbByteToLinearLut()
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            lut[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return lut;
    }

    private static byte ComputeOutOfGamutScore(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        // 快速跳过几乎无变化的色域内像素
        if (Math.Abs(r1 - r2) <= 4 && Math.Abs(g1 - g2) <= 4 && Math.Abs(b1 - b2) <= 4)
            return 0;

        RgbToCieLab(r1, g1, b1, out double l1, out double a1, out double bb1);
        RgbToCieLab(r2, g2, b2, out double l2, out double a2, out double bb2);
        double dl = l1 - l2;
        double da = a1 - a2;
        double db = bb1 - bb2;
        double dChroma = Math.Sqrt(da * da + db * db);
        double dE = Math.Sqrt(dl * dl + da * da + db * db);

        // 排除中性暗部 Dmax 纸黑轻微上抬（dChroma 很小且 dE 适中）造成的误报，精准捕捉真正超色域的高饱和色
        if (dChroma >= 8.5 || (dE >= 12.0 && dChroma >= 3.5) || dE >= 16.0)
        {
            return (byte)Math.Clamp((int)Math.Round(dE * 6.0), 32, 255);
        }
        return 0;
    }

    private static void RgbToCieLab(byte r, byte g, byte b, out double l, out double a, out double bb)
    {
        double rl = SrgbByteToLinear[r];
        double gl = SrgbByteToLinear[g];
        double bl = SrgbByteToLinear[b];

        double x = (rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375) / 0.95047;
        double y = (rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750);
        double z = (rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041) / 1.08883;

        double fx = x > 0.008856 ? Math.Cbrt(x) : 7.787 * x + 16.0 / 116.0;
        double fy = y > 0.008856 ? Math.Cbrt(y) : 7.787 * y + 16.0 / 116.0;
        double fz = z > 0.008856 ? Math.Cbrt(z) : 7.787 * z + 16.0 / 116.0;

        l = 116.0 * fy - 16.0;
        a = 500.0 * (fx - fy);
        bb = 200.0 * (fy - fz);
    }

    private static IntPtr CreateProofTransform(IntPtr hSrgb, IntPtr hTarget, SoftProofOptions options)
    {
        IntPtr[] profiles = [hSrgb, hTarget, hSrgb];
        uint intentToTarget = (uint)Math.Clamp((int)options.Intent, 0, 3);
        uint intentToDisplay = options.SimulatePaperWhite ? 3u : 1u; // 3=AbsoluteColorimetric (模拟纸白), 1=RelativeColorimetric
        // Windows CreateMultiProfileTransform 要求 nIntents 必须为 1 或等于 nProfiles(3)：
        // [0]=源配置文件意图, [1]=sRGB->目标纸张意图, [2]=目标纸张->显示回转意图（绝对色度即模拟纸白）
        uint[] intents = [intentToTarget, intentToTarget, intentToDisplay];

        // ICM preserves the absolute-colorimetric media white for ICC proof profiles.
        uint preferredFlags = options.SimulatePaperWhite ? BEST_MODE : BEST_MODE | WCS_ALWAYS;
        IntPtr handle = CreateMultiProfileTransform(profiles, 3, intents, 3, preferredFlags, 0);
        if (handle == IntPtr.Zero && preferredFlags != BEST_MODE)
            handle = CreateMultiProfileTransform(profiles, 3, intents, 3, BEST_MODE, 0);
        if (handle == IntPtr.Zero && !options.SimulatePaperWhite)
            handle = CreateMultiProfileTransform(profiles, 3, [intentToTarget], 1, BEST_MODE, 0);
        return handle;
    }

    private static IntPtr OpenMemoryProfile(byte[] bytes)
    {
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var prof = new PROFILE
            {
                dwType = PROFILE_MEMBUFFER,
                pProfileData = pin.AddrOfPinnedObject(),
                cbDataSize = (uint)bytes.Length
            };
            return OpenColorProfileW(ref prof, PROFILE_READ, FILE_SHARE_READ, OPEN_EXISTING);
        }
        finally
        {
            pin.Free();
        }
    }

    private static byte[] LoadOrBuildStandardSrgbIcc()
    {
        if (OperatingSystem.IsWindows())
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string winSrgb = Path.Combine(sysDir, "spool", "drivers", "color", "sRGB Color Space Profile.icm");
            if (File.Exists(winSrgb))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(winSrgb);
                    if (InspectProfileHeader(bytes).IsValid)
                        return bytes;
                }
                catch
                {
                    // Fallback to synthesized minimal sRGB v2 profile
                }
            }
        }
        return BuildMinimalSrgbIccV2();
    }

    /// <summary>
    /// 构造符合 ICC.1:2001-04 (v2.1) 规范的紧凑 sRGB IEC61966-2.1 色彩配置文件（含 D50 适应矩阵与 sRGB 1024 点 TRC），
    /// 作为跨平台或系统缺少 sRGB.icm 时的零依赖兜底。
    /// </summary>
    public static byte[] BuildMinimalSrgbIccV2()
    {
        // Tag 列表: desc, cprt, wtpt, rXYZ, gXYZ, bXYZ, rTRC, gTRC(共享), bTRC(共享) -> 9 tags
        byte[] descData = BuildTextDescriptionTag("sRGB IEC61966-2.1");
        byte[] cprtData = BuildTextTag("Public Domain");
        byte[] wtptData = BuildXyzTag(0.9642, 1.0000, 0.8249); // D50 PCS white
        // IEC 61966-2-1 D50-adapted sRGB primaries
        byte[] rXyzData = BuildXyzTag(0.43606567, 0.22248840, 0.01391602);
        byte[] gXyzData = BuildXyzTag(0.38514709, 0.71687317, 0.09707642);
        byte[] bXyzData = BuildXyzTag(0.14306641, 0.06060791, 0.71409607);
        byte[] trcData = BuildSrgbCurveTag(256);

        var uniqueChunks = new List<(string Sig, byte[] Data)>
        {
            ("desc", descData),
            ("cprt", cprtData),
            ("wtpt", wtptData),
            ("rXYZ", rXyzData),
            ("gXYZ", gXyzData),
            ("bXYZ", bXyzData),
            ("rTRC", trcData)
        };

        const int tagCount = 9;
        int offset = Align4(128 + 4 + tagCount * 12);
        var offsets = new Dictionary<string, (int Offset, int Size)>();
        foreach (var (sig, data) in uniqueChunks)
        {
            offsets[sig] = (offset, data.Length);
            offset = Align4(offset + data.Length);
        }

        byte[] icc = new byte[offset];
        var span = icc.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], (uint)icc.Length);
        WriteAscii(span, 8, "\x02\x10\x00\x00"); // version 2.1.0
        WriteAscii(span, 12, "mntr");
        WriteAscii(span, 16, "RGB ");
        WriteAscii(span, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(span[24..26], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..28], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..30], 1);
        WriteAscii(span, 36, "acsp");
        WriteAscii(span, 40, "MSFT");
        // PCS D50 illuminant (bytes 68..79)
        WriteS15Fixed16(span, 68, 0.9642);
        WriteS15Fixed16(span, 72, 1.0000);
        WriteS15Fixed16(span, 76, 0.8249);

        BinaryPrimitives.WriteUInt32BigEndian(span[128..132], tagCount);
        (string Tag, string Ref)[] entries =
        [
            ("desc", "desc"), ("cprt", "cprt"), ("wtpt", "wtpt"),
            ("rXYZ", "rXYZ"), ("gXYZ", "gXYZ"), ("bXYZ", "bXYZ"),
            ("rTRC", "rTRC"), ("gTRC", "rTRC"), ("bTRC", "rTRC")
        ];
        for (int i = 0; i < entries.Length; i++)
        {
            int tOff = 132 + i * 12;
            var (dataOff, dataLen) = offsets[entries[i].Ref];
            WriteAscii(span, tOff, entries[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(tOff + 4, 4), (uint)dataOff);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(tOff + 8, 4), (uint)dataLen);
        }

        foreach (var (sig, data) in uniqueChunks)
            data.CopyTo(icc, offsets[sig].Offset);

        return icc;
    }

    private static int Align4(int v) => (v + 3) & ~3;

    private static void WriteAscii(Span<byte> dst, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
            dst[offset + i] = (byte)text[i];
    }

    private static void WriteS15Fixed16(Span<byte> dst, int offset, double value)
    {
        int fixedVal = (int)Math.Round(value * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(offset, 4), fixedVal);
    }

    private static byte[] BuildXyzTag(double x, double y, double z)
    {
        byte[] buf = new byte[20];
        WriteAscii(buf, 0, "XYZ ");
        WriteS15Fixed16(buf, 8, x);
        WriteS15Fixed16(buf, 12, y);
        WriteS15Fixed16(buf, 16, z);
        return buf;
    }

    private static byte[] BuildTextTag(string text)
    {
        byte[] buf = new byte[8 + text.Length + 1];
        WriteAscii(buf, 0, "text");
        WriteAscii(buf, 8, text);
        return buf;
    }

    private static byte[] BuildTextDescriptionTag(string text)
    {
        int asciiLen = text.Length + 1;
        byte[] buf = new byte[12 + asciiLen + 8 + 67 + 3];
        WriteAscii(buf, 0, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)asciiLen);
        WriteAscii(buf, 12, text);
        return buf;
    }

    private static byte[] BuildSrgbCurveTag(int count)
    {
        byte[] buf = new byte[12 + count * 2];
        WriteAscii(buf, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)count);
        for (int i = 0; i < count; i++)
        {
            double c = i / (double)(count - 1);
            double lin = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            ushort val = (ushort)Math.Clamp(Math.Round(lin * 65535.0), 0, 65535);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(12 + i * 2, 2), val);
        }
        return buf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROFILE
    {
        public uint dwType;
        public IntPtr pProfileData;
        public uint cbDataSize;
    }

    [DllImport("mscms.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenColorProfileW(ref PROFILE pProfile, uint dwDesiredAccess, uint dwShareMode, uint dwCreationMode);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseColorProfile(IntPtr hColorProfile);

    [DllImport("mscms.dll", SetLastError = true)]
    private static extern IntPtr CreateMultiProfileTransform(
        IntPtr[] pahProfiles,
        uint nProfiles,
        uint[] padwIntent,
        uint nIntents,
        uint dwFlags,
        uint indexPreferredCMM);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteColorTransform(IntPtr hColorTransform);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateBitmapBits(
        IntPtr hColorTransform,
        IntPtr pSrcBits,
        uint bmInput,
        uint dwWidth,
        uint dwHeight,
        uint dwInputStride,
        IntPtr pDestBits,
        uint bmOutput,
        uint dwOutputStride,
        IntPtr pfnCallBack,
        IntPtr ulCallbackData);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckBitmapBits(
        IntPtr hColorTransform,
        IntPtr pSrcBits,
        uint bmInput,
        uint dwWidth,
        uint dwHeight,
        uint dwStride,
        [Out] byte[] paResult,
        IntPtr pfnCallback,
        IntPtr lpCallbackData);
}
