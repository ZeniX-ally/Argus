using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 源机检索服务（P4 D5）：POST /api/mesh/query 并发查各机台 test_records 全量（含 PASS），结果聚合缓存 5 分钟，聚合端零膨胀。
/// 架构：聚合端收到查询 → 本机 Database.QueryTestRecords + 并发 POST 到各在线机台的 /api/mesh/query/local
/// （各机台只查本地 test_records，有 sn/test_date 索引，3000 行内 <100ms）。结果汇总后内存缓存 5min，
/// 缓存键 = 查询条件规范化 JSON；命中缓存直接返回，不再碰各机台。
/// 安全：沿用 IsAuthenticated（agg_token/users），LocalReadValidator 仍守护本机文件读取；本服务只做查询与 XML 热点缓存，不复制 PASS 索引。
/// </summary>
public sealed class MeshQueryService
{
    /// <summary>查询请求（全部字段可选，大小写不敏感）。</summary>
    public sealed class QueryRequest
    {
        public string? Machine { get; set; }
        public string? Sn { get; set; }
        public string? Model { get; set; }
        public string? Result { get; set; }   // PASS / FAIL / null=全部
        public string? DateFrom { get; set; } // yyyyMMdd 或 yyyy-MM-dd
        public string? DateTo { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; } = 0;
    }

    /// <summary>单条命中（来自任意机台的 test_records）。</summary>
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

    /// <summary>聚合响应。</summary>
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

    // ===== 缓存 5 分钟 =====
    // v3.22.0：LRUCache 接线（Infra/LRUCache.cs）——容量 + TTL 双淘汰替代手写 ConcurrentDictionary 兜底，
    // 语义不变：键 = CacheKey 归一化串，值 = JSON 原文，TTL 300s，容量 256（超限挤掉最久未用）。
    private static readonly LRUCache<string, string> _cache =
        new(256, TimeSpan.FromSeconds(CacheTtlSec));

    public const int CacheTtlSec = 300; // 5 分钟

    /// <summary>生成缓存键：各字段归一化后拼 JSON（大小写不敏感、空格去重）。</summary>
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

    /// <summary>本地查询：直接读本机 Database test_records（全量含 PASS）。</summary>
    public static List<QueryItem> QueryLocal(Database db, string localMachine, QueryRequest req)
    {
        return db.QueryTestRecords(localMachine, req);
    }

    /// <summary>解析 QueryRequest 的 JSON（容错：大小写不敏感、缺省给默认值）。</summary>
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
