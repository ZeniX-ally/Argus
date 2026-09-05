using System.Net;

namespace FctAggregator;

public delegate Task RouteHandler(HttpListenerContext ctx);

[Flags]
public enum RouteFlags
{
    None = 0,
    Audit = 1,
    RoleAdmin = 2,
    RoleViewer = 4,
    Anonymous = 8,
}

public sealed class HttpRoute
{
    public string Method { get; }

    public string Path { get; }

    public RouteHandler Handler { get; }

    public RouteFlags Flags { get; }

    public HttpRoute(string method, string path, RouteHandler handler, RouteFlags flags = RouteFlags.None)
    {
        Method = method;
        Path = path;
        Handler = handler;
        Flags = flags;
    }
}

public sealed class RoutePipeline
{
    private readonly List<HttpRoute> _exact = new();

    private readonly List<HttpRoute> _wildcard = new();

    public void Add(string method, string path, RouteHandler handler, RouteFlags flags = RouteFlags.None)
    {
        var m = method.ToUpperInvariant();
        if (path.EndsWith("/*", StringComparison.Ordinal))
            _wildcard.Add(new HttpRoute(m, path, handler, flags));
        else
            _exact.Add(new HttpRoute(m, path, handler, flags));
    }

    public void Handle(HttpListenerContext ctx)
    {
        var raw = ctx.Request.Url?.AbsolutePath ?? "";
        var path = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        if (path.Length == 0) path = "/";
        var method = ctx.Request.HttpMethod.ToUpperInvariant();

        var pathMatched = false;

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

    private static bool WildcardMatch(string routePath, string path)
    {
        var prefix = routePath[..^2];
        int n = prefix.Length;
        if (path.Length < n) return false;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == n || path[n] == '/';
    }
}