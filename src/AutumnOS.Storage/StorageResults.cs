namespace AutumnOS.Storage;

public static class StorageErrors
{
    public const string None = "NONE";
    public const string AccessDenied = "DATA_ACCESS_DENIED";
    public const string IoError = "DATA_IO_ERROR";
    public const string UnsafePath = "DATA_UNSAFE_PATH";
    public const string PathConflict = "DATA_PATH_CONFLICT";
    public const string Cancelled = "USER_CANCELLED";
    public const string CorruptState = "CONFIG_CORRUPT";
    public const string FutureSchema = "CONFIG_FUTURE_SCHEMA";
    public const string Busy = "CONFIG_BUSY";
    public const string InvalidTransition = "CONFIG_INVALID_TRANSITION";
    public const string LogFull = "LOG_CAPACITY_REACHED";
    public const string Maintenance = "MAINTENANCE_IN_PROGRESS";

    internal static bool IsKnown(string code) => code is None or AccessDenied or IoError or UnsafePath
        or PathConflict or Cancelled or CorruptState or FutureSchema or Busy or InvalidTransition or LogFull or Maintenance;

    internal static string Recovery(string code) => code switch
    {
        None => "数据目录已就绪。",
        AccessDenied => "无法写入程序旁的数据目录。请关闭程序后将完整程序和 AutumnOS_Data 一起移动到当前用户可写的位置，再重试。",
        UnsafePath => "数据目录或文件包含链接/重解析点。请关闭程序并检查路径；保留数据，不要将数据目录重定向到其他位置。",
        PathConflict => "所需目录被同名文件占用，或状态文件被目录占用。请关闭程序，备份并处理冲突后重试。",
        Cancelled => "初始化已取消；已有数据与已保存的进度保留，下次启动可继续。",
        CorruptState => "首次启动配置无法读取。原文件已保留；请先备份并检查配置，再重试。",
        FutureSchema => "首次启动配置来自较新版本。原文件已保留，请使用兼容的 AutumnOS 版本。",
        Busy => "另一个进程正在保存首次启动状态。请稍后重试。",
        InvalidTransition => "首次启动状态只能保持或向下一步推进。请重新读取当前状态。",
        LogFull => "诊断日志已达到容量上限；现有日志保留，本次记录未写入。",
        Maintenance => "系统更新正在准备或提交；本次改动尚未保存，原配置保留。请在维护结束后重试。",
        _ => "数据操作失败，已有数据保留。请检查磁盘空间与文件占用后重试。"
    };
}

public sealed record DataRootResult(bool Success, string ErrorCode, string RecoveryMessage)
{
    internal static DataRootResult Ready() => new(true, StorageErrors.None, StorageErrors.Recovery(StorageErrors.None));
    internal static DataRootResult Failed(string code) => new(false, code, StorageErrors.Recovery(code));
}

internal sealed class StoragePathException(string errorCode) : IOException
{
    internal string ErrorCode { get; } = errorCode;
}

internal static class StorageExceptionMapper
{
    internal static string Code(Exception exception) => exception switch
    {
        StoragePathException path => path.ErrorCode,
        DataStoreException data => data.Code,
        UnauthorizedAccessException or System.Security.SecurityException => StorageErrors.AccessDenied,
        OperationCanceledException => StorageErrors.Cancelled,
        _ => StorageErrors.IoError
    };

    internal static bool CanHandle(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or OperationCanceledException;
}
