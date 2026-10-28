using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace DfoServer;

// 仅专属自测child使用。观察现有SQLite提交边界，不注入生产仓储时序或异常触发器。
internal sealed class LukeRewardPrecommitObserver : IDisposable
{
    internal sealed record Proof(string Library,int Version,string Source,string Database,long Connection,long ReceiptRowId,int Inserts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Registration(nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Entry(nint db,nint error,nint api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Hook(nint db,nint callback,nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Update(nint context,int operation,nint database,nint table,long rowid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Commit(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Filename(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint SourceId();
    sealed class State(string path) { internal string Path=path; internal int Inserts; internal long Row; }
    static LukeRewardPrecommitObserver? processRoot;
    readonly Dictionary<nint,State> states=[];
    readonly string database,library,source;
    readonly int version;
    readonly Action<Proof> reached;
    readonly Action<string> failed;
    readonly Registration cancel;
    readonly Hook updateHook,commitHook;
    readonly Filename filename;
    readonly Entry entry;
    readonly Update update;
    readonly Commit commit;
    readonly nint entryPointer;
    // 句柄不Free，委托与上下文强根覆盖整个child生命周期；不hook原EXE或父进程。
    readonly nint native;
    bool armed,disposed,broken,targetAllowed;

    internal LukeRewardPrecommitObserver(string path,Action<Proof> reached,Action<string> failed)
    {
        if(processRoot is not null)throw new InvalidOperationException("专属child只能装配一个提交前观察器");
        database=Path.GetFullPath(path);this.reached=reached;this.failed=failed;
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64||!(OperatingSystem.IsLinux()||OperatingSystem.IsWindows()))
            throw new PlatformNotSupportedException("提交前观察器仅已登记Linux/Windows x64 ABI，不跳过测试");
        string file=OperatingSystem.IsWindows()?"e_sqlite3.dll":"libe_sqlite3.so";
        string rid=OperatingSystem.IsWindows()?"win-x64":"linux-x64";
        library=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"runtimes",rid,"native",file));
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        // Repository已实际初始化provider；只接受它已经加载的本项目runtime模块，不偷偷另装系统SQLite。
        using var process=Process.GetCurrentProcess();
        var modules=process.Modules.Cast<ProcessModule>().Where(module=>Path.GetFileName(module.FileName).Equals(file,comparison)).ToArray();
        if(modules.Length!=1||!Path.GetFullPath(modules[0].FileName).Equals(library,comparison))
            throw new PlatformNotSupportedException("真实SQLite provider模块路径不匹配项目runtime");
        native=NativeLibrary.Load(library);
        T Bind<T>(string name) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(native,name));
        version=Bind<Version>("sqlite3_libversion_number")();source=Marshal.PtrToStringUTF8(Bind<SourceId>("sqlite3_sourceid")())??"";
        if(version!=SQLitePCL.raw.sqlite3_libversion_number()||source!=SQLitePCL.raw.sqlite3_sourceid().utf8_to_string()||source.Length==0)
            throw new PlatformNotSupportedException("SQLite ABI版本/sourceid与真实provider不一致");
        filename=Bind<Filename>("sqlite3_db_filename");updateHook=Bind<Hook>("sqlite3_update_hook");commitHook=Bind<Hook>("sqlite3_commit_hook");
        cancel=Bind<Registration>("sqlite3_cancel_auto_extension");entry=Opened;update=Changed;commit=Committing;
        entryPointer=Marshal.GetFunctionPointerForDelegate(entry);
        processRoot=this; // 原生函数指针不是GC根，必须显式保活至child结束。
        if(Bind<Registration>("sqlite3_auto_extension")(entryPointer)!=0)throw new InvalidOperationException("测试自动扩展注册失败");
        armed=true;
    }
    internal void VerifyScopeAndArm()
    {
        // 独立内存库的同名表、匹配库TEMP同名表都不得被当作真实main receipt。
        using(var other=new SqliteConnection("Data Source=:memory:;Pooling=False"))
        {
            other.Open();using var command=other.CreateCommand();
            command.CommandText="CREATE TABLE character_raid_reward_receipts(v); INSERT INTO character_raid_reward_receipts VALUES(1)";command.ExecuteNonQuery();
        }
        using(var matching=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            matching.Open();using var command=matching.CreateCommand();
            command.CommandText="CREATE TEMP TABLE character_raid_reward_receipts(v); INSERT INTO temp.character_raid_reward_receipts VALUES(1); SELECT count(*) FROM main.character_raid_reward_receipts";
            command.ExecuteScalar();
        }
        if(broken||states.Count==0||states.Values.Any(state=>state.Inserts!=0))throw new InvalidDataException("SQLite测试观察器库/表范围自检失败");
        targetAllowed=true;
    }

    int Opened(nint db,nint error,nint api)
    {
        try
        {
            string? path=Marshal.PtrToStringUTF8(filename(db,"main"));
            if(string.IsNullOrEmpty(path)||!Path.GetFullPath(path).Equals(database,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return 0;
            if(states.Count>=64&&!states.ContainsKey(db))throw new InvalidDataException("测试连接观察超过有界上限");
            states[db]=new(Path.GetFullPath(path)); // 指针复用必须清除旧连接标记，不用旧receipt授权新commit。
            updateHook(db,Marshal.GetFunctionPointerForDelegate(update),db);
            commitHook(db,Marshal.GetFunctionPointerForDelegate(commit),db);
            return 0;
        }
        catch(Exception exception){Fail(exception);return 1;}
    }
    void Changed(nint context,int operation,nint dbName,nint table,long rowid)
    {
        try
        {
            if(operation!=18||Marshal.PtrToStringUTF8(dbName)!="main"||Marshal.PtrToStringUTF8(table)!="character_raid_reward_receipts")return;
            if(!targetAllowed||!states.TryGetValue(context,out var state)||++state.Inserts!=1)throw new InvalidDataException("非唯一或未知连接receipt INSERT");
            state.Row=rowid;
        }
        catch(Exception exception){Fail(exception);}
    }
    int Committing(nint context)
    {
        try
        {
            if(broken)return 1; // 普通错误回滚会由worker报失败，不记为进程崩溃通过。
            if(!states.TryGetValue(context,out var state)||state.Inserts==0)return 0;
            if(state.Inserts!=1||state.Row<=0)throw new InvalidDataException("提交前receipt证据不完整");
            reached(new(library,version,source,state.Path,context.ToInt64(),state.Row,state.Inserts));
            // 绝不先return0再通知。此处不调用任何SQLite API/SELECT；只有parent能终止本child。
            using var hold=new ManualResetEventSlim(false);hold.Wait();
            return 1;
        }
        catch(Exception exception){Fail(exception);return 1;}
    }
    void Fail(Exception exception)
    {
        broken=true;
        try{failed(exception.GetType().Name+": "+exception.Message);}catch{ /* 不允许托管异常越过原生回调边界。 */ }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(armed&&cancel(entryPointer)!=1)throw new InvalidOperationException("测试扩展未能精确取消");
        GC.KeepAlive(entry);GC.KeepAlive(update);GC.KeepAlive(commit);
        // 不reset其它扩展，不在可能仍有原生连接回调时释放library/delegate。
    }
}
