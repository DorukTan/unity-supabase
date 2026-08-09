using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public sealed class AuthIdentity
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("identity_id")] public string IdentityId { get; internal set; }
        [JsonProperty("user_id")] public string UserId { get; internal set; }
        [JsonProperty("provider")] public string Provider { get; internal set; }
        [JsonProperty("identity_data")] public JObject IdentityData { get; internal set; }
        [JsonProperty("created_at")] public DateTimeOffset? CreatedAt { get; internal set; }
        [JsonProperty("updated_at")] public DateTimeOffset? UpdatedAt { get; internal set; }
        [JsonProperty("last_sign_in_at")] public DateTimeOffset? LastSignInAt { get; internal set; }
    }

    public sealed class AuthMfaFactor
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("friendly_name")] public string FriendlyName { get; internal set; }
        [JsonProperty("factor_type")] public string FactorType { get; internal set; }
        [JsonProperty("status")] public string Status { get; internal set; }
        [JsonProperty("phone")] public string Phone { get; internal set; }
        [JsonProperty("created_at")] public DateTimeOffset? CreatedAt { get; internal set; }
        [JsonProperty("updated_at")] public DateTimeOffset? UpdatedAt { get; internal set; }
    }

    public sealed class AuthUser
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("aud")] public string Audience { get; internal set; }
        [JsonProperty("role")] public string Role { get; internal set; }
        [JsonProperty("email")] public string Email { get; internal set; }
        [JsonProperty("phone")] public string Phone { get; internal set; }
        [JsonProperty("is_anonymous")] public bool IsAnonymous { get; internal set; }
        [JsonProperty("email_confirmed_at")] public DateTimeOffset? EmailConfirmedAt { get; internal set; }
        [JsonProperty("phone_confirmed_at")] public DateTimeOffset? PhoneConfirmedAt { get; internal set; }
        [JsonProperty("last_sign_in_at")] public DateTimeOffset? LastSignInAt { get; internal set; }
        [JsonProperty("created_at")] public DateTimeOffset? CreatedAt { get; internal set; }
        [JsonProperty("updated_at")] public DateTimeOffset? UpdatedAt { get; internal set; }
        [JsonProperty("app_metadata")] public JObject AppMetadata { get; internal set; }
        [JsonProperty("user_metadata")] public JObject UserMetadata { get; internal set; }
        [JsonProperty("identities")] public List<AuthIdentity> Identities { get; internal set; }
        [JsonProperty("factors")] public List<AuthMfaFactor> Factors { get; internal set; }
    }

    public sealed class AuthSession
    {
        [JsonProperty("access_token")] public string AccessToken { get; internal set; }
        [JsonProperty("token_type")] public string TokenType { get; internal set; }
        [JsonProperty("expires_in")] public long ExpiresIn { get; internal set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; internal set; }
        [JsonProperty("refresh_token")] public string RefreshToken { get; internal set; }
        [JsonProperty("provider_token")] public string ProviderToken { get; internal set; }
        [JsonProperty("provider_refresh_token")] public string ProviderRefreshToken { get; internal set; }
        [JsonProperty("user")] public AuthUser User { get; internal set; }

        public bool IsExpired(TimeSpan margin)
        {
            if (ExpiresAt <= 0)
                return true;
            return DateTimeOffset.UtcNow.Add(margin).ToUnixTimeSeconds() >= ExpiresAt;
        }

        internal void NormalizeExpiry()
        {
            if (ExpiresAt <= 0 && ExpiresIn > 0)
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ExpiresIn;
        }
    }

    public sealed class AuthResponse
    {
        [JsonProperty("access_token")] public string AccessToken { get; internal set; }
        [JsonProperty("token_type")] public string TokenType { get; internal set; }
        [JsonProperty("expires_in")] public long ExpiresIn { get; internal set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; internal set; }
        [JsonProperty("refresh_token")] public string RefreshToken { get; internal set; }
        [JsonProperty("provider_token")] public string ProviderToken { get; internal set; }
        [JsonProperty("provider_refresh_token")] public string ProviderRefreshToken { get; internal set; }
        [JsonProperty("user")] public AuthUser User { get; internal set; }
        [JsonProperty("session")] public AuthSession Session { get; internal set; }

        internal AuthSession GetSession()
        {
            if (Session != null)
            {
                Session.NormalizeExpiry();
                return Session;
            }
            if (string.IsNullOrWhiteSpace(AccessToken))
                return null;
            var session = new AuthSession
            {
                AccessToken = AccessToken,
                TokenType = TokenType,
                ExpiresIn = ExpiresIn,
                ExpiresAt = ExpiresAt,
                RefreshToken = RefreshToken,
                ProviderToken = ProviderToken,
                ProviderRefreshToken = ProviderRefreshToken,
                User = User
            };
            session.NormalizeExpiry();
            return session;
        }
    }

    public sealed class AuthMfaEnrollment
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("type")] public string Type { get; internal set; }
        [JsonProperty("totp")] public AuthTotpEnrollment Totp { get; internal set; }
        [JsonProperty("phone")] public string Phone { get; internal set; }
    }

    public sealed class AuthTotpEnrollment
    {
        [JsonProperty("qr_code")] public string QrCode { get; internal set; }
        [JsonProperty("secret")] public string Secret { get; internal set; }
        [JsonProperty("uri")] public string Uri { get; internal set; }
    }

    public sealed class AuthMfaChallenge
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; internal set; }
    }

    public sealed class AuthSsoResponse
    {
        [JsonProperty("url")] public string Url { get; internal set; }
    }

    public sealed class AuthAssuranceLevel
    {
        public string CurrentLevel { get; internal set; }
        public string NextLevel { get; internal set; }
        public IReadOnlyList<AuthAuthenticationMethod> CurrentAuthenticationMethods { get; internal set; }
    }

    public sealed class AuthAuthenticationMethod
    {
        [JsonProperty("method")] public string Method { get; internal set; }
        [JsonProperty("timestamp")] public long Timestamp { get; internal set; }
    }
}
