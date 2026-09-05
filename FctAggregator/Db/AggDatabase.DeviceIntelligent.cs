using Microsoft.Data.Sqlite;

namespace FctAggregator;

public partial class AggDatabase
{
    public void InsertFctChange(string machine, string detail, string hash)
    {
        if(string.IsNullOrWhiteSpace(machine)) return;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock(_writeLock){ Open(); using var cmd=_conn!.CreateCommand(); cmd.CommandText="INSERT INTO fct_change_log (ts, machine, detail, hash) VALUES (@ts,@m,@d,@h)"; cmd.Parameters.AddWithValue("@ts", now); cmd.Parameters.AddWithValue("@m", machine); cmd.Parameters.AddWithValue("@d", detail ?? ""); cmd.Parameters.AddWithValue("@h", hash ?? ""); cmd.ExecuteNonQuery(); }
    }
    public List<(long id, string ts, string machine, string detail, string hash)> QueryFctChanges(string? machine=null, int limit=100)
    {
        var list = new List<(long,string,string,string,string)>();
        limit=Math.Clamp(limit,1,500);
        using var conn=OpenReader(); using var cmd=conn.CreateCommand();
        if(!string.IsNullOrEmpty(machine)){ cmd.CommandText="SELECT id, ts, machine, detail, hash FROM fct_change_log WHERE machine=@m ORDER BY ts DESC, id DESC LIMIT @lim"; cmd.Parameters.AddWithValue("@m", machine!); }
        else { cmd.CommandText="SELECT id, ts, machine, detail, hash FROM fct_change_log ORDER BY ts DESC, id DESC LIMIT @lim"; }
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r=cmd.ExecuteReader(); while(r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1)?"":r.GetString(1), r.IsDBNull(2)?"":r.GetString(2), r.IsDBNull(3)?"":r.GetString(3), r.IsDBNull(4)?"":r.GetString(4)));
        return list;
    }

    public void InsertDevicePredictLog(string machine, string metric, string level, double predicted, int? daysToExhaust, string detail)
    {
        if(string.IsNullOrWhiteSpace(machine)) return;
        var now=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock(_writeLock){ Open(); using var cmd=_conn!.CreateCommand(); cmd.CommandText="INSERT INTO device_predict_log (ts, machine, metric, level, predicted, days_to_exhaust, detail) VALUES (@ts,@m,@met,@lv,@pred,@days,@d)"; cmd.Parameters.AddWithValue("@ts", now); cmd.Parameters.AddWithValue("@m", machine); cmd.Parameters.AddWithValue("@met", metric ?? ""); cmd.Parameters.AddWithValue("@lv", level ?? ""); cmd.Parameters.AddWithValue("@pred", predicted); cmd.Parameters.AddWithValue("@days", (object?)daysToExhaust ?? DBNull.Value); cmd.Parameters.AddWithValue("@d", detail ?? ""); cmd.ExecuteNonQuery(); }
    }
    public List<(long id,string ts,string machine,string metric,string level,double predicted,int? days,string detail)> QueryDevicePredicts(string? machine=null, int limit=100)
    {
        var list = new List<(long,string,string,string,string,double,int?,string)>();
        limit=Math.Clamp(limit,1,500);
        using var conn=OpenReader(); using var cmd=conn.CreateCommand();
        if(!string.IsNullOrEmpty(machine)){ cmd.CommandText="SELECT id, ts, machine, metric, level, predicted, days_to_exhaust, detail FROM device_predict_log WHERE machine=@m ORDER BY ts DESC, id DESC LIMIT @lim"; cmd.Parameters.AddWithValue("@m", machine!); }
        else { cmd.CommandText="SELECT id, ts, machine, metric, level, predicted, days_to_exhaust, detail FROM device_predict_log ORDER BY ts DESC, id DESC LIMIT @lim"; }
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r=cmd.ExecuteReader(); while(r.Read()){ int? days=null; if(!r.IsDBNull(6)) days=r.GetInt32(6); list.Add((r.GetInt64(0), r.IsDBNull(1)?"":r.GetString(1), r.IsDBNull(2)?"":r.GetString(2), r.IsDBNull(3)?"":r.GetString(3), r.IsDBNull(4)?"":r.GetString(4), r.IsDBNull(5)?0:r.GetDouble(5), days, r.IsDBNull(7)?"":r.GetString(7))); }
        return list;
    }
}
