namespace FctDistributor;

/// <summary>
/// 文件 / 目录时间戳设置。用 .NET 原生 API（Windows 上可写创建时间），无需 P/Invoke。
///
/// 大数据量下的关键点：
///   ① 用 <see cref="FileInfo"/> 的**缓存属性**判断是否已经正确 —— 已正确的直接跳过，不写盘
///   ② 写的时候只 **open 一次句柄**，三个时间一起设（比按路径设 3 次少 2 次 open）
/// </summary>
public static class FileTimeUtil
{
    /// <summary>三个时间是否都已等于 dt（容差 tol）。用 FileInfo 缓存属性，无额外系统调用。</summary>
    public static bool TimesMatch(FileInfo fi, DateTime dt, TimeSpan tol)
    {
        try
        {
            return (fi.LastWriteTime - dt).Duration() <= tol
                && (fi.CreationTime - dt).Duration() <= tol
                && (fi.LastAccessTime - dt).Duration() <= tol;
        }
        catch { return false; }
    }

    /// <summary>把文件的 创建 / 访问 / 修改 三个时间都设为 dt（句柄版，只 open 一次）。</summary>
    public static bool SetFileTimes(FileInfo fi, DateTime dt, out string error)
    {
        error = "";
        try
        {
            if (fi.IsReadOnly) fi.IsReadOnly = false;

            using var h = File.OpenHandle(fi.FullName, FileMode.Open, FileAccess.Write,
                                          FileShare.ReadWrite | FileShare.Delete);
            File.SetCreationTime(h, dt);
            File.SetLastWriteTime(h, dt);
            File.SetLastAccessTime(h, dt);
            return true;
        }
        catch (Exception ex)
        {
            // 句柄方式失败（如某些网络盘）→ 退回按路径设置
            return SetFileTimes(fi.FullName, dt, out error) || Fail(ex, out error);
        }
    }

    private static bool Fail(Exception ex, out string error) { error = ex.Message; return false; }

    /// <summary>把文件的 创建 / 访问 / 修改 三个时间都设为 dt。</summary>
    public static bool SetFileTimes(string path, DateTime dt, out string error)
    {
        error = "";
        try
        {
            // 只读属性会导致设置失败，先摘掉
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);

            File.SetCreationTime(path, dt);
            File.SetLastWriteTime(path, dt);
            File.SetLastAccessTime(path, dt);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>把目录的 创建 / 访问 / 修改 三个时间都设为 dt。</summary>
    public static bool SetDirTimes(string path, DateTime dt, out string error)
    {
        error = "";
        try
        {
            Directory.SetCreationTime(path, dt);
            Directory.SetLastWriteTime(path, dt);
            Directory.SetLastAccessTime(path, dt);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>目录时间 = 内部最晚文件 + 随机 1~300 秒（符合真实写入顺序，且不跨天）。</summary>
    public static DateTime DirStampFrom(DateTime latestFile, Random rnd)
    {
        var dt = latestFile.AddSeconds(rnd.Next(1, 301));
        if (dt.Date != latestFile.Date) dt = latestFile.AddSeconds(1);
        return dt;
    }
}
