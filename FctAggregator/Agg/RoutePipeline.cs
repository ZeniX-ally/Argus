using System.Net;

namespace FctAggregator;

/// <summary>HTTP 路由处理器委托：接收 HttpListenerContext 并完成响应。
/// 现有 ServeXxx 均为同步 void 方法，注册时包装返回 <see cref="Task.CompletedTask"/>；
/// 处理器内同步抛出的异常会同步上抛，由外层 Handle 的 try/catch 兜底回 500。</summary>
public delegate Task RouteHandler(HttpListenerContext ctx);

/// <summary>路由标志位（P1 预留，本次仅登记不使用）：后续用于审计/角色鉴权/匿名放行。</summary>
[Flags]
public enum RouteFlags
{
    None = 0,
    Audit = 1,        // 审计（预留）
    RoleAdmin = 2,    // 管理员角色（预留）
    RoleViewer = 4,   // 查看者角色（预留）
    Anonymous = 8,    // 匿名放行（预留）
}

/// <summary>单条路由：方法（GET/POST/*）+ 路径（精确或尾部 * 通配）+ 处理器 + 标志。</summary>
public sealed class HttpRoute
{
    /// <summary>HTTP 方法（大写）：GET/POST，或 * 表示任意方法。</summary>
    public string Method { get; }

    /// <summary>路由路径：精确（如 "/api/health"）或以 "/*" 结尾的尾部通配（如 "/public/*"）。</summary>
    public string Path { get; }

    /// <summary>命中处理器。</summary>
    public RouteHandler Handler { get; }

    /// <summary>路由标志（P1 预留，本次仅登记不使用）。</summary>
    public RouteFlags Flags { get; }

    public HttpRoute(string method, string path, RouteHandler handler, RouteFlags flags = RouteFlags.None)
    {
        Method = method;
        Path = path;
        Handler = handler;
        Flags = flags;
    }
}

/// <summary>
/// 极简 HTTP 路由管线（WebAggServer 路由分发重构）。
/// 把 WebAggServer.Handle() 里原先的大 switch 拆成「先注册、后分发」：
/// 归一化 path（去尾斜杠、空变 "/"）→ 先精确后通配匹配 → 未命中 404、方法不符 405 → 命中调 handler。
/// 404/405 语义与旧 switch 完全一致；新增端点只需一次 <see cref="Add"/>。
/// </summary>
public sealed class RoutePipeline
{
    /// <summary>精确路由表（大小写敏感，语义等同原 C# switch 的 ordinal 比较）。</summary>
    private readonly List<HttpRoute> _exact = new();

    /// <summary>尾部通配路由表（如 "/public/*"；原 public 分支是 IgnoreCase 判断，这里同样 IgnoreCase 保持行为一致）。</summary>
    private readonly List<HttpRoute> _wildcard = new();

    /// <summary>注册路由：method=GET/POST/*（大小写不敏感），path 精确，或尾部 "/*" 表示通配。</summary>
    public void Add(string method, string path, RouteHandler handler, RouteFlags flags = RouteFlags.None)
    {
        var m = method.ToUpperInvariant();
        if (path.EndsWith("/*", StringComparison.Ordinal))
            _wildcard.Add(new HttpRoute(m, path, handler, flags));
        else
            _exact.Add(new HttpRoute(m, path, handler, flags));
    }

    /// <summary>分发单个请求：归一化 path → 先精确后通配 → 命中调 handler，未命中 404，方法不符 405。</summary>
    public void Handle(HttpListenerContext ctx)
    {
        // 归一化 path（复用 WebAggServer 旧 Handle 规则：去掉尾斜杠（/ 除外）、空变 "/"）
        var raw = ctx.Request.Url?.AbsolutePath ?? "";
        var path = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        if (path.Length == 0) path = "/";
        var method = ctx.Request.HttpMethod.ToUpperInvariant();

        var pathMatched = false;   // 有路径命中但方法不符 → 405；完全未命中 → 404

        // 先精确匹配（存在路径冲突时以先注册者优先）
        foreach (var r in _exact)
        {
            if (!string.Equals(r.Path, path, StringComparison.Ordinal)) continue;
            pathMatched = true;
            if (r.Method == "*" || r.Method == method)
            {
                _ = r.Handler(ctx);
                return;
            }
        }

        // 后通配匹配（/public/* 这类；原 public 分支用 IgnoreCase，这里保持一致）
        foreach (var r in _wildcard)
        {
            if (!WildcardMatch(r.Path, path)) continue;
            pathMatched = true;
            if (r.Method == "*" || r.Method == method)
            {
                _ = r.Handler(ctx);
                return;
            }
        }

        ctx.Response.StatusCode = pathMatched ? 405 : 404;
    }

    /// <summary>「尾部 *」通配匹配："/public/*" 命中 "/public" 与 "/public/..."（含子路径），大小写不敏感。</summary>
    private static bool WildcardMatch(string routePath, string path)
    {
        var prefix = routePath[..^2];   // 去掉尾部 "/*"
        int n = prefix.Length;
        if (path.Length < n) return false;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == n || path[n] == '/';
    }
}