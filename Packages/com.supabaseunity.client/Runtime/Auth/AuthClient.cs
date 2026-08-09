using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public sealed class AuthClient : IAuthClient
    {
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
        // Comfortably longer than any real OAuth round trip, short enough that an
        // abandoned verifier does not linger on disk.
        private static readonly TimeSpan PkceLifetime = TimeSpan.FromMinutes(10);
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly ISessionStore sessionStore;
        private readonly ISessionStore pkceStore;
        private readonly IAuthCallbackProvider callbackProvider;
        private readonly string sessionKey;
        private readonly string pkceKey;
        private readonly object refreshGate = new object();
        private CancellationTokenSource refreshLoopCancellation;
        private Task<SupabaseResult<AuthSession>> refreshTask;
        private bool initialized;
        private bool disposed;

        public AuthSession CurrentSession { get; private set; }
        public AuthUser CurrentUser { get { return CurrentSession == null ? null : CurrentSession.User; } }
        public event EventHandler<AuthStateChangedEventArgs> StateChanged;

        internal AuthClient(
            SupabaseClientOptions options,
            Uri endpoint,
            IHttpTransport transport,
            ISessionStore sessionStore,
            ISessionStore pkceStore,
            IAuthCallbackProvider callbackProvider)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.sessionStore = sessionStore;
            this.pkceStore = pkceStore ?? sessionStore;
            this.callbackProvider = callbackProvider;
            sessionKey = "supabase.unity.auth." + StableKey(options.ProjectUrl);
            pkceKey = sessionKey + ".pkce";
            if (callbackProvider != null)
                callbackProvider.CallbackReceived += OnCallbackReceived;
            SupabaseRuntimeHost.FocusChanged += OnFocusChanged;
        }

        public async Task<SupabaseResult<AuthSession>> InitializeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (initialized)
                return SupabaseResult<AuthSession>.Success(CurrentSession);

            try
            {
                var persisted = await sessionStore.GetAsync(sessionKey, cancellationToken);
                if (!string.IsNullOrWhiteSpace(persisted))
                {
                    CurrentSession = SupabaseJson.Deserialize<AuthSession>(persisted);
                    if (CurrentSession != null)
                    {
                        SupabaseKeyValidator.RejectElevatedKey(CurrentSession.AccessToken,
                            "a persisted user session");
                        CurrentSession.NormalizeExpiry();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await SafeRemoveSessionAsync(CancellationToken.None);
                return SupabaseResult<AuthSession>.Failure(SupabaseError.Create(
                    SupabaseService.Auth,
                    SupabaseErrorKind.Serialization,
                    "The persisted Supabase session could not be read.",
                    details: exception.Message));
            }

            initialized = true;
            StartAutoRefresh();

            if (CurrentSession != null && CurrentSession.IsExpired(RefreshMargin))
            {
                var refreshed = await RefreshSessionAsync(cancellationToken);
                if (!refreshed.IsSuccess)
                    return refreshed;
            }

            Notify(AuthChangeEvent.InitialSession);

            if (callbackProvider != null && callbackProvider.InitialCallback != null)
            {
                var callback = callbackProvider.InitialCallback;
                try
                {
                    var callbackResult = await HandleAuthCallbackAsync(callback, cancellationToken);
                    if (!callbackResult.IsSuccess)
                        return callbackResult;
                }
                finally
                {
                    ClearSensitiveCallback(callback);
                }
            }

            return SupabaseResult<AuthSession>.Success(CurrentSession);
        }

        public async Task<SupabaseResult<AuthResponse>> SignUpWithPasswordAsync(
            string email,
            string password,
            AuthSignUpOptions signUpOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(email, "email");
            Require(password, "password");
            signUpOptions = signUpOptions ?? new AuthSignUpOptions();
            var body = new JObject
            {
                ["email"] = email,
                ["password"] = password
            };
            Add(body, "data", signUpOptions.Data);
            Add(body, "redirect_to", signUpOptions.EmailRedirectTo);
            Add(body, "captcha_token", signUpOptions.CaptchaToken);
            Add(body, "channel", signUpOptions.Channel);

            var response = await RequestAsync<AuthResponse>(SupabaseHttpMethod.Post, "signup", body, null,
                cancellationToken);
            if (!response.IsSuccess)
                return response;
            var session = response.Data == null ? null : response.Data.GetSession();
            if (session != null)
                await AdoptSessionAsync(session, AuthChangeEvent.SignedIn, cancellationToken);
            return response;
        }

        public async Task<SupabaseResult<AuthSession>> SignInWithPasswordAsync(
            string emailOrPhone,
            string password,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(emailOrPhone, "emailOrPhone");
            Require(password, "password");
            var body = new JObject { ["password"] = password };
            body[emailOrPhone.IndexOf('@') >= 0 ? "email" : "phone"] = emailOrPhone;
            return await RequestAndAdoptSessionAsync("token", new[]
            {
                Pair("grant_type", "password")
            }, body, AuthChangeEvent.SignedIn, cancellationToken);
        }

        public async Task<SupabaseResult<AuthSession>> SignInAnonymouslyAsync(
            JObject data = null,
            string captchaToken = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject();
            Add(body, "data", data);
            Add(body, "captcha_token", captchaToken);
            return await RequestAndAdoptSessionAsync("signup", null, body, AuthChangeEvent.SignedIn,
                cancellationToken);
        }

        public async Task<SupabaseResult> SignInWithOtpAsync(
            string emailOrPhone,
            AuthOtpOptions otpOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(emailOrPhone, "emailOrPhone");
            otpOptions = otpOptions ?? new AuthOtpOptions();
            var body = new JObject
            {
                [emailOrPhone.IndexOf('@') >= 0 ? "email" : "phone"] = emailOrPhone,
                ["create_user"] = otpOptions.ShouldCreateUser
            };
            Add(body, "data", otpOptions.Data);
            Add(body, "redirect_to", otpOptions.EmailRedirectTo);
            Add(body, "captcha_token", otpOptions.CaptchaToken);
            Add(body, "channel", otpOptions.Channel);
            if (emailOrPhone.IndexOf('@') >= 0)
            {
                var pkce = await CreatePkceAsync(cancellationToken);
                body["code_challenge"] = pkce;
                body["code_challenge_method"] = "s256";
            }
            return await RequestEmptyAsync(SupabaseHttpMethod.Post, "otp", body, null, cancellationToken);
        }

        public async Task<SupabaseResult<AuthSession>> VerifyOtpAsync(
            string token,
            AuthOtpType type,
            string emailOrPhone = null,
            string redirectTo = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(token, "token");
            var body = new JObject { ["type"] = OtpType(type) };
            body[string.IsNullOrWhiteSpace(emailOrPhone) ? "token_hash" : "token"] = token;
            if (!string.IsNullOrWhiteSpace(emailOrPhone))
                body[emailOrPhone.IndexOf('@') >= 0 ? "email" : "phone"] = emailOrPhone;
            Add(body, "redirect_to", redirectTo);
            var changeEvent = type == AuthOtpType.Recovery
                ? AuthChangeEvent.PasswordRecovery
                : AuthChangeEvent.SignedIn;
            return await RequestAndAdoptSessionAsync("verify", null, body, changeEvent, cancellationToken);
        }

        public async Task<SupabaseResult<AuthSession>> SignInWithIdTokenAsync(
            AuthIdTokenOptions idTokenOptions,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (idTokenOptions == null)
                throw new ArgumentNullException("idTokenOptions");
            Require(idTokenOptions.Provider, "idTokenOptions.Provider");
            Require(idTokenOptions.IdToken, "idTokenOptions.IdToken");
            var body = new JObject
            {
                ["provider"] = idTokenOptions.Provider,
                ["id_token"] = idTokenOptions.IdToken
            };
            Add(body, "access_token", idTokenOptions.AccessToken);
            Add(body, "nonce", idTokenOptions.Nonce);
            Add(body, "captcha_token", idTokenOptions.CaptchaToken);
            return await RequestAndAdoptSessionAsync("token", new[] { Pair("grant_type", "id_token") },
                body, AuthChangeEvent.SignedIn, cancellationToken);
        }

        public async Task<SupabaseResult<Uri>> SignInWithOAuthAsync(
            string provider,
            AuthOAuthOptions oauthOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(provider, "provider");
            oauthOptions = oauthOptions ?? new AuthOAuthOptions();
            var verifierChallenge = await CreatePkceAsync(cancellationToken);
            var query = new List<KeyValuePair<string, string>>
            {
                Pair("provider", provider),
                Pair("skip_http_redirect", "true"),
                Pair("code_challenge", verifierChallenge),
                Pair("code_challenge_method", "s256")
            };
            Add(query, "redirect_to", oauthOptions.RedirectTo);
            Add(query, "scopes", oauthOptions.Scopes);
            if (oauthOptions.QueryParameters != null)
            {
                foreach (var property in oauthOptions.QueryParameters.Properties())
                    Add(query, property.Name, property.Value.ToString());
            }

            var uri = SupabaseHttp.Combine(endpoint, "authorize", query);
            if (oauthOptions.OpenBrowser)
            {
                if (callbackProvider == null)
                    return SupabaseResult<Uri>.Failure(SupabaseError.Create(SupabaseService.Auth,
                        SupabaseErrorKind.Configuration,
                        "OAuth browser launch requires an IAuthCallbackProvider."));
                callbackProvider.Open(uri);
            }
            return SupabaseResult<Uri>.Success(uri);
        }

        public async Task<SupabaseResult<AuthSession>> ExchangeCodeForSessionAsync(
            string code,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(code, "code");
            var verifier = await ReadPkceVerifierAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(verifier))
                return SupabaseResult<AuthSession>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Configuration,
                    "No valid PKCE verifier was found for this OAuth callback. The verifier is " +
                    "created when sign-in starts and expires after 10 minutes. This usually means " +
                    "the sign-in was never started on this device, it has already been completed, " +
                    "or too much time passed before the callback arrived."));

            var body = new JObject
            {
                ["auth_code"] = code,
                ["code_verifier"] = verifier
            };
            var result = await RequestAndAdoptSessionAsync("token", new[] { Pair("grant_type", "pkce") },
                body, AuthChangeEvent.SignedIn, cancellationToken);
            if (result.IsSuccess)
                await pkceStore.RemoveAsync(pkceKey, cancellationToken);
            return result;
        }

        public async Task<SupabaseResult<AuthSession>> HandleAuthCallbackAsync(
            Uri callback,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (callback == null)
                throw new ArgumentNullException("callback");
            var parameters = ParseParameters(callback);
            string error;
            if (parameters.TryGetValue("error_description", out error) || parameters.TryGetValue("error", out error))
                return SupabaseResult<AuthSession>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Protocol, error, parameters.ContainsKey("error_code")
                        ? parameters["error_code"]
                        : null));

            string code;
            if (parameters.TryGetValue("code", out code) && !string.IsNullOrWhiteSpace(code))
                return await ExchangeCodeForSessionAsync(code, cancellationToken);

            string accessToken;
            string refreshToken;
            if (parameters.TryGetValue("access_token", out accessToken) &&
                parameters.TryGetValue("refresh_token", out refreshToken))
                return await SetSessionAsync(accessToken, refreshToken, cancellationToken);

            return SupabaseResult<AuthSession>.Success(CurrentSession);
        }

        public async Task<SupabaseResult<AuthSsoResponse>> SignInWithSsoAsync(
            AuthSsoOptions ssoOptions,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (ssoOptions == null)
                throw new ArgumentNullException("ssoOptions");
            if (string.IsNullOrWhiteSpace(ssoOptions.Domain) && string.IsNullOrWhiteSpace(ssoOptions.ProviderId))
                throw new ArgumentException("SSO requires either a domain or provider ID.", "ssoOptions");
            var body = new JObject
            {
                ["skip_http_redirect"] = true,
                ["code_challenge"] = await CreatePkceAsync(cancellationToken),
                ["code_challenge_method"] = "s256"
            };
            Add(body, "domain", ssoOptions.Domain);
            Add(body, "provider_id", ssoOptions.ProviderId);
            Add(body, "redirect_to", ssoOptions.RedirectTo);
            Add(body, "captcha_token", ssoOptions.CaptchaToken);
            return await RequestAsync<AuthSsoResponse>(SupabaseHttpMethod.Post, "sso", body, null,
                cancellationToken);
        }

        public Task<SupabaseResult<AuthSession>> RefreshSessionAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            lock (refreshGate)
            {
                if (refreshTask != null)
                    return refreshTask;
                var started = RefreshSessionCoreAsync(cancellationToken);
                refreshTask = started;
                ClearRefreshTaskWhenComplete(started);
                return started;
            }
        }

        public async Task<SupabaseResult<AuthSession>> SetSessionAsync(
            string accessToken,
            string refreshToken,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(accessToken, "accessToken");
            Require(refreshToken, "refreshToken");
            var oldSession = CurrentSession;
            CurrentSession = new AuthSession
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                TokenType = "bearer",
                ExpiresAt = ReadJwtLong(accessToken, "exp")
            };
            var user = await GetUserAsync(cancellationToken);
            if (!user.IsSuccess)
            {
                CurrentSession = oldSession;
                return SupabaseResult<AuthSession>.Failure(user.Error, user.Metadata);
            }
            CurrentSession.User = user.Data;
            await PersistSessionAsync(cancellationToken);
            Notify(AuthChangeEvent.SignedIn);
            return SupabaseResult<AuthSession>.Success(CurrentSession, user.Metadata);
        }

        public async Task<SupabaseResult<AuthUser>> GetUserAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken))
                return SupabaseResult<AuthUser>.Failure(NotAuthenticated());
            var result = await RequestAsync<AuthUser>(SupabaseHttpMethod.Get, "user", null, null,
                cancellationToken);
            if (result.IsSuccess && CurrentSession != null)
            {
                CurrentSession.User = result.Data;
                await PersistSessionAsync(cancellationToken);
            }
            return result;
        }

        public async Task<SupabaseResult<AuthUser>> UpdateUserAsync(
            JObject attributes,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (attributes == null)
                throw new ArgumentNullException("attributes");
            if (CurrentSession == null)
                return SupabaseResult<AuthUser>.Failure(NotAuthenticated());
            var result = await RequestAsync<AuthUser>(SupabaseHttpMethod.Put, "user", attributes, null,
                cancellationToken);
            if (result.IsSuccess)
            {
                CurrentSession.User = result.Data;
                await PersistSessionAsync(cancellationToken);
                Notify(AuthChangeEvent.UserUpdated);
            }
            return result;
        }

        public async Task<SupabaseResult> ResetPasswordForEmailAsync(
            string email,
            string redirectTo = null,
            string captchaToken = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(email, "email");
            var body = new JObject
            {
                ["email"] = email,
                ["code_challenge"] = await CreatePkceAsync(cancellationToken),
                ["code_challenge_method"] = "s256"
            };
            Add(body, "redirect_to", redirectTo);
            Add(body, "captcha_token", captchaToken);
            return await RequestEmptyAsync(SupabaseHttpMethod.Post, "recover", body, null, cancellationToken);
        }

        public Task<SupabaseResult> ReauthenticateAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (CurrentSession == null)
                return Task.FromResult(SupabaseResult.Failure(NotAuthenticated()));
            return RequestEmptyAsync(SupabaseHttpMethod.Get, "reauthenticate", null, null, cancellationToken);
        }

        public async Task<SupabaseResult> SignOutAsync(
            AuthSignOutScope scope = AuthSignOutScope.Global,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (CurrentSession == null)
            {
                await SafeRemoveSessionAsync(cancellationToken);
                return SupabaseResult.Success();
            }
            var scopeValue = scope.ToString().ToLowerInvariant();
            var result = await RequestEmptyAsync(SupabaseHttpMethod.Post, "logout", null,
                new[] { Pair("scope", scopeValue) }, cancellationToken);
            if (result.IsSuccess || (result.Error != null &&
                (result.Error.StatusCode == 401 || result.Error.StatusCode == 403 || result.Error.StatusCode == 404)))
            {
                CurrentSession = null;
                await SafeRemoveSessionAsync(cancellationToken);
                Notify(AuthChangeEvent.SignedOut);
                return SupabaseResult.Success(result.Metadata);
            }
            return result;
        }

        public async Task<SupabaseResult<Uri>> LinkIdentityAsync(
            string provider,
            AuthOAuthOptions oauthOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (CurrentSession == null)
                return SupabaseResult<Uri>.Failure(NotAuthenticated());
            Require(provider, "provider");
            oauthOptions = oauthOptions ?? new AuthOAuthOptions();
            var query = new List<KeyValuePair<string, string>>
            {
                Pair("provider", provider),
                Pair("skip_http_redirect", "true"),
                Pair("code_challenge", await CreatePkceAsync(cancellationToken)),
                Pair("code_challenge_method", "s256")
            };
            Add(query, "redirect_to", oauthOptions.RedirectTo);
            Add(query, "scopes", oauthOptions.Scopes);
            var result = await RequestAsync<AuthSsoResponse>(SupabaseHttpMethod.Get,
                "user/identities/authorize", null, query, cancellationToken);
            if (!result.IsSuccess)
                return SupabaseResult<Uri>.Failure(result.Error, result.Metadata);
            Uri uri;
            if (result.Data == null || !Uri.TryCreate(result.Data.Url, UriKind.Absolute, out uri))
                return SupabaseResult<Uri>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Protocol, "Supabase did not return a valid identity authorization URL."));
            if (oauthOptions.OpenBrowser)
            {
                if (callbackProvider == null)
                    return SupabaseResult<Uri>.Failure(SupabaseError.Create(SupabaseService.Auth,
                        SupabaseErrorKind.Configuration,
                        "Identity linking requires an IAuthCallbackProvider to open the browser."));
                callbackProvider.Open(uri);
            }
            return SupabaseResult<Uri>.Success(uri, result.Metadata);
        }

        public async Task<SupabaseResult<AuthUser>> UnlinkIdentityAsync(
            string identityId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(identityId, "identityId");
            if (CurrentSession == null)
                return SupabaseResult<AuthUser>.Failure(NotAuthenticated());
            var result = await RequestAsync<AuthUser>(SupabaseHttpMethod.Delete,
                "user/identities/" + Uri.EscapeDataString(identityId), null, null, cancellationToken);
            if (result.IsSuccess)
            {
                CurrentSession.User = result.Data;
                await PersistSessionAsync(cancellationToken);
                Notify(AuthChangeEvent.UserUpdated);
            }
            return result;
        }

        public async Task<SupabaseResult<IReadOnlyList<AuthIdentity>>> ListIdentitiesAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var user = await GetUserAsync(cancellationToken);
            if (!user.IsSuccess)
                return SupabaseResult<IReadOnlyList<AuthIdentity>>.Failure(user.Error, user.Metadata);
            IReadOnlyList<AuthIdentity> identities = user.Data.Identities ?? new List<AuthIdentity>();
            return SupabaseResult<IReadOnlyList<AuthIdentity>>.Success(identities, user.Metadata);
        }

        public Task<SupabaseResult<AuthMfaEnrollment>> EnrollMfaAsync(
            AuthMfaEnrollOptions enrollOptions,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (enrollOptions == null)
                throw new ArgumentNullException("enrollOptions");
            var body = new JObject { ["factor_type"] = enrollOptions.FactorType };
            Add(body, "friendly_name", enrollOptions.FriendlyName);
            Add(body, "issuer", enrollOptions.Issuer);
            Add(body, "phone", enrollOptions.Phone);
            return RequestAsync<AuthMfaEnrollment>(SupabaseHttpMethod.Post, "factors", body, null,
                cancellationToken);
        }

        public Task<SupabaseResult<AuthMfaChallenge>> ChallengeMfaAsync(
            string factorId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(factorId, "factorId");
            return RequestAsync<AuthMfaChallenge>(SupabaseHttpMethod.Post,
                "factors/" + Uri.EscapeDataString(factorId) + "/challenge", new JObject(), null,
                cancellationToken);
        }

        public async Task<SupabaseResult<AuthSession>> VerifyMfaAsync(
            string factorId,
            string challengeId,
            string code,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(factorId, "factorId");
            Require(challengeId, "challengeId");
            Require(code, "code");
            var body = new JObject
            {
                ["challenge_id"] = challengeId,
                ["code"] = code
            };
            return await RequestAndAdoptSessionAsync(
                "factors/" + Uri.EscapeDataString(factorId) + "/verify", null, body,
                AuthChangeEvent.MfaChallengeVerified, cancellationToken);
        }

        public async Task<SupabaseResult<AuthSession>> ChallengeAndVerifyMfaAsync(
            string factorId,
            string code,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var challenge = await ChallengeMfaAsync(factorId, cancellationToken);
            if (!challenge.IsSuccess)
                return SupabaseResult<AuthSession>.Failure(challenge.Error, challenge.Metadata);
            return await VerifyMfaAsync(factorId, challenge.Data.Id, code, cancellationToken);
        }

        public Task<SupabaseResult> UnenrollMfaAsync(
            string factorId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Require(factorId, "factorId");
            return RequestEmptyAsync(SupabaseHttpMethod.Delete,
                "factors/" + Uri.EscapeDataString(factorId), null, null, cancellationToken);
        }

        public async Task<SupabaseResult<IReadOnlyList<AuthMfaFactor>>> ListMfaFactorsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var response = await RequestAsync<JObject>(SupabaseHttpMethod.Get, "factors", null, null,
                cancellationToken);
            if (!response.IsSuccess)
                return SupabaseResult<IReadOnlyList<AuthMfaFactor>>.Failure(response.Error, response.Metadata);
            var factors = new List<AuthMfaFactor>();
            foreach (var name in new[] { "all", "totp", "phone", "verified" })
            {
                var array = response.Data == null ? null : response.Data[name] as JArray;
                if (array == null)
                    continue;
                foreach (var token in array)
                {
                    var factor = token.ToObject<AuthMfaFactor>(JsonSerializer.Create(SupabaseJson.Settings));
                    if (factor != null && factors.Find(existing => existing.Id == factor.Id) == null)
                        factors.Add(factor);
                }
            }
            return SupabaseResult<IReadOnlyList<AuthMfaFactor>>.Success(factors, response.Metadata);
        }

        public SupabaseResult<AuthAssuranceLevel> GetAuthenticatorAssuranceLevel()
        {
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken))
                return SupabaseResult<AuthAssuranceLevel>.Failure(NotAuthenticated());
            try
            {
                var payload = ReadJwtPayload(CurrentSession.AccessToken);
                var current = (string)payload["aal"] ?? "aal1";
                var methods = payload["amr"] == null
                    ? new List<AuthAuthenticationMethod>()
                    : payload["amr"].ToObject<List<AuthAuthenticationMethod>>(
                        JsonSerializer.Create(SupabaseJson.Settings));
                var hasVerifiedFactor = CurrentUser != null && CurrentUser.Factors != null &&
                    CurrentUser.Factors.Exists(factor => string.Equals(factor.Status, "verified",
                        StringComparison.OrdinalIgnoreCase));
                return SupabaseResult<AuthAssuranceLevel>.Success(new AuthAssuranceLevel
                {
                    CurrentLevel = current,
                    NextLevel = hasVerifiedFactor ? "aal2" : current,
                    CurrentAuthenticationMethods = methods
                });
            }
            catch (Exception exception)
            {
                return SupabaseResult<AuthAssuranceLevel>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Serialization, "The session JWT could not be decoded.",
                    details: exception.Message));
            }
        }

        private async Task<SupabaseResult<AuthSession>> RefreshSessionCoreAsync(CancellationToken cancellationToken)
        {
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.RefreshToken))
                return SupabaseResult<AuthSession>.Failure(NotAuthenticated());
            var body = new JObject { ["refresh_token"] = CurrentSession.RefreshToken };
            var result = await RequestAndAdoptSessionAsync("token",
                new[] { Pair("grant_type", "refresh_token") }, body, AuthChangeEvent.TokenRefreshed,
                cancellationToken);
            if (!result.IsSuccess && result.Error != null &&
                (result.Error.StatusCode == 400 || result.Error.StatusCode == 401))
            {
                CurrentSession = null;
                await SafeRemoveSessionAsync(CancellationToken.None);
                Notify(AuthChangeEvent.SignedOut);
            }
            return result;
        }

        private async void ClearRefreshTaskWhenComplete(Task<SupabaseResult<AuthSession>> task)
        {
            try { await task; }
            catch { }
            lock (refreshGate)
            {
                if (ReferenceEquals(refreshTask, task))
                    refreshTask = null;
            }
        }

        private async Task<SupabaseResult<AuthSession>> RequestAndAdoptSessionAsync(
            string path,
            IEnumerable<KeyValuePair<string, string>> query,
            JObject body,
            AuthChangeEvent changeEvent,
            CancellationToken cancellationToken)
        {
            var response = await RequestAsync<AuthResponse>(SupabaseHttpMethod.Post, path, body, query,
                cancellationToken);
            if (!response.IsSuccess)
                return SupabaseResult<AuthSession>.Failure(response.Error, response.Metadata);
            var session = response.Data == null ? null : response.Data.GetSession();
            if (session == null)
                return SupabaseResult<AuthSession>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Protocol, "Supabase Auth did not return a session."), response.Metadata);
            await AdoptSessionAsync(session, changeEvent, cancellationToken);
            return SupabaseResult<AuthSession>.Success(session, response.Metadata);
        }

        private async Task<SupabaseResult<T>> RequestAsync<T>(
            SupabaseHttpMethod method,
            string path,
            object body,
            IEnumerable<KeyValuePair<string, string>> query,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var request = SupabaseHttp.CreateJsonRequest(options, SupabaseHttp.Combine(endpoint, path, query),
                method, body, CurrentSession == null ? null : CurrentSession.AccessToken);
            var response = await SupabaseHttp.SendAsync(transport, request, cancellationToken);
            var metadata = SupabaseHttp.Metadata(response);
            if (!response.IsSuccessStatusCode)
                return SupabaseResult<T>.Failure(SupabaseHttp.Error(SupabaseService.Auth, response), metadata);
            try
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                    return SupabaseResult<T>.Success(default(T), metadata);
                return SupabaseResult<T>.Success(SupabaseJson.Deserialize<T>(response.Text), metadata);
            }
            catch (Exception exception)
            {
                return SupabaseResult<T>.Failure(SupabaseError.Create(SupabaseService.Auth,
                    SupabaseErrorKind.Serialization, "Supabase Auth returned an invalid response.",
                    details: exception.Message, rawResponse: SupabaseHttp.Redact(response.Text)), metadata);
            }
        }

        private async Task<SupabaseResult> RequestEmptyAsync(
            SupabaseHttpMethod method,
            string path,
            object body,
            IEnumerable<KeyValuePair<string, string>> query,
            CancellationToken cancellationToken)
        {
            var request = SupabaseHttp.CreateJsonRequest(options, SupabaseHttp.Combine(endpoint, path, query),
                method, body, CurrentSession == null ? null : CurrentSession.AccessToken);
            var response = await SupabaseHttp.SendAsync(transport, request, cancellationToken);
            var metadata = SupabaseHttp.Metadata(response);
            return response.IsSuccessStatusCode
                ? SupabaseResult.Success(metadata)
                : SupabaseResult.Failure(SupabaseHttp.Error(SupabaseService.Auth, response), metadata);
        }

        private async Task AdoptSessionAsync(
            AuthSession session,
            AuthChangeEvent changeEvent,
            CancellationToken cancellationToken)
        {
            session.NormalizeExpiry();
            CurrentSession = session;
            await PersistSessionAsync(cancellationToken);
            Notify(changeEvent);
        }

        private async Task PersistSessionAsync(CancellationToken cancellationToken)
        {
            if (CurrentSession == null)
            {
                await SafeRemoveSessionAsync(cancellationToken);
                return;
            }
            try
            {
                await sessionStore.SetAsync(sessionKey, SupabaseJson.Serialize(CurrentSession), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Warning,
                    "The Supabase session is active but could not be persisted.", exception);
            }
        }

        private async Task SafeRemoveSessionAsync(CancellationToken cancellationToken)
        {
            try { await sessionStore.RemoveAsync(sessionKey, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Warning, "The persisted Supabase session could not be removed.",
                    exception);
            }
        }

        private void StartAutoRefresh()
        {
            if (!options.AutoRefreshToken || refreshLoopCancellation != null)
                return;
            refreshLoopCancellation = new CancellationTokenSource();
            AutoRefreshLoopAsync(refreshLoopCancellation.Token);
        }

        private async void AutoRefreshLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await SupabaseRuntimeHost.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    if (CurrentSession != null && CurrentSession.IsExpired(RefreshMargin))
                    {
                        var result = await RefreshSessionAsync(cancellationToken);
                        if (!result.IsSuccess)
                            options.Logger.Log(SupabaseLogLevel.Warning,
                                "Automatic Supabase token refresh failed: " + result.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Error, "The Supabase token refresh loop stopped unexpectedly.",
                    exception);
            }
        }

        private async void OnFocusChanged(bool focused)
        {
            if (!focused || CurrentSession == null || !CurrentSession.IsExpired(RefreshMargin))
                return;
            try { await RefreshSessionAsync(); }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Warning, "Session refresh after application resume failed.",
                    exception);
            }
        }

        private async void OnCallbackReceived(Uri callback)
        {
            try
            {
                var result = await HandleAuthCallbackAsync(callback);
                if (!result.IsSuccess)
                    options.Logger.Log(SupabaseLogLevel.Error, "Supabase Auth callback failed: " + result.Error);
            }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Error, "Supabase Auth callback failed.", exception);
            }
            finally
            {
                ClearSensitiveCallback(callback);
            }
        }

        private void ClearSensitiveCallback(Uri callback)
        {
            if (callback == null)
                return;
            var parameters = ParseParameters(callback);
            if (!parameters.ContainsKey("code") &&
                !parameters.ContainsKey("access_token") &&
                !parameters.ContainsKey("refresh_token") &&
                !parameters.ContainsKey("error") &&
                !parameters.ContainsKey("error_description"))
                return;
            var sanitizer = callbackProvider as IAuthCallbackSanitizer;
            if (sanitizer == null)
                return;
            try { sanitizer.ClearSensitiveCallback(callback); }
            catch (Exception exception)
            {
                options.Logger.Log(SupabaseLogLevel.Warning,
                    "The Supabase Auth callback URL could not be sanitized.", exception);
            }
        }

        private async Task<string> CreatePkceAsync(CancellationToken cancellationToken)
        {
            var bytes = new byte[64];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            var verifier = Base64Url(bytes);
            byte[] challengeBytes;
            using (var sha = SHA256.Create())
                challengeBytes = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            var envelope = new JObject
            {
                ["v"] = verifier,
                ["exp"] = DateTimeOffset.UtcNow.Add(PkceLifetime).ToUnixTimeSeconds()
            };
            await pkceStore.SetAsync(pkceKey, envelope.ToString(Formatting.None), cancellationToken);
            return Base64Url(challengeBytes);
        }

        private async Task<string> ReadPkceVerifierAsync(CancellationToken cancellationToken)
        {
            var stored = await pkceStore.GetAsync(pkceKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(stored))
                return null;

            JObject envelope;
            try { envelope = JObject.Parse(stored); }
            catch (JsonException) { return null; }

            var expiry = (long?)envelope["exp"];
            if (!expiry.HasValue || expiry.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await pkceStore.RemoveAsync(pkceKey, cancellationToken);
                return null;
            }
            return (string)envelope["v"];
        }

        private void Notify(AuthChangeEvent changeEvent)
        {
            var handler = StateChanged;
            if (handler == null)
                return;
            var args = new AuthStateChangedEventArgs(changeEvent, CurrentSession);
            SupabaseRuntimeHost.Post(delegate { handler(this, args); });
        }

        private static SupabaseError NotAuthenticated()
        {
            return SupabaseError.Create(SupabaseService.Auth, SupabaseErrorKind.Protocol,
                "No authenticated Supabase session is available.", "not_authenticated", 401);
        }

        private static string OtpType(AuthOtpType type)
        {
            switch (type)
            {
                case AuthOtpType.Email: return "email";
                case AuthOtpType.Signup: return "signup";
                case AuthOtpType.Invite: return "invite";
                case AuthOtpType.MagicLink: return "magiclink";
                case AuthOtpType.Recovery: return "recovery";
                case AuthOtpType.EmailChange: return "email_change";
                case AuthOtpType.Sms: return "sms";
                case AuthOtpType.PhoneChange: return "phone_change";
                default: throw new ArgumentOutOfRangeException("type", type, null);
            }
        }

        private static Dictionary<string, string> ParseParameters(Uri uri)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ParseParameterText(uri.Query, result);
            ParseParameterText(uri.Fragment, result);
            return result;
        }

        private static void ParseParameterText(string text, IDictionary<string, string> result)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            var trimmed = text.TrimStart('?', '#');
            foreach (var segment in trimmed.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    continue;
                var equals = segment.IndexOf('=');
                var key = equals < 0 ? segment : segment.Substring(0, equals);
                var value = equals < 0 ? string.Empty : segment.Substring(equals + 1);
                result[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                    Uri.UnescapeDataString(value.Replace('+', ' '));
            }
        }

        private static long ReadJwtLong(string jwt, string claim)
        {
            try
            {
                var value = ReadJwtPayload(jwt)[claim];
                return value == null ? 0 : value.Value<long>();
            }
            catch { return 0; }
        }

        private static JObject ReadJwtPayload(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                throw new FormatException("JWT must contain three segments.");
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            switch (encoded.Length % 4)
            {
                case 2: encoded += "=="; break;
                case 3: encoded += "="; break;
            }
            return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }

        private static string StableKey(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                    builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static void Add(JObject body, string key, object value)
        {
            if (value == null)
                return;
            var text = value as string;
            if (text != null && string.IsNullOrWhiteSpace(text))
                return;
            body[key] = value is JToken ? (JToken)value : JToken.FromObject(value);
        }

        private static void Add(ICollection<KeyValuePair<string, string>> values, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(Pair(key, value));
        }

        private static void Require(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(parameter + " cannot be empty.", parameter);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (refreshLoopCancellation != null)
            {
                refreshLoopCancellation.Cancel();
                refreshLoopCancellation.Dispose();
                refreshLoopCancellation = null;
            }
            SupabaseRuntimeHost.FocusChanged -= OnFocusChanged;
            if (callbackProvider != null)
                callbackProvider.CallbackReceived -= OnCallbackReceived;
        }
    }
}
