using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public interface IAuthClient : IDisposable
    {
        AuthSession CurrentSession { get; }
        AuthUser CurrentUser { get; }
        event EventHandler<AuthStateChangedEventArgs> StateChanged;

        Task<SupabaseResult<AuthSession>> InitializeAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthResponse>> SignUpWithPasswordAsync(string email, string password, AuthSignUpOptions options = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> SignInWithPasswordAsync(string emailOrPhone, string password, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> SignInAnonymouslyAsync(JObject data = null, string captchaToken = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult> SignInWithOtpAsync(string emailOrPhone, AuthOtpOptions options = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> VerifyOtpAsync(string token, AuthOtpType type, string emailOrPhone = null, string redirectTo = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> SignInWithIdTokenAsync(AuthIdTokenOptions options, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<Uri>> SignInWithOAuthAsync(string provider, AuthOAuthOptions options = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> ExchangeCodeForSessionAsync(string code, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> HandleAuthCallbackAsync(Uri callback, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSsoResponse>> SignInWithSsoAsync(AuthSsoOptions options, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> RefreshSessionAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> SetSessionAsync(string accessToken, string refreshToken, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthUser>> GetUserAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthUser>> UpdateUserAsync(JObject attributes, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult> ResetPasswordForEmailAsync(string email, string redirectTo = null, string captchaToken = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult> ReauthenticateAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult> SignOutAsync(AuthSignOutScope scope = AuthSignOutScope.Global, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<Uri>> LinkIdentityAsync(string provider, AuthOAuthOptions options = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthUser>> UnlinkIdentityAsync(string identityId, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<IReadOnlyList<AuthIdentity>>> ListIdentitiesAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthMfaEnrollment>> EnrollMfaAsync(AuthMfaEnrollOptions options, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthMfaChallenge>> ChallengeMfaAsync(string factorId, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> VerifyMfaAsync(string factorId, string challengeId, string code, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<AuthSession>> ChallengeAndVerifyMfaAsync(string factorId, string code, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult> UnenrollMfaAsync(string factorId, CancellationToken cancellationToken = default(CancellationToken));
        Task<SupabaseResult<IReadOnlyList<AuthMfaFactor>>> ListMfaFactorsAsync(CancellationToken cancellationToken = default(CancellationToken));
        SupabaseResult<AuthAssuranceLevel> GetAuthenticatorAssuranceLevel();
    }
}
