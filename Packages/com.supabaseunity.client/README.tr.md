# Unity için Supabase

Bu paket Unity 2021.3 LTS ile Unity 6 arasında aynı API ile Auth, Database, Realtime, Storage ve Edge Functions kullanmak için Unity’ye özgü bir istemci sağlar. WebGL’de tarayıcı WebSocket köprüsünü kullanır; diğer platformlarda native WebSocket kullanır. Oturum kalıcılığı varsayılan olarak kapalıdır. Açıldığında oturumlar WebGL’de `localStorage`, diğer platformlarda `persistentDataPath` altında saklanır.

## Hızlı başlangıç

Package Manager’da **Add package from git URL** seçeneğine şunu gir:

```text
https://github.com/DorukTan/unity-supabase.git?path=/Packages/com.supabaseunity.client#v0.2.0-beta.6
```

Ardından **Assets > Create > Supabase > Settings** ile ayar dosyası oluştur. Supabase Dashboard’daki **Connect** ekranından proje URL’sini ve `sb_publishable_...` anahtarını alabilirsin. Eski `anon` JWT anahtarları da desteklenir.

```csharp
using Supabase.Unity;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    [SerializeField] private SupabaseSettings settings;
    private SupabaseClient client;

    private async void Start()
    {
        client = new SupabaseClient(settings);
        var initialized = await client.InitializeAsync();
        if (!initialized.IsSuccess)
        {
            Debug.LogError(initialized.Error);
            return;
        }

        var result = await client.From<PlayerProfile>("profiles")
            .Select("id,username,score")
            .Order("score", ascending: false)
            .Limit(20)
            .GetAsync();

        if (result.IsSuccess)
            foreach (var profile in result.Data) Debug.Log(profile.Username);
    }

    private void OnDestroy()
    {
        if (client != null) client.Dispose();
    }
}

public sealed class PlayerProfile
{
    [SupabaseColumn("id")] public string Id { get; set; }
    [SupabaseColumn("username")] public string Username { get; set; }
    [SupabaseColumn("score")] public int Score { get; set; }
}
```

Giriş örneği:

```csharp
var result = await client.Auth.SignInWithPasswordAsync(email, password);
if (!result.IsSuccess)
    Debug.LogError(result.Error.Message);
```

Realtime örneği:

```csharp
var channel = client.Realtime.Channel("scores");
channel.OnPostgresChanges(new RealtimePostgresChangeFilter
{
    Event = RealtimePostgresEvent.All,
    Schema = "public",
    Table = "scores"
}, change => Debug.Log(change.Event + ": " + change.Schema + "." + change.Table));

await channel.SubscribeAsync();
```

Önemli: Unity uygulamasına hiçbir zaman `sb_secret_...` veya `service_role` anahtarı koyma. Bunlar derlenen oyuncu tarafından çıkarılabilir. İstemci güvenliği publishable/anon anahtarını saklamaya değil, Supabase RLS ve Storage policy kurallarına dayanır. Paket bu anahtarları ayar dosyasında fark ederse build’i durdurur.

Daha ayrıntılı bilgi için `Documentation~` klasörüne ve içe aktarılabilir `Quickstart` örneğine bakabilirsin.
