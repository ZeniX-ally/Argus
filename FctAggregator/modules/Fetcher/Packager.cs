using System.IO.Compression;

namespace FctFetcher;

public static class Packager
{
    public sealed class Result
    {
        public string ZipPath = "";
        public int Xml, Csv, Tdms, Failed;
        public long ZipBytes;
        public int Total => Xml + Csv + Tdms;
    }

    public static string DateTag(DateTime start, DateTime end)
        => start.Date == end.Date
            ? $"{start:yyyyMMdd}"
            : $"{start:yyyyMMdd}-{end:yyyyMMdd}";

    public static Result Pack(List<Record> recs, string outDir, DateTime start, DateTime end,
                             string? xlsxPath, bool keepStage, Action<string>? log = null)
    {
        var tag = DateTag(start, end);
        var res = new Result();
        var stage = Path.Combine(outDir, tag);

        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        string dXml = Path.Combine(stage, "xml");
        string dCsv = Path.Combine(stage, "csv");
        string dTdms = Path.Combine(stage, "tdms");
        Directory.CreateDirectory(dXml);
        Directory.CreateDirectory(dCsv);
        Directory.CreateDirectory(dTdms);

        foreach (var r in recs)
        {
            if (Copy(r.XmlPath, dXml, log)) res.Xml++; else res.Failed++;
            if (r.CsvPath.Length > 0)
            {
                if (Copy(r.CsvPath, dCsv, log)) res.Csv++; else res.Failed++;
            }
            foreach (var t in r.TdmsPaths)
            {
                if (Copy(t, dTdms, log)) res.Tdms++; else res.Failed++;
            }
        }

        if (!string.IsNullOrEmpty(xlsxPath) && File.Exists(xlsxPath))
            Copy(xlsxPath, stage, log);

        log?.Invoke($"已归集: xml {res.Xml} / csv {res.Csv} / tdms {res.Tdms}" +
                    (res.Failed > 0 ? $"  (失败 {res.Failed})" : ""));

        var zip = Path.Combine(outDir, tag + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        log?.Invoke("正在压缩...");
        ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, false);
        res.ZipPath = zip;
        res.ZipBytes = new FileInfo(zip).Length;

        if (!keepStage)
        {
            try { Directory.Delete(stage, true); }
            catch (Exception e) { log?.Invoke($"[提示] 中间目录未能删除: {e.GetType().Name}"); }
        }
        return res;
    }

    private static bool Copy(string src, string dstDir, Action<string>? log)
    {
        try
        {
            var name = Path.GetFileName(src);
            var dst = Path.Combine(dstDir, name);
            if (File.Exists(dst))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                for (int i = 2; ; i++)
                {
                    dst = Path.Combine(dstDir, $"{stem}_{i}{ext}");
                    if (!File.Exists(dst)) break;
                }
            }
            File.Copy(src, dst);
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke($"  [复制失败] {Path.GetFileName(src)}: {e.GetType().Name}");
            return false;
        }
    }

    public static string HumanSize(long b)
        => b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB"
         : b >= 1L << 20 ? $"{b / (double)(1L << 20):F1} MB"
         : b >= 1L << 10 ? $"{b / (double)(1L << 10):F0} KB"
         : $"{b} B";
}
