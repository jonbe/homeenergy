using System.Collections.Concurrent;
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

    /// <summary>Below this the startup log warns; update.sh refuses to expose it.</summary>
    public const int MinPasswordLength = 12;

    // Brute-force brake, two layers. Every wrong guess waits before answering,
    // which slows a single sequential guesser. And each client address gets
    // MaxFailures wrong guesses per FailureWindow; after that every attempt from
    // it is refused outright until the window passes, which also stops a
    // parallel guesser. Addresses are what the proxy reports (X-Forwarded-For).
    private static readonly TimeSpan DefaultWrongPasswordDelay = TimeSpan.FromMilliseconds(750);
    public const int MaxFailures = 10;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private readonly record struct Strikes(int Count, DateTimeOffset First);
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new();

    private readonly string _password;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _wrongPasswordDelay;

    public LoginGate(string password, IDataProtectionProvider protection, TimeProvider clock,
        TimeSpan? wrongPasswordDelay = null)
    {
        _password = password;
        _protector = protection.CreateProtector("HomeEnergy.Session");
        _clock = clock;
        _wrongPasswordDelay = wrongPasswordDelay ?? DefaultWrongPasswordDelay;
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
        string client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = _clock.GetUtcNow();

        if (LockedOut(client, now) is { } remaining)
        {
            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
            await Reject(ctx, $"Too many attempts — try again in {Math.Ceiling(remaining.TotalMinutes):0} min",
                next, StatusCodes.Status429TooManyRequests);
            return;
        }

        if (!Matches(attempt, _password))
        {
            RecordFailure(client, now);
            await Task.Delay(_wrongPasswordDelay, ctx.RequestAborted);
            await Reject(ctx, error: "Wrong password", next);
            return;
        }

        _strikes.TryRemove(client, out _);
        var expires = now + SessionLifetime;
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

    // Time left on this client's lockout, or null if it may try.
    private TimeSpan? LockedOut(string client, DateTimeOffset now)
    {
        if (!_strikes.TryGetValue(client, out var s)) return null;
        var until = s.First + FailureWindow;
        if (until <= now)
        {
            _strikes.TryRemove(client, out _);   // window over, clean slate
            return null;
        }
        return s.Count >= MaxFailures ? until - now : null;
    }

    private void RecordFailure(string client, DateTimeOffset now)
    {
        _strikes.AddOrUpdate(client, _ => new Strikes(1, now),
            (_, s) => s.First + FailureWindow <= now ? new Strikes(1, now) : s with { Count = s.Count + 1 });

        // Keep the table from growing with one-off visitors: drop expired entries.
        if (_strikes.Count > 1000)
            foreach (var (key, s) in _strikes)
                if (s.First + FailureWindow <= now) _strikes.TryRemove(key, out _);
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

    private static async Task Reject(HttpContext ctx, string? error, string? next = null,
        int status = StatusCodes.Status401Unauthorized)
    {
        ctx.Response.StatusCode = status;
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
