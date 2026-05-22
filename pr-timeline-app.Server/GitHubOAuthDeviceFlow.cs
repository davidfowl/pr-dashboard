using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

sealed class GitHubOAuthDeviceFlow(HttpClient httpClient, GitHubTokenProvider tokenProvider)
{
    private const string Scope = "repo read:org";
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private DeviceLoginState? current;

    public static string? ClientId => Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public async Task<DeviceLoginResponse> StartAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (current is { ExpiresAt: var expiresAt } && expiresAt > DateTimeOffset.UtcNow)
            {
                return current.ToResponse("pending", "Enter this code at GitHub to finish signing in.");
            }

            var clientId = ClientId ?? throw new InvalidOperationException("GitHub login is not configured.");
            using var response = await httpClient.PostAsync(
                "login/device/code",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scope"] = Scope
                }),
                cancellationToken);
            var result = await ReadOAuthJsonAsync(
                response,
                GitHubJsonSerializerContext.Default.DeviceCodeResponseDto,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                return new DeviceLoginResponse(
                    Status: "error",
                    UserCode: null,
                    VerificationUri: null,
                    VerificationUriComplete: null,
                    IntervalSeconds: 5,
                    ExpiresAt: null,
                    Message: OAuthErrorMessage(result.Error, result.ErrorDescription));
            }

            current = new DeviceLoginState(
                DeviceCode: result.DeviceCode ?? "",
                UserCode: result.UserCode ?? "",
                VerificationUri: result.VerificationUri ?? "https://github.com/login/device",
                VerificationUriComplete: result.VerificationUriComplete,
                IntervalSeconds: result.Interval == 0 ? 5 : result.Interval,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn));

            return current.ToResponse("pending", "Enter this code at GitHub to finish signing in.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<DeviceLoginResponse> PollAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (current is null)
            {
                return new DeviceLoginResponse("idle", null, null, null, 5, null, "Start GitHub login first.");
            }

            if (current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                current = null;
                return new DeviceLoginResponse("expired", null, null, null, 5, null, "The GitHub login code expired.");
            }

            if (current.NextPollAt > DateTimeOffset.UtcNow)
            {
                return current.ToResponse("pending", "Waiting for GitHub authorization.");
            }

            current = current with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(current.IntervalSeconds) };
            var clientId = ClientId ?? throw new InvalidOperationException("GitHub login is not configured.");
            using var response = await httpClient.PostAsync(
                "login/oauth/access_token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = current.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }),
                cancellationToken);
            var result = await ReadOAuthJsonAsync(
                response,
                GitHubJsonSerializerContext.Default.OAuthTokenResponseDto,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.AccessToken))
            {
                tokenProvider.SetOAuthToken(result.AccessToken);
                current = null;
                return new DeviceLoginResponse("authorized", null, null, null, 5, null, "Signed in with GitHub.");
            }

            var error = result.Error ?? "authorization_pending";
            if (error == "slow_down")
            {
                current = current with { IntervalSeconds = current.IntervalSeconds + 5 };
            }

            if (error is "expired_token" or "access_denied")
            {
                current = null;
            }

            var status = error switch
            {
                "authorization_pending" => "pending",
                "expired_token" => "expired",
                _ => error ?? "pending"
            };
            return current?.ToResponse(status, OAuthErrorMessage(error, result.ErrorDescription))
                ?? new DeviceLoginResponse(status, null, null, null, 5, null, OAuthErrorMessage(error, result.ErrorDescription));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<T> ReadOAuthJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken)
            ?? throw new InvalidOperationException("GitHub OAuth returned an empty response.");
    }

    private static string OAuthErrorMessage(string? error, string? errorDescription) =>
        error switch
        {
            "authorization_pending" => "Waiting for GitHub authorization.",
            "slow_down" => "GitHub asked us to poll more slowly.",
            "expired_token" => "The GitHub login code expired.",
            "access_denied" => "GitHub login was canceled.",
            "device_flow_disabled" => "Enable Device Flow on the GitHub OAuth App used by GITHUB_CLIENT_ID.",
            _ => errorDescription ?? "GitHub login failed."
        };

    private sealed record DeviceLoginState(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        int IntervalSeconds,
        DateTimeOffset ExpiresAt)
    {
        public DateTimeOffset NextPollAt { get; init; } = DateTimeOffset.MinValue;

        public DeviceLoginResponse ToResponse(string status, string message) =>
            new(
                Status: status,
                UserCode: UserCode,
                VerificationUri: VerificationUri,
                VerificationUriComplete: VerificationUriComplete,
                IntervalSeconds: IntervalSeconds,
                ExpiresAt: ExpiresAt,
                Message: message);
    }
}
