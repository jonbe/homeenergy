using System.Text;
using HomeEnergy.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace HomeEnergy.Tests;

public class LoginGateTests
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // One key ring shared by every gate in a test, so a cookie issued by one
    // request is readable by the next.
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

    // No wrong-password delay in tests; the lockout is what's under test, not the sleep.
    private LoginGate Gate(DateTimeOffset? now = null) => Gate(new FixedClock(now ?? T0));
    private LoginGate Gate(FixedClock clock) => new(Password, _keys, clock, wrongPasswordDelay: TimeSpan.Zero);

    private static DefaultHttpContext Request(string method, string path, string? accept = "text/html",
        string ip = "10.0.0.1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        if (accept is not null) ctx.Request.Headers.Accept = accept;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static DefaultHttpContext LoginPost(string password, string next = "/", string ip = "10.0.0.1")
    {
        var ctx = Request("POST", "/login", ip: ip);
        var body = Encoding.UTF8.GetBytes($"password={Uri.EscapeDataString(password)}&next={Uri.EscapeDataString(next)}");
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Body = new MemoryStream(body);
        ctx.Request.ContentLength = body.Length;
        return ctx;
    }

    // The session cookie value from a Set-Cookie header, or null if none was issued.
    private static string? IssuedCookie(HttpContext ctx) =>
        ctx.Response.Headers.SetCookie
            .Select(h => h!.Split(';')[0])
            .Where(kv => kv.StartsWith(LoginGate.CookieName + "="))
            .Select(kv => kv[(LoginGate.CookieName.Length + 1)..])
            .FirstOrDefault();

    private static string Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    private static async Task<bool> PassesGate(LoginGate gate, HttpContext ctx)
    {
        bool reached = false;
        await gate.InvokeAsync(ctx, () => { reached = true; return Task.CompletedTask; });
        return reached;
    }

    [Fact]
    public async Task Browser_WithoutSession_GetsLoginForm()
    {
        var ctx = Request("GET", "/");

        Assert.False(await PassesGate(Gate(), ctx));
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Contains("method=post action=/login", Body(ctx));
        Assert.Contains("type=password", Body(ctx));
    }

    [Fact]
    public async Task ApiCall_WithoutSession_GetsJson401()
    {
        var ctx = Request("GET", "/api/status", accept: "application/json");

        Assert.False(await PassesGate(Gate(), ctx));
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Contains("unauthorized", Body(ctx));
    }

    [Fact]
    public async Task WrongPassword_IsRejected_AndIssuesNoCookie()
    {
        var ctx = LoginPost("nope");

        await Gate().InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Null(IssuedCookie(ctx));
        Assert.Contains("Wrong password", Body(ctx));
    }

    [Fact]
    public async Task CorrectPassword_IssuesAnEncryptedSession_ThatThenPasses()
    {
        var login = LoginPost(Password, next: "/?day=-1");
        await Gate().InvokeAsync(login, () => Task.CompletedTask);

        Assert.Equal(302, login.Response.StatusCode);
        Assert.Equal("/?day=-1", login.Response.Headers.Location);
        var cookie = IssuedCookie(login);
        Assert.NotNull(cookie);
        Assert.DoesNotContain(Password, cookie);                 // never the password itself
        Assert.DoesNotContain(Uri.EscapeDataString(Password), cookie);
        Assert.Contains("httponly", login.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);

        var later = Request("GET", "/api/status", accept: "application/json");
        later.Request.Headers.Cookie = $"{LoginGate.CookieName}={cookie}";
        Assert.True(await PassesGate(Gate(), later));
    }

    [Fact]
    public async Task TamperedCookie_IsRejected()
    {
        var login = LoginPost(Password);
        await Gate().InvokeAsync(login, () => Task.CompletedTask);
        var cookie = IssuedCookie(login)!;

        var ctx = Request("GET", "/");
        ctx.Request.Headers.Cookie = $"{LoginGate.CookieName}={cookie[..^4]}AAAA";
        Assert.False(await PassesGate(Gate(), ctx));
    }

    [Fact]
    public async Task Session_ExpiresAfterItsLifetime()
    {
        var login = LoginPost(Password);
        await Gate().InvokeAsync(login, () => Task.CompletedTask);
        var cookie = IssuedCookie(login)!;

        var stillValid = Request("GET", "/");
        stillValid.Request.Headers.Cookie = $"{LoginGate.CookieName}={cookie}";
        Assert.True(await PassesGate(Gate(T0 + LoginGate.SessionLifetime - TimeSpan.FromMinutes(1)), stillValid));

        var expired = Request("GET", "/");
        expired.Request.Headers.Cookie = $"{LoginGate.CookieName}={cookie}";
        Assert.False(await PassesGate(Gate(T0 + LoginGate.SessionLifetime + TimeSpan.FromMinutes(1)), expired));
    }

    [Fact]
    public async Task OffsiteRedirectTarget_IsIgnored()
    {
        var login = LoginPost(Password, next: "//evil.example/phish");
        await Gate().InvokeAsync(login, () => Task.CompletedTask);

        Assert.Equal("/", login.Response.Headers.Location);
    }

    [Fact]
    public async Task ApiKeyHeader_StillWorksForScripts()
    {
        var ctx = Request("GET", "/api/status", accept: "application/json");
        ctx.Request.Headers["X-Api-Key"] = Password;

        Assert.True(await PassesGate(Gate(), ctx));
    }

    [Fact]
    public async Task TibberCallback_IsExempt()
    {
        Assert.True(await PassesGate(Gate(), Request("GET", "/tibber/callback", accept: null)));
    }

    [Fact]
    public async Task TooManyWrongGuesses_LockTheAddressOut_EvenForTheRightPassword()
    {
        var gate = Gate();
        for (int i = 0; i < LoginGate.MaxFailures; i++)
            await gate.InvokeAsync(LoginPost("nope"), () => Task.CompletedTask);

        var ctx = LoginPost(Password);
        await gate.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Null(IssuedCookie(ctx));
        Assert.Contains("Too many attempts", Body(ctx));
        Assert.True(int.Parse(ctx.Response.Headers.RetryAfter!) > 0);
    }

    [Fact]
    public async Task Lockout_IsPerAddress_AndEndsWithTheWindow()
    {
        var clock = new FixedClock(T0);
        var gate = Gate(clock);
        for (int i = 0; i < LoginGate.MaxFailures; i++)
            await gate.InvokeAsync(LoginPost("nope", ip: "10.0.0.1"), () => Task.CompletedTask);

        // Someone else is unaffected.
        var other = LoginPost(Password, ip: "10.0.0.2");
        await gate.InvokeAsync(other, () => Task.CompletedTask);
        Assert.Equal(302, other.Response.StatusCode);

        // The locked address gets back in once the window has passed.
        clock.Now = T0 + LoginGate.FailureWindow + TimeSpan.FromSeconds(1);
        var retry = LoginPost(Password, ip: "10.0.0.1");
        await gate.InvokeAsync(retry, () => Task.CompletedTask);
        Assert.Equal(302, retry.Response.StatusCode);
        Assert.NotNull(IssuedCookie(retry));
    }

    [Fact]
    public async Task SuccessfulLogin_ClearsEarlierStrikes()
    {
        var gate = Gate();
        for (int i = 0; i < LoginGate.MaxFailures - 1; i++)
            await gate.InvokeAsync(LoginPost("nope"), () => Task.CompletedTask);
        await gate.InvokeAsync(LoginPost(Password), () => Task.CompletedTask);

        // Fresh slate: another MaxFailures-1 wrong guesses must not lock out.
        for (int i = 0; i < LoginGate.MaxFailures - 1; i++)
            await gate.InvokeAsync(LoginPost("nope"), () => Task.CompletedTask);
        var ctx = LoginPost(Password);
        await gate.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(302, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Logout_ClearsTheSession()
    {
        var ctx = Request("POST", "/logout");

        Assert.False(await PassesGate(Gate(), ctx));
        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Contains(LoginGate.CookieName + "=;", ctx.Response.Headers.SetCookie.ToString());
    }
}
