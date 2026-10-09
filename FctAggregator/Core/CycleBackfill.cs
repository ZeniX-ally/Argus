namespace FctAggregator;

/// <summary>把今天还没记节拍的结果文件补读一遍。没有总时长的也记下，避免下次再读。</summary>
public static class CycleBackfill
{
    public static int Run(Database db, string stationId, string dateYmd, int limit)
    {
        var paths = db.ListMissingCyclePaths(stationId, dateYmd, limit);
        foreach (var path in paths)
        {
            double? sec = null;
            try
            {
                if (File.Exists(path))
                    sec = XmlParser.ReadCycleSecondsFromFile(path);
            }
            catch (Exception ex) { Logger.Warning($"[节拍] 补读失败 {path}: {ex.Message}"); }
            db.SaveCycle(path, sec);
        }
        return paths.Count;
    }
}
