# Editor scripting

Use `SupabaseEditorClient.Create` from `Supabase.Unity.Editor` for requests made by Editor
scripts outside Play mode. It returns a regular `SupabaseClient` whose HTTP requests advance on
Editor updates. Use `await`; blocking with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`
can freeze the Editor because requests need the main thread to advance.

Keep the calling script in an `Editor` folder. If it has its own assembly definition, reference
both `Supabase.Unity.Editor` and `Supabase.Unity.Runtime`.

## Supported operations

| Area | Edit mode behavior |
| --- | --- |
| Database, Storage, Functions | Explicit HTTP operations, including upload/download progress and download chunks. Normal key validation and RLS/Storage policies apply. |
| Auth | Explicit HTTP operations, such as password or anonymous sign-in, user requests, sign-out, and manual session refresh. Auth notifications run on the Editor main thread. |
| Session and PKCE stores | Separate memory-only stores per client. State is discarded with the client and is not shared with Play mode or stored in the settings asset. |
| Background refresh | Disabled. Refresh an active session explicitly when needed. |
| Realtime | Unsupported; connecting returns an `UnsupportedPlatform` result. |
| Browser authentication | Automatic browser launch throws `PlatformNotSupportedException`. There is no automatic deep-link or browser callback handling. |

The factory accepts a settings asset, a URL/key pair, or `SupabaseClientOptions`. It copies
connection options, endpoints, headers, timeout, and logger, without changing the source.
`PersistSession`, `AutoRefreshToken`, and `AutoConnectRealtime` are always disabled for these
clients. Custom HTTP/WebSocket transports, session/PKCE stores, and callback providers are
rejected because the factory owns their lifetime. Keep those lifecycle options unchanged on
the returned client.

The regular `new SupabaseClient(...)` path still uses the player coroutine host. Use the
Editor factory for Edit mode work and the regular constructor in player code or Play mode.
The Model Generator remains a separate Editor-only schema workflow; Editor clients continue
to accept only publishable keys or legacy `anon` keys.

## Own the client from the Editor window

Create, call, and dispose the client on Unity's main thread. Dispose it when the owning tool
closes; that aborts pending requests and cancels their tasks. The package also disposes all
Editor clients before assembly reload, Editor exit, and entry into Play mode, including when
domain reload is disabled. Recreate the client for a later operation.

This example reads the `scores` table from the [Quickstart](../Samples~/Quickstart/README.md).
The same pattern works for Storage and Functions calls. It creates a client for each button
action and ignores completions belonging to a window that has closed.

```csharp
using System;
using Supabase.Unity;
using Supabase.Unity.Editor;
using UnityEditor;
using UnityEngine;

public sealed class SupabaseScoresWindow : EditorWindow
{
    private SupabaseSettings settings;
    private SupabaseClient client;
    private string status;

    [MenuItem("Tools/Game/Read Scores")]
    private static void Open()
    {
        GetWindow<SupabaseScoresWindow>("Scores");
    }

    private void OnGUI()
    {
        settings = (SupabaseSettings)EditorGUILayout.ObjectField(
            "Settings", settings, typeof(SupabaseSettings), false);
        using (new EditorGUI.DisabledScope(settings == null || client != null))
        {
            if (GUILayout.Button("Read scores")) ReadScores();
        }
        EditorGUILayout.LabelField(status ?? "Ready");
    }

    private async void ReadScores()
    {
        SupabaseClient current = null;
        try
        {
            current = SupabaseEditorClient.Create(settings);
            client = current;
            status = "Loading...";
            var result = await current.From<Score>().Limit(20).GetAsync();
            if (this != null && ReferenceEquals(client, current))
                status = result.IsSuccess ? "Loaded " + result.Data.Count + " scores."
                    : result.Error.Message;
        }
        catch (OperationCanceledException)
        {
            if (this != null && ReferenceEquals(client, current)) status = "Cancelled.";
        }
        catch (Exception exception)
        {
            // Avoid logging raw credentials or callback URLs.
            if (this != null && ReferenceEquals(client, current)) status = exception.GetType().Name;
        }
        finally
        {
            if (current != null) current.Dispose();
            if (this != null && ReferenceEquals(client, current))
            {
                client = null;
                Repaint();
            }
        }
    }

    private void OnDisable()
    {
        var previous = client;
        client = null;
        if (previous != null) previous.Dispose();
    }

    [SupabaseTable("scores")]
    private sealed class Score
    {
        [SupabaseColumn("id")] public long Id { get; set; }
    }
}
```

For authenticated data, use the same client for explicit sign-in and the later request. Dispose
it when the tool closes and handle cancellation after every `await` before accessing the
window. Supply a `CancellationToken` to a network operation to cancel it independently of
disposing the entire client. A cancelled server mutation may already have been applied; check
the server state before retrying a write.
