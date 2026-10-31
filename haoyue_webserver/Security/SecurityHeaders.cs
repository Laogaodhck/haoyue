namespace haoyue_webserver.Security;

/// <summary>
/// 全站安全响应头。Blazor Server 依赖同源 WebSocket 与内联主题脚本，
/// 因此脚本/样式源保留 'unsafe-inline'，其余一律收紧到本站资源。
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' ws: wss:";

    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Blazor 组件渲染时会给 HTML 追加 frame-ancestors 'self'，登记顺序保证
            // 本回调最后执行（OnStarting 逆序），用单一 CSP 覆盖掉框架默认值。
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                return Task.CompletedTask;
            });
            await next();
        });
}
