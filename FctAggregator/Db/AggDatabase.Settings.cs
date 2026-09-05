using Microsoft.Data.Sqlite;

namespace FctAggregator;

public partial class AggDatabase
{
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
