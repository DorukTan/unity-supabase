using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Supabase.Unity.Editor
{
    [CustomEditor(typeof(SupabaseSettings))]
    internal sealed class SupabaseSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("projectUrl"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("publishableKey"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultSchema"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("persistSession"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoRefreshToken"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoConnectRealtime"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("httpTimeoutSeconds"));
            serializedObject.ApplyModifiedProperties();

            var settings = (SupabaseSettings)target;
            try
            {
                var options = settings.ToOptions();
                options.ValidateAndResolve();
                EditorGUILayout.HelpBox("Client-safe Supabase configuration is valid. RLS policies are still required.",
                    MessageType.Info);
                if (options.PersistSession)
                    EditorGUILayout.HelpBox(
                        "Session persistence writes the refresh token as plain text to " +
                        "Application.persistentDataPath. On Android and iOS that location is " +
                        "app-private. On Windows, macOS, and Linux it is readable by any process " +
                        "running as the same user, and the token stays usable until it is revoked. " +
                        "Assign a custom ISessionStore backed by Keychain, Keystore, or another " +
                        "OS-protected credential store if your game needs more than that.",
                        MessageType.Warning);
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Error);
            }
        }
    }

    internal sealed class SupabaseBuildValidator : IPreprocessBuildWithReport
    {
        private static readonly Regex SecretKeyPattern = new Regex(
            @"sb_secret_[A-Za-z0-9_-]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex JwtPattern = new Regex(
            @"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            RegexOptions.CultureInvariant);
        private static readonly string[] TextAssetExtensions =
        {
            ".asset", ".cs", ".json", ".prefab", ".txt", ".unity", ".uss", ".uxml", ".xml", ".yaml", ".yml"
        };

        public int callbackOrder { get { return -1000; } }

        public void OnPreprocessBuild(BuildReport report)
        {
            var issues = ValidateAllSettings();
            if (!string.IsNullOrEmpty(issues))
                throw new BuildFailedException("Supabase configuration validation failed:\n" + issues);
        }

        [MenuItem("Tools/Supabase/Validate Client Configuration")]
        private static void ValidateMenu()
        {
            var issues = ValidateAllSettings();
            if (string.IsNullOrEmpty(issues))
                EditorUtility.DisplayDialog("Supabase", "All SupabaseSettings assets use client-safe keys.", "OK");
            else
                EditorUtility.DisplayDialog("Supabase validation failed", issues, "OK");
        }

        private static string ValidateAllSettings()
        {
            var issues = string.Empty;
            foreach (var guid in AssetDatabase.FindAssets("t:SupabaseSettings"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<SupabaseSettings>(path);
                try
                {
                    settings.ToOptions().ValidateAndResolve();
                }
                catch (Exception exception)
                {
                    issues += path + ": " + exception.Message + "\n";
                }
            }
            issues += ValidateNoPrivateCredentialsInAssets();
            return issues.TrimEnd();
        }

        private static string ValidateNoPrivateCredentialsInAssets()
        {
            var issues = string.Empty;
            foreach (var assetPath in AssetDatabase.GetAllAssetPaths())
            {
                if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    !IsTextAsset(assetPath))
                    continue;
                var absolutePath = Path.GetFullPath(assetPath);
                if (!File.Exists(absolutePath))
                    continue;
                string text;
                try { text = File.ReadAllText(absolutePath); }
                catch { continue; }

                if (SecretKeyPattern.IsMatch(text))
                    issues += assetPath + ": contains an sb_secret key. Remove and rotate it before building.\n";
                foreach (Match match in JwtPattern.Matches(text))
                {
                    string role;
                    if (!SupabaseKeyValidator.TryGetJwtRole(match.Value, out role) ||
                        string.Equals(role, "anon", StringComparison.OrdinalIgnoreCase))
                        continue;
                    issues += assetPath + ": contains a non-anon JWT (role " + role +
                              "). User and elevated JWTs must not be embedded in a player build.\n";
                    break;
                }
            }
            return issues;
        }

        private static bool IsTextAsset(string path)
        {
            var extension = Path.GetExtension(path);
            foreach (var allowed in TextAssetExtensions)
                if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
