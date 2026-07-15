using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ITibberVehicleClient
{
    Task<TibberSnapshot?> FetchAsync(CancellationToken ct);

    /// <summary>True once a refresh token is held (i.e. the user has consented
    /// and the connection has not since been rejected).</summary>
    bool IsConnected { get; }

    /// <summary>Builds the OAuth authorize URL (with PKCE) to send the user's
    /// browser to. <paramref name="requestOrigin"/> is the scheme://host[:port]
    /// of the incoming request; the callback path is appended to form the
    /// redirect URI. Returns null if the client id is not configured.</summary>
    string? BuildAuthorizeUrl(string requestOrigin);

    /// <summary>Exchanges the authorization code returned to the callback for
    /// tokens. Returns false on a bad/unknown state or a failed exchange.</summary>
    Task<bool> HandleCallbackAsync(string code, string state, CancellationToken ct);
}
