using Microsoft.Data.Sqlite;
namespace FctAggregator;
public partial class AggDatabase
{
    public void InsertAlertPredictLog(string machine, string rule, string level, double current, double predicted, string detail)
    {
        if(string.IsNullOrWhiteSpace(machine)) return;
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock(_writeLock){ Open(); using var cmd=_conn!.CreateCommand(); cmd.CommandText="INSERT INTO alert_predict_log (ts, machine, rule, level, current, predicted, detail) VALUES (@ts,@m,@r,@lv,@cur,@pred,@d)"; cmd.Parameters.AddWithValue("@ts", ts); cmd.Parameters.AddWithValue("@m", machine); cmd.Parameters.AddWithValue("@r", rule); cmd.Parameters.AddWithValue("@lv", level); cmd.Parameters.AddWithValue("@cur", current); cmd.Parameters.AddWithValue("@pred", predicted); cmd.Parameters.AddWithValue("@d", detail ?? ""); cmd.ExecuteNonQuery(); }
    }
    public List<(long id,string ts,string machine,string rule,string level,double current,double predicted,string detail)> QueryAlertPredicts(string? machine=null, int limit=100)
    {
        var list = new List<(long,string,string,string,string,double,double,string)>();
        limit=Math.Clamp(limit,1,500);
        using var conn=OpenReader(); using var cmd=conn.CreateCommand();
        if(!string.IsNullOrEmpty(machine)){ cmd.CommandText="SELECT id, ts, machine, rule, level, current, predicted, detail FROM alert_predict_log WHERE machine=@m ORDER BY ts DESC, id DESC LIMIT @lim"; cmd.Parameters.AddWithValue("@m", machine!); }
        else { cmd.CommandText="SELECT id, ts, machine, rule, level, current, predicted, detail FROM alert_predict_log ORDER BY ts DESC, id DESC LIMIT @lim"; }
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r=cmd.ExecuteReader(); while(r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1)?"":r.GetString(1), r.IsDBNull(2)?"":r.GetString(2), r.IsDBNull(3)?"":r.GetString(3), r.IsDBNull(4)?"":r.GetString(4), r.IsDBNull(5)?0:r.GetDouble(5), r.IsDBNull(6)?0:r.GetDouble(6), r.IsDBNull(7)?"":r.GetString(7)));
        return list;
    }
}
