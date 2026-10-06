using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace HomeEnergy.Services;

/// <summary>
/// Password gate in front of the dashboard and every API endpoint. A browser
/// without a session gets a login form; a correct password issues a session
/// cookie that is an encrypted, expiring ticket — the password itself is never
/// stored on the client. Scripts may instead send the password in an
/// <c>X-Api-Key</c> header. The Tibber OAuth callback is exempt since Tibber's
/// redirect can't carry our credentials (its unguessable state parameter
/// authenticates it instead).
/// </summary>
public sealed class LoginGate
{
    public const string CookieName = "he_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // Ticket format inside the protected cookie. Bump the version to invalidate
    // every outstanding session at once.
    private const string TicketVersion = "1";

    // Wrong guesses wait this long before answering, which caps brute-force rate
    // without needing any per-client state.
    private static readonly TimeSpan WrongPasswordDelay = TimeSpan.FromMilliseconds(750);

    private readonly string _password;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public LoginGate(string password, IDataProtectionProvider protection, TimeProvider clock)
    {
        _password = password;
        _protector = protection.CreateProtector("HomeEnergy.Session");
        _clock = clock;
    }

    public async Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path;

        if (path.StartsWithSegments("/tibber/callback"))
        {
            await next();
            return;
        }

        if (path == "/login" && HttpMethods.IsPost(ctx.Request.Method))
        {
            await HandleLogin(ctx);
            return;
        }

        if (path == "/logout" && HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.Cookies.Delete(CookieName);
            ctx.Response.Redirect("/");
            return;
        }

        if (HeaderMatches(ctx) || HasValidSession(ctx))
        {
            await next();
            return;
        }

        await Reject(ctx, error: null);
    }

    private async Task HandleLogin(HttpContext ctx)
    {
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : null;
        string attempt = form?["password"].ToString() ?? "";
        string next = SafeLocalPath(form?["next"].ToString());

        if (!Matches(attempt, _password))
        {
            await Task.Delay(WrongPasswordDelay, ctx.RequestAborted);
            await Reject(ctx, error: "Wrong password", next);
            return;
        }

        var expires = _clock.GetUtcNow() + SessionLifetime;
        var ticket = _protector.Protect($"{TicketVersion}|{expires.ToUnixTimeSeconds()}");
        ctx.Response.Cookies.Append(CookieName, ticket, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            MaxAge = SessionLifetime,
            Path = "/",
        });
        ctx.Response.Redirect(next);
    }

    private bool HeaderMatches(HttpContext ctx) =>
        ctx.Request.Headers["X-Api-Key"].FirstOrDefault() is { } key && Matches(key, _password);

    private bool HasValidSession(HttpContext ctx)
    {
        if (ctx.Request.Cookies[CookieName] is not { Length: > 0 } cookie) return false;
        try
        {
            var parts = _protector.Unprotect(cookie).Split('|');
            return parts.Length == 2
                && parts[0] == TicketVersion
                && long.TryParse(parts[1], out var exp)
                && DateTimeOffset.FromUnixTimeSeconds(exp) > _clock.GetUtcNow();
        }
        catch (CryptographicException)
        {
            return false;   // tampered, or issued under a key we no longer have
        }
    }

    // Only ever redirect within this site: a relative path, not a scheme or "//host".
    private static string SafeLocalPath(string? candidate) =>
        candidate is { Length: > 0 } p && p[0] == '/' && (p.Length == 1 || (p[1] != '/' && p[1] != '\\'))
            ? p : "/";

    private static async Task Reject(HttpContext ctx, string? error, string? next = null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var wantsHtml = ctx.Request.Headers.Accept.ToString().Contains("text/html");
        if (!wantsHtml || ctx.Request.Path.StartsWithSegments("/api"))
        {
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        next ??= ctx.Request.Path + ctx.Request.QueryString;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(LoginPage(error, SafeLocalPath(next)));
    }

    private static string LoginPage(string? error, string next) =>
        "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'>"
        + "<title>Home Energy — sign in</title>"
        + "<body style='background:#111;color:#eee;font-family:system-ui;display:grid;place-items:center;height:100vh;margin:0'>"
        + "<form method=post action=/login style='display:flex;flex-direction:column;gap:.6rem;align-items:center'>"
        + "<div style='display:flex;gap:.5rem'>"
        + "<input name=password type=password placeholder='Password' autofocus autocomplete=current-password "
        + "style='padding:.5rem;border-radius:6px;border:1px solid #386;background:#222;color:#eee'>"
        + "<button style='padding:.5rem .9rem;border-radius:6px;border:1px solid #386;background:#264;color:#eee;cursor:pointer'>Enter</button>"
        + "</div>"
        + $"<input type=hidden name=next value='{System.Net.WebUtility.HtmlEncode(next)}'>"
        + (error is null ? "" : $"<div style='color:#f77;font-size:.9rem'>{System.Net.WebUtility.HtmlEncode(error)}</div>")
        + "</form>";

    private static bool Matches(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
