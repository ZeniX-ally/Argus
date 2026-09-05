using System.Text;
using System.Text.Json;

namespace FctAggregator;

public sealed class MeshQueryService
{
    public sealed class QueryRequest
    {
        public string? Machine { get; set; }
        public string? Sn { get; set; }
        public string? Model { get; set; }
        public string? Result { get; set; }
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; } = 0;
    }

    public sealed class QueryItem
    {
        public string Machine { get; set; } = "";
        public long Id { get; set; }
        public string Sn { get; set; } = "";
        public string Model { get; set; } = "";
        public string Result { get; set; } = "";
        public string TestDate { get; set; } = "";
        public string FailReason { get; set; } = "";
        public string XmlPath { get; set; } = "";
        public long FileSize { get; set; }
        public string BatchTimestamp { get; set; } = "";
        public string Tester { get; set; } = "";
    }

    public sealed class QueryResponse
    {
        public bool Ok { get; set; } = true;
        public bool Cached { get; set; }
        public int Total { get; set; }
        public List<QueryItem> Results { get; set; } = new();
        public List<PeerHit> Peers { get; set; } = new();
    }

    public sealed class PeerHit
    {
        public string Machine { get; set; } = "";
        public bool Online { get; set; }
        public int Count { get; set; }
        public string? Error { get; set; }
    }

    private static readonly LRUCache<string, string> _cache =
        new(256, TimeSpan.FromSeconds(CacheTtlSec));

    public const int CacheTtlSec = 300;

    public static string CacheKey(QueryRequest req)
    {
        var m = (req.Machine ?? "").Trim().ToLowerInvariant();
        var sn = (req.Sn ?? "").Trim().ToLowerInvariant();
        var model = (req.Model ?? "").Trim().ToLowerInvariant();
        var result = (req.Result ?? "").Trim().ToUpperInvariant();
        var df = NormalizeDate(req.DateFrom);
        var dt = NormalizeDate(req.DateTo);
        var lim = Math.Clamp(req.Limit <= 0 ? 100 : req.Limit, 1, 2000);
        var off = Math.Max(0, req.Offset);
        return $"{m}|{sn}|{model}|{result}|{df}|{dt}|{lim}|{off}";
    }

    private static string NormalizeDate(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return "";
        var s = d.Trim().Replace("-", "").Replace("/", "");
        if (s.Length >= 8) return s.Substring(0, 8);
        return s;
    }

    public static bool TryGetCached(string key, out string json) => _cache.TryGet(key, out json!);

    public static void PutCached(string key, string json) => _cache.Set(key, json);

    public static void ClearCache() => _cache.Clear();

    public static int CacheCount => _cache.Count;

    public static List<QueryItem> QueryLocal(Database db, string localMachine, QueryRequest req)
    {
        return db.QueryTestRecords(localMachine, req);
    }

    public static QueryRequest ParseRequest(string json)
    {
        var req = new QueryRequest();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            req.Machine = GetStr(root, "machine");
            req.Sn = GetStr(root, "sn");
            req.Model = GetStr(root, "model");
            req.Result = GetStr(root, "result");
            req.DateFrom = GetStr(root, "date_from") ?? GetStr(root, "dateFrom") ?? GetStr(root, "from");
            req.DateTo = GetStr(root, "date_to") ?? GetStr(root, "dateTo") ?? GetStr(root, "to");
            if (root.TryGetProperty("limit", out var pl) && pl.TryGetInt32(out var lim)) req.Limit = lim;
            if (root.TryGetProperty("offset", out var po) && po.TryGetInt32(out var off)) req.Offset = off;
        }
        catch { }
        return req;
    }

    private static string? GetStr(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.ToString();
            }
        }
        return null;
    }
}
