using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Supabase.Unity.Editor
{
    internal sealed class SupabaseSetupWindow : EditorWindow
    {
        private const string DefaultSettingsFolder = "Assets/Supabase";
        private const string DefaultSettingsPath = DefaultSettingsFolder + "/SupabaseSettings.asset";
        private const string DocumentationUrl =
            "https://github.com/DorukTan/unity-supabase/blob/v0.2.0-beta.8/Packages/com.supabaseunity.client/Documentation~/getting-started.md";
        private const string QuickstartUrl =
            "https://github.com/DorukTan/unity-supabase/tree/v0.2.0-beta.8/Packages/com.supabaseunity.client/Samples~/Quickstart";
        private const string DashboardUrl = "https://supabase.com/dashboard/projects";
        private const string PackageName = "com.supabaseunity.client";

        private SupabaseSettings settings;
        private Vector2 scrollPosition;
        private bool testingConnection;
        private string configurationNotice;
        private string connectionStatus;
        private MessageType connectionStatusType = MessageType.None;
        private UnityWebRequest activeRequest;

        private static float SectionSpacing
        {
            get { return EditorGUIUtility.standardVerticalSpacing * 4f; }
        }

        private static float PrimaryButtonHeight
        {
            get { return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing * 3f; }
        }

        [MenuItem("Tools/Supabase/Setup")]
        private static void OpenMenu()
        {
            Open(null);
        }

        internal static void Open(SupabaseSettings preferredSettings)
        {
            var window = GetWindow<SupabaseSetupWindow>("Supabase Setup");
            window.minSize = new Vector2(460f, 560f);
            if (preferredSettings != null)
                window.settings = preferredSettings;
            else
                window.FindSettings();
            window.Show();
        }

        private void OnEnable()
        {
            if (settings == null)
                FindSettings();
        }

        private void OnDisable()
        {
            if (activeRequest != null)
                activeRequest.Abort();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            var contentWidth = Mathf.Max(
                320f,
                position.width - EditorGUIUtility.singleLineHeight - SectionSpacing);
            EditorGUILayout.BeginVertical(GUILayout.Width(contentWidth));
            DrawHeader();

            EditorGUILayout.Space(SectionSpacing);
            DrawConfigurationStep();
            EditorGUILayout.Space(SectionSpacing);
            DrawConnectionStep();
            EditorGUILayout.Space(SectionSpacing);
            DrawQuickstartStep();
            EditorGUILayout.Space(SectionSpacing);
            DrawResources();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
        }

        private static void DrawHeader()
        {
            GUILayout.Label(
                "Connect the project, verify the connection, and run the Quickstart.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
            GUILayout.Label(
                "Publishable keys only. Never place secret or service-role keys in a Unity project.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawConfigurationStep()
        {
            SupabaseClientOptions options;
            string validationError;
            var configurationValid = SupabaseSettingsInspectorGui.TryValidate(
                settings, out options, out validationError);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawSectionHeader(
                "1", "Configure the client",
                settings == null ? "Not configured" : configurationValid ? "Ready" : "Needs attention");
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

            var selected = (SupabaseSettings)EditorGUILayout.ObjectField(
                "Settings asset", settings, typeof(SupabaseSettings), false);
            if (selected != settings)
            {
                settings = selected;
                configurationNotice = null;
                connectionStatus = null;
                connectionStatusType = MessageType.None;
                configurationValid = SupabaseSettingsInspectorGui.TryValidate(
                    settings, out options, out validationError);
            }

            if (settings == null)
            {
                GUILayout.Label(
                    "Create a settings asset, or select one that is already in the project.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                if (GUILayout.Button("Create Settings Asset", GUILayout.Height(PrimaryButtonHeight)))
                    CreateSettingsAsset();
                if (GUILayout.Button("Find Existing Settings"))
                {
                    FindSettings();
                    configurationNotice = settings == null
                        ? "No SupabaseSettings asset was found in this project."
                        : "Found " + AssetDatabase.GetAssetPath(settings) + ".";
                }
                if (!string.IsNullOrEmpty(configurationNotice))
                    EditorGUILayout.HelpBox(configurationNotice, MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginDisabledGroup(testingConnection);
            SupabaseSettingsInspectorGui.DrawFields(new SerializedObject(settings));
            EditorGUI.EndDisabledGroup();

            if (configurationValid)
            {
                configurationNotice = null;
                EditorGUILayout.HelpBox(
                    "Configuration looks valid. Test the connection when you are ready.", MessageType.Info);
                if (options.PersistSession)
                    EditorGUILayout.HelpBox(
                        "Session persistence uses plain-text storage by default. For stronger desktop or mobile protection, provide an ISessionStore backed by the operating system's credential storage.",
                        MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Error);
            }

            if (!string.IsNullOrEmpty(configurationNotice))
                EditorGUILayout.HelpBox(configurationNotice, MessageType.Info);
            if (GUILayout.Button("Show Settings Asset"))
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawConnectionStep()
        {
            SupabaseClientOptions options;
            string validationError;
            var configurationValid = SupabaseSettingsInspectorGui.TryValidate(
                settings, out options, out validationError);
            var connectionState = testingConnection
                ? "Testing"
                : connectionStatusType == MessageType.Error
                    ? "Check failed"
                    : !string.IsNullOrEmpty(connectionStatus)
                        ? "Connected"
                        : configurationValid ? "Ready" : "Waiting";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawSectionHeader("2", "Test the connection", connectionState);
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
            GUILayout.Label(
                "Checks the URL and publishable key against Supabase Auth. It does not test tables or RLS.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

            var canTest = configurationValid && !testingConnection;
            EditorGUI.BeginDisabledGroup(!canTest);
            if (GUILayout.Button(
                    testingConnection ? "Testing..." : "Test Project Connection",
                    GUILayout.Height(PrimaryButtonHeight)))
                TestConnectionAsync();
            EditorGUI.EndDisabledGroup();

            if (!configurationValid)
                GUILayout.Label(
                    "Finish step 1 before testing the connection.",
                    EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(connectionStatus))
                EditorGUILayout.HelpBox(connectionStatus, connectionStatusType);
            EditorGUILayout.EndVertical();
        }

        private void DrawQuickstartStep()
        {
            var quickstart = FindImportedQuickstart();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawSectionHeader("3", "Run the Quickstart", quickstart == null ? "Not imported" : "Imported");
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
            GUILayout.Label(
                quickstart == null
                    ? "Import the Quickstart sample from Package Manager, run setup.sql, then add its component to a GameObject."
                    : "Run the imported setup.sql, then add SupabaseQuickstart to a GameObject and assign the settings asset.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

            if (quickstart != null)
            {
                if (GUILayout.Button("Show Imported Quickstart", GUILayout.Height(PrimaryButtonHeight)))
                {
                    Selection.activeObject = quickstart;
                    EditorGUIUtility.PingObject(quickstart);
                }
            }
            else if (GUILayout.Button("Open Package Manager", GUILayout.Height(PrimaryButtonHeight)))
            {
                UnityEditor.PackageManager.UI.Window.Open(PackageName);
            }
            if (GUILayout.Button("Read the Quickstart Guide"))
                Application.OpenURL(QuickstartUrl);
            EditorGUILayout.EndVertical();
        }

        private static void DrawResources()
        {
            EditorGUILayout.LabelField("Resources", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Dashboard", EditorStyles.miniButtonLeft))
                Application.OpenURL(DashboardUrl);
            if (GUILayout.Button("Documentation", EditorStyles.miniButtonMid))
                Application.OpenURL(DocumentationUrl);
            if (GUILayout.Button("Model Generator", EditorStyles.miniButtonRight))
                SupabaseModelGeneratorWindow.Open();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawSectionHeader(string number, string title, string status)
        {
            GUILayout.Label(
                number + ". " + title + "  -  " + status,
                EditorStyles.boldLabel);
        }

        private void CreateSettingsAsset()
        {
            EnsureAssetFolder(DefaultSettingsFolder);
            var path = AssetDatabase.GenerateUniqueAssetPath(DefaultSettingsPath);
            settings = CreateInstance<SupabaseSettings>();
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
            configurationNotice = "Created " + path + ". Add the Project URL and publishable key from the Supabase Dashboard Connect dialog.";
            connectionStatus = null;
            connectionStatusType = MessageType.None;
        }

        private void FindSettings()
        {
            var selectedSettings = Selection.activeObject as SupabaseSettings;
            if (selectedSettings != null)
            {
                settings = selectedSettings;
                return;
            }

            var guids = AssetDatabase.FindAssets("t:SupabaseSettings");
            var paths = new string[guids.Length];
            for (var index = 0; index < guids.Length; index++)
                paths[index] = AssetDatabase.GUIDToAssetPath(guids[index]);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            settings = paths.Length == 0
                ? null
                : AssetDatabase.LoadAssetAtPath<SupabaseSettings>(paths[0]);
        }

        private static UnityEngine.Object FindImportedQuickstart()
        {
            var guids = AssetDatabase.FindAssets("SupabaseQuickstart t:MonoScript", new[] { "Assets" });
            if (guids.Length == 0)
                return null;
            Array.Sort(guids, StringComparer.Ordinal);
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private async void TestConnectionAsync()
        {
            testingConnection = true;
            connectionStatus = "Connecting...";
            connectionStatusType = MessageType.None;
            Repaint();

            try
            {
                using (var request = CreateConnectionRequest(settings.ToOptions()))
                {
                    activeRequest = request;
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Delay(25);

#if UNITY_2020_2_OR_NEWER
                    var succeeded = request.result == UnityWebRequest.Result.Success;
#else
                    var succeeded = !request.isNetworkError && !request.isHttpError;
#endif
                    if (succeeded)
                    {
                        connectionStatus = "Connected successfully. The project URL and client key were accepted.";
                        connectionStatusType = MessageType.Info;
                    }
                    else
                    {
                        connectionStatus = FormatConnectionFailure(request.responseCode, request.error);
                        connectionStatusType = MessageType.Error;
                    }
                }
            }
            catch (Exception exception)
            {
                connectionStatus = "Connection test failed: " + SupabaseHttp.Redact(exception.Message);
                connectionStatusType = MessageType.Error;
            }
            finally
            {
                activeRequest = null;
                testingConnection = false;
                if (this != null)
                    Repaint();
            }
        }

        internal static UnityWebRequest CreateConnectionRequest(SupabaseClientOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            var endpoints = options.ValidateAndResolve();
            var request = UnityWebRequest.Get(endpoints.Auth.AbsoluteUri.TrimEnd('/') + "/settings");
            request.timeout = 15;
            request.redirectLimit = 0;
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("apikey", options.PublishableKey);
            request.SetRequestHeader("X-Client-Info", SupabaseHttp.ClientInfo);
            return request;
        }

        internal static string FormatConnectionFailure(long statusCode, string requestError)
        {
            if (statusCode == 401 || statusCode == 403)
                return "Supabase rejected the connection (HTTP " + statusCode +
                       "). Copy the Project URL and publishable key again from the Dashboard Connect dialog.";
            if (statusCode > 0)
                return "Supabase returned HTTP " + statusCode +
                       ". The project is reachable; check its status and Auth configuration.";
            return "Could not reach the project. Check the URL, internet connection, proxy, and firewall. " +
                   SupabaseHttp.Redact(requestError ?? string.Empty);
        }

        private static void EnsureAssetFolder(string folder)
        {
            var segments = folder.Split('/');
            var current = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[index]);
                current = next;
            }
        }
    }
}
