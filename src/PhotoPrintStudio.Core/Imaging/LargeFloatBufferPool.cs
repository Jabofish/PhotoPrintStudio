namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 面向大分辨率图像的受控浮点数组复用池：
/// 常规预览缓冲常驻复用（零 LOH 抖动），超过 16 MiB 的全分辨率导出缓冲在批量任务内复用并在空闲 6 秒后自动释放归还系统内存。
/// </summary>
internal sealed class LargeFloatBufferPool
{
    public const int LargePlaneElements = 4_194_304; // 16 MiB per float plane

    private readonly object _lock = new();
    private readonly List<(float[] Buffer, long LastUsedTick)> _small = [];
    private readonly List<(float[] Buffer, long LastUsedTick)> _large = [];
    private readonly Timer _trimTimer;

    public LargeFloatBufferPool()
    {
        _trimTimer = new Timer(_ => TrimIdleLargeBuffers(), null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
    }

    public float[] Rent(int minLength)
    {
        var list = minLength > LargePlaneElements ? _large : _small;
        int maxAccepted = minLength <= 16384 ? Math.Max(16384, minLength * 2) : (int)(minLength * 1.45);
        lock (_lock)
        {
            int bestIdx = -1;
            int bestLen = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                int len = list[i].Buffer.Length;
                if (len >= minLength && len <= maxAccepted && len < bestLen)
                {
                    bestLen = len;
                    bestIdx = i;
                    if (len == minLength) break;
                }
            }
            if (bestIdx >= 0)
            {
                var buf = list[bestIdx].Buffer;
                list.RemoveAt(bestIdx);
                return buf;
            }
        }
        return new float[minLength];
    }

    public void Return(float[] buffer)
    {
        long now = Environment.TickCount64;
        bool isLarge = buffer.Length > LargePlaneElements;
        var list = isLarge ? _large : _small;
        int maxCap = isLarge ? 8 : 16;

        lock (_lock)
        {
            if (list.Count >= maxCap)
            {
                // 优先驱逐最早放入/最久未用的条目
                list.RemoveAt(0);
            }
            list.Add((buffer, now));
        }
    }

    private void TrimIdleLargeBuffers()
    {
        long cutoff = Environment.TickCount64 - 6000;
        lock (_lock)
        {
            for (int i = _large.Count - 1; i >= 0; i--)
            {
                if (_large[i].LastUsedTick < cutoff)
                    _large.RemoveAt(i);
            }
        }
    }
}
