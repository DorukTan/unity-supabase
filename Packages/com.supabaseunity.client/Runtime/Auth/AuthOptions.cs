using System;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public enum AuthChangeEvent
    {
        InitialSession,
        SignedIn,
        SignedOut,
        TokenRefreshed,
        UserUpdated,
        PasswordRecovery,
        MfaChallengeVerified
    }

    public enum AuthOtpType
    {
        Email,
        Signup,
        Invite,
        MagicLink,
        Recovery,
        EmailChange,
        Sms,
        PhoneChange
    }

    public enum AuthSignOutScope
    {
        Global,
        Local,
        Others
    }

    public sealed class AuthStateChangedEventArgs : EventArgs
    {
        public AuthChangeEvent Event { get; private set; }
        public AuthSession Session { get; private set; }

        internal AuthStateChangedEventArgs(AuthChangeEvent changeEvent, AuthSession session)
        {
            Event = changeEvent;
            Session = session;
        }
    }

    public sealed class AuthSignUpOptions
    {
        public JObject Data { get; set; }
        public string EmailRedirectTo { get; set; }
        public string CaptchaToken { get; set; }
        public string Channel { get; set; }
    }

    public sealed class AuthOtpOptions
    {
        public bool ShouldCreateUser { get; set; } = true;
        public JObject Data { get; set; }
        public string EmailRedirectTo { get; set; }
        public string CaptchaToken { get; set; }
        public string Channel { get; set; }
    }

    public sealed class AuthOAuthOptions
    {
        public string RedirectTo { get; set; }
        public string Scopes { get; set; }
        public bool OpenBrowser { get; set; } = true;
        public JObject QueryParameters { get; set; }
    }

    public sealed class AuthIdTokenOptions
    {
        public string Provider { get; set; }
        public string IdToken { get; set; }
        public string AccessToken { get; set; }
        public string Nonce { get; set; }
        public string CaptchaToken { get; set; }
    }

    public sealed class AuthSsoOptions
    {
        public string Domain { get; set; }
        public string ProviderId { get; set; }
        public string RedirectTo { get; set; }
        public string CaptchaToken { get; set; }
    }

    public sealed class AuthMfaEnrollOptions
    {
        public string FactorType { get; set; } = "totp";
        public string FriendlyName { get; set; }
        public string Issuer { get; set; }
        public string Phone { get; set; }
    }
}
