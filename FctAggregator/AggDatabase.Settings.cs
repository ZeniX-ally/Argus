using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>
/// 聚合库设置扩展（Lite-Settings，2026-08-28）：users.layout / users.favorites 双写持久化。
/// 复用 users 表现有 TEXT 字段（layout 存卡片拖拽排列 JSON，favorites 存常用筛选 JSON），
/// 单写者 WAL，走既有 _writeLock；读短租连接；向后兼容（缺字段视为 null）。
/// </summary>
public partial class AggDatabase
{
    /// <summary>按用户名取 layout（NOCASE）；用户不存在返回 null。</summary>
    public string? GetUserLayout(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT layout FROM users WHERE name=@n COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@n", name.Trim());
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    /// <summary>按用户名取 favorites（NOCASE）；用户不存在返回 null。</summary>
    public string? GetUserFavorites(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT favorites FROM users WHERE name=@n COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@n", name.Trim());
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    /// <summary>更新用户 layout（NOCASE）。成功返回 true，未找到用户返回 false。</summary>
    public bool SetUserLayout(string name, string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET layout=@l WHERE name=@n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@l", (object?)layoutJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", name.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>更新用户 favorites（NOCASE）。成功返回 true，未找到用户返回 false。</summary>
    public bool SetUserFavorites(string name, string? favJson)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET favorites=@f WHERE name=@n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@f", (object?)favJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", name.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
