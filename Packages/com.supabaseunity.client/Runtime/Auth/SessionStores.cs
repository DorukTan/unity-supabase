using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Supabase.Unity
{
    public sealed class MemorySessionStore : ISessionStore
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();

        /// <summary>A snapshot of the stored keys, for diagnostics and tests.</summary>
        public IEnumerable<string> Keys
        {
            get { lock (gate) return new List<string>(values.Keys); }
        }

        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                string value;
                values.TryGetValue(key, out value);
                return Task.FromResult(value);
            }
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                values.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Persists sessions as plain text under <see cref="Application.persistentDataPath"/>.
    /// On Android and iOS that directory is app-private. On Windows, macOS, and Linux it is
    /// readable by any process running as the same user, and a refresh token found there can
    /// mint new access tokens until it is revoked. Implement <see cref="ISessionStore"/> over
    /// the platform keystore if that matters for your game.
    /// </summary>
    public sealed class UnencryptedFileSessionStore : ISessionStore
    {
        private readonly string directory;

        public UnencryptedFileSessionStore(string directory = null)
        {
            this.directory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Application.persistentDataPath, "Supabase")
                : directory;
        }

        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(key);
            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var path = GetPath(key);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value ?? string.Empty, Encoding.UTF8);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(temporary, path);
                }
                catch (IOException)
                {
                    File.Delete(path);
                    File.Move(temporary, path);
                }
            }
            else
            {
                File.Move(temporary, path);
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(key);
            if (File.Exists(path))
                File.Delete(path);
            var temporary = path + ".tmp";
            if (File.Exists(temporary))
                File.Delete(temporary);
            return Task.CompletedTask;
        }

        private string GetPath(string key)
        {
            var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key ?? string.Empty))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Path.Combine(directory, safe + ".session");
        }
    }

    public sealed class WebLocalStorageSessionStore : ISessionStore
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int SupabaseUnity_LocalStorageRead(string key, byte[] destination, int capacity);

        [DllImport("__Internal")]
        private static extern void SupabaseUnity_LocalStorageWrite(string key, string value);

        [DllImport("__Internal")]
        private static extern void SupabaseUnity_LocalStorageRemove(string key);
#endif

        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            var length = SupabaseUnity_LocalStorageRead(key, null, 0);
            if (length < 0)
                return Task.FromResult<string>(null);
            var bytes = new byte[length + 1];
            SupabaseUnity_LocalStorageRead(key, bytes, bytes.Length);
            return Task.FromResult(Encoding.UTF8.GetString(bytes, 0, length));
#else
            throw new PlatformNotSupportedException(
                "WebLocalStorageSessionStore can only be used in a WebGL player.");
#endif
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            SupabaseUnity_LocalStorageWrite(key, value ?? string.Empty);
            return Task.CompletedTask;
#else
            throw new PlatformNotSupportedException(
                "WebLocalStorageSessionStore can only be used in a WebGL player.");
#endif
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            SupabaseUnity_LocalStorageRemove(key);
            return Task.CompletedTask;
#else
            throw new PlatformNotSupportedException(
                "WebLocalStorageSessionStore can only be used in a WebGL player.");
#endif
        }
    }

    internal static class PlatformSessionStore
    {
        internal static ISessionStore Create(bool persist)
        {
            return persist ? CreateDurable() : (ISessionStore)new MemorySessionStore();
        }

        internal static ISessionStore CreateDurable()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebLocalStorageSessionStore();
#else
            return new UnencryptedFileSessionStore();
#endif
        }
    }

    public sealed class DefaultAuthCallbackProvider : IAuthCallbackProvider, IAuthCallbackSanitizer, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SupabaseUnity_ClearAuthCallbackUrl();
#endif

        public event Action<Uri> CallbackReceived;
        public Uri InitialCallback { get; private set; }

        public DefaultAuthCallbackProvider()
        {
            Uri initial;
            if (!string.IsNullOrWhiteSpace(Application.absoluteURL) &&
                Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out initial))
                InitialCallback = initial;
            Application.deepLinkActivated += HandleDeepLink;
        }

        public void Open(Uri authorizationUri)
        {
            if (authorizationUri == null)
                throw new ArgumentNullException("authorizationUri");
            Application.OpenURL(authorizationUri.AbsoluteUri);
        }

        public void ClearSensitiveCallback(Uri callbackUri)
        {
            if (InitialCallback == callbackUri)
                InitialCallback = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            SupabaseUnity_ClearAuthCallbackUrl();
#endif
        }

        private void HandleDeepLink(string url)
        {
            Uri callback;
            if (!Uri.TryCreate(url, UriKind.Absolute, out callback))
                return;
            var handler = CallbackReceived;
            if (handler != null)
                handler(callback);
        }

        public void Dispose()
        {
            Application.deepLinkActivated -= HandleDeepLink;
        }
    }
}
