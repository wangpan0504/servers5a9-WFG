using System.Diagnostics;

namespace PvfLib;

public readonly record struct PvfDocumentReadMetrics(long ReadTicks, long IndexTicks,
    long ReadAllocatedBytes, long IndexAllocatedBytes, bool ReadCompleted, bool IndexCompleted);

/// <summary>显式同步诊断；分配为当前线程差分，不代表进程总量或保留内存。</summary>
public static class PvfDocumentReader
{
    public static PvfDocument ReadProfiled(Func<string, string> readText, string path, bool imported,
        Action<bool, PvfDocumentReadMetrics> observe)
    {
        long readTicks = 0, indexTicks = 0, readBytes = 0, indexBytes = 0;
        bool readCompleted = false, indexCompleted = false;
        try
        {
            string text;
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            try { text = readText(path); readCompleted = true; }
            finally
            {
                readTicks = Stopwatch.GetTimestamp() - started;
                readBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            try
            {
                var result = new PvfDocument(text);
                indexCompleted = true;
                return result;
            }
            finally
            {
                indexTicks = Stopwatch.GetTimestamp() - started;
                indexBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
        }
        finally
        {
            try { observe(imported, new(readTicks, indexTicks, readBytes, indexBytes, readCompleted, indexCompleted)); }
            catch (Exception) { /* 附加诊断不得替换原读取或索引异常。 */ }
        }
    }
}
