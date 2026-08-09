mergeInto(LibraryManager.library, {
  SupabaseUnity_LocalStorageRead: function (keyPtr, destinationPtr, capacity) {
    var key = UTF8ToString(keyPtr);
    var value = window.localStorage.getItem(key);
    if (value === null) return -1;
    var length = lengthBytesUTF8(value);
    if (destinationPtr && capacity > 0) stringToUTF8(value, destinationPtr, capacity);
    return length;
  },

  SupabaseUnity_LocalStorageWrite: function (keyPtr, valuePtr) {
    window.localStorage.setItem(UTF8ToString(keyPtr), UTF8ToString(valuePtr));
  },

  SupabaseUnity_LocalStorageRemove: function (keyPtr) {
    window.localStorage.removeItem(UTF8ToString(keyPtr));
  },

  SupabaseUnity_ClearAuthCallbackUrl: function () {
    if (!window.history || !window.history.replaceState || !window.URL) return;
    try {
      var url = new URL(window.location.href);
      var sensitive = ['code', 'access_token', 'refresh_token', 'provider_token',
        'provider_refresh_token', 'error', 'error_code', 'error_description', 'type'];
      sensitive.forEach(function (key) { url.searchParams.delete(key); });

      var hashText = url.hash && url.hash.length > 1 ? url.hash.substring(1) : '';
      if (hashText) {
        var hash = new URLSearchParams(hashText);
        var containedAuthValue = sensitive.some(function (key) { return hash.has(key); });
        if (containedAuthValue) {
          sensitive.forEach(function (key) { hash.delete(key); });
          var remaining = hash.toString();
          url.hash = remaining ? '#' + remaining : '';
        }
      }

      window.history.replaceState(window.history.state, document.title,
        url.pathname + url.search + url.hash);
    } catch (_) {
      // URL cleanup is best-effort and must never interrupt the Unity player.
    }
  },

  SupabaseUnity_WebSocketConnect: function (id, urlPtr) {
    if (!window.__supabaseUnitySockets) window.__supabaseUnitySockets = {};
    var socket = new WebSocket(UTF8ToString(urlPtr));
    window.__supabaseUnitySockets[id] = socket;
    socket.onopen = function () {
      SendMessage('[Supabase Runtime]', 'SupabaseWebSocketOpened', String(id));
    };
    socket.onmessage = function (event) {
      SendMessage('[Supabase Runtime]', 'SupabaseWebSocketMessage', JSON.stringify({ id: id, message: String(event.data) }));
    };
    socket.onclose = function (event) {
      delete window.__supabaseUnitySockets[id];
      SendMessage('[Supabase Runtime]', 'SupabaseWebSocketClosed', JSON.stringify({ id: id, code: event.code, reason: event.reason || '' }));
    };
    socket.onerror = function () {
      SendMessage('[Supabase Runtime]', 'SupabaseWebSocketError', JSON.stringify({ id: id, message: 'Browser WebSocket error.' }));
    };
  },

  SupabaseUnity_WebSocketSend: function (id, messagePtr) {
    var sockets = window.__supabaseUnitySockets || {};
    var socket = sockets[id];
    if (!socket || socket.readyState !== WebSocket.OPEN) return 0;
    socket.send(UTF8ToString(messagePtr));
    return 1;
  },

  SupabaseUnity_WebSocketClose: function (id, code, reasonPtr) {
    var sockets = window.__supabaseUnitySockets || {};
    var socket = sockets[id];
    if (socket) socket.close(code, UTF8ToString(reasonPtr));
  }
});
