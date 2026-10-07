using System.Runtime.ExceptionServices;
using System.Text;

namespace DfoServer.Infrastructure;

// 每个文件一个写入器和固定缓冲；没有逐条任务或待写消息队列。
internal sealed class BufferedLogFile : IDisposable
{
    readonly object gate = new();
    readonly Func<Stream> open;
    readonly Action<Exception> onFailure;
    readonly TimeSpan interval;
    StreamWriter? writer;
    Timer? timer;
    Exception? failure;
    bool dirty;
    bool disposed;

    public BufferedLogFile(string path, Action<Exception> onFailure) : this(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 32768);
    }, onFailure, TimeSpan.FromSeconds(1)) { }

    internal BufferedLogFile(Func<Stream> open, Action<Exception> onFailure, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        this.open = open;
        this.onFailure = onFailure;
        this.interval = interval;
    }

    public void Append(string text)
    {
        Exception? error = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
            try
            {
                if (writer is null)
                {
                    writer = new StreamWriter(open(), new UTF8Encoding(false), 16384);
                    timer = new Timer(_ => FlushFromTimer(), null, interval, interval);
                }
                writer.Write(text);
                dirty = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = Fail(ex);
            }
        }
        if (error is not null)
        {
            ReportFailure(error);
            ExceptionDispatchInfo.Throw(error);
        }
    }

    public void Flush()
    {
        Exception? error = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
            try { FlushWriter(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Fail(ex); }
        }
        if (error is not null)
        {
            ReportFailure(error);
            ExceptionDispatchInfo.Throw(error);
        }
    }

    void FlushFromTimer()
    {
        Exception? error = null;
        lock (gate)
        {
            if (disposed || failure is not null)
                return;
            try { FlushWriter(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Fail(ex); }
        }
        if (error is not null)
            ReportFailure(error);
    }

    void ReportFailure(Exception error)
    {
        try { onFailure(error); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    void FlushWriter()
    {
        if (!dirty)
            return;
        writer!.Flush();
        dirty = false;
    }

    Exception Fail(Exception error)
    {
        failure = error;
        timer?.Dispose();
        timer = null;
        try { writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        writer = null;
        return error;
    }

    public void Dispose()
    {
        Exception? error;
        bool report = false;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            timer?.Dispose();
            timer = null;
            error = failure;
            try { writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error ??= ex;
                report = failure is null;
            }
            writer = null;
            dirty = false;
        }
        if (error is not null)
        {
            if (report) ReportFailure(error);
            ExceptionDispatchInfo.Throw(error);
        }
    }
}
