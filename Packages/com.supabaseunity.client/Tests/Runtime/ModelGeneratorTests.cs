using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Supabase.Unity.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace Supabase.Unity.Tests
{
    public sealed class ModelGeneratorTests
    {
        [TestCase(PlayModeStateChange.ExitingEditMode)]
        [TestCase(PlayModeStateChange.ExitingPlayMode)]
        public void ModelGeneration_PlayModeTransitionCancelsOwnedWork(PlayModeStateChange state)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var window = ScriptableObject.CreateInstance<SupabaseModelGeneratorWindow>();
                try
                {
                    Set(window, "generationCancellation", cancellation);
                    Set(window, "secretApiKey", "sb_secret_editor-test-only");
                    typeof(SupabaseModelGeneratorWindow).GetMethod("OnPlayModeStateChanged",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { state });
                    Assert.IsTrue(cancellation.IsCancellationRequested);
                    Assert.AreEqual(string.Empty, Get<string>(window, "secretApiKey"));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
        }

        [UnityTest]
        public IEnumerator SetupConnection_EditingSettingsClearsSuccess()
        {
            using (var fixture = new LoopbackSupabaseFixture(delegate { return ContractHttpResponse.Json(200, "{}"); }))
            {
                var settings = Settings(fixture.ProjectUrl.AbsoluteUri);
                var window = ScriptableObject.CreateInstance<SupabaseSetupWindow>();
                try
                {
                    Set(window, "settings", settings);
                    var task = window.TestConnectionAsync();
                    yield return Await(task);
                    Assert.AreEqual(MessageType.Info, Get<MessageType>(window, "connectionStatusType"));
                    StringAssert.Contains("Connected", Get<string>(window, "connectionStatus"));
                    Set(settings, "publishableKey", "sb_publishable_changed");
                    window.SynchronizeConnectionStatus();
                    Assert.IsNull(Get<string>(window, "connectionStatus"));
                    Assert.AreEqual(MessageType.None, Get<MessageType>(window, "connectionStatusType"));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(window);
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [UnityTest]
        public IEnumerator SetupConnection_ChangedConfigurationRejectsPendingResult()
        {
            using (var release = new ManualResetEventSlim())
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                release.Wait(TimeSpan.FromSeconds(5));
                return ContractHttpResponse.Json(200, "{}");
            }))
            {
                var settings = Settings(fixture.ProjectUrl.AbsoluteUri);
                var replacement = Settings(fixture.ProjectUrl.AbsoluteUri);
                var window = ScriptableObject.CreateInstance<SupabaseSetupWindow>();
                try
                {
                    Set(window, "settings", settings);
                    var task = window.TestConnectionAsync();
                    yield return Received(fixture);
                    Set(window, "settings", replacement);
                    window.SynchronizeConnectionStatus();
                    release.Set();
                    yield return Await(task);
                    Assert.IsNull(Get<string>(window, "connectionStatus"));
                    Assert.IsFalse(Get<bool>(window, "testingConnection"));
                    Assert.IsNull(Get<UnityWebRequest>(window, "activeRequest"));
                }
                finally
                {
                    release.Set();
                    UnityEngine.Object.DestroyImmediate(window);
                    UnityEngine.Object.DestroyImmediate(settings);
                    UnityEngine.Object.DestroyImmediate(replacement);
                }
            }
        }

        [UnityTest]
        public IEnumerator ModelGeneration_ClosingWindowCancelsWithoutWritingFiles()
        {
            using (var release = new ManualResetEventSlim())
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                release.Wait(TimeSpan.FromSeconds(5));
                return SchemaResponse();
            }))
            {
                var window = Generator(fixture);
                var folder = Get<string>(window, "outputFolder");
                try
                {
                    var task = window.GenerateModelsAsync();
                    yield return Received(fixture);
                    UnityEngine.Object.DestroyImmediate(window);
                    release.Set();
                    yield return Await(task);
                    Assert.IsFalse(Directory.Exists(folder));
                    Assert.IsFalse(Get<bool>(window, "generating"));
                    Assert.IsNull(Get<UnityWebRequest>(window, "activeRequest"));
                    Assert.AreEqual(string.Empty, Get<string>(window, "secretApiKey"));
                }
                finally
                {
                    release.Set();
                    if (window != null) UnityEngine.Object.DestroyImmediate(window);
                    RemoveTestFolder(folder);
                }
            }
        }

        [UnityTest]
        public IEnumerator ModelGeneration_CancelAllowsAnotherOperation()
        {
            using (var fixture = new LoopbackSupabaseFixture(delegate { return SchemaResponse(); }))
            {
                var window = Generator(fixture);
                var folder = Get<string>(window, "outputFolder");
                try
                {
                    var cancelled = window.GenerateModelsAsync();
                    window.CancelGeneration();
                    yield return Await(cancelled);
                    Assert.AreEqual("Cancelled.", Get<string>(window, "status"));
                    Assert.IsFalse(Directory.Exists(folder));
                    Set(window, "secretApiKey", "sb_secret_editor-test-only");
                    yield return Await(window.GenerateModelsAsync());
                    Assert.IsTrue(File.Exists(Path.Combine(folder, "Scores.cs")));
                    Assert.IsFalse(Get<bool>(window, "generating"));
                    StringAssert.StartsWith("Generated 1", Get<string>(window, "status"));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(window);
                    RemoveTestFolder(folder);
                }
            }
        }

        [UnityTest]
        public IEnumerator ModelGeneration_CapturesDestinationBeforeRequest()
        {
            using (var release = new ManualResetEventSlim())
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                release.Wait(TimeSpan.FromSeconds(5));
                return SchemaResponse();
            }))
            {
                var window = Generator(fixture);
                var folder = Get<string>(window, "outputFolder");
                var changedFolder = folder + "Changed~";
                try
                {
                    var task = window.GenerateModelsAsync();
                    yield return Received(fixture);
                    Set(window, "outputFolder", changedFolder);
                    Set(window, "generatedNamespace", "Changed.Namespace");
                    release.Set();
                    yield return Await(task);
                    var source = File.ReadAllText(Path.Combine(folder, "Scores.cs"));
                    StringAssert.Contains("namespace Game.Database", source);
                    Assert.IsFalse(Directory.Exists(changedFolder));
                }
                finally
                {
                    release.Set();
                    UnityEngine.Object.DestroyImmediate(window);
                    RemoveTestFolder(folder);
                    RemoveTestFolder(changedFolder);
                }
            }
        }

        private static SupabaseModelGeneratorWindow Generator(LoopbackSupabaseFixture fixture)
        {
            var window = ScriptableObject.CreateInstance<SupabaseModelGeneratorWindow>();
            Set(window, "projectUrl", fixture.ProjectUrl.AbsoluteUri);
            Set(window, "allowCustomSchemaHost", true);
            Set(window, "secretApiKey", "sb_secret_editor-test-only");
            Set(window, "outputFolder", "Assets/__SupabaseGeneratorTest_" + Guid.NewGuid().ToString("N") + "~");
            return window;
        }

        private static ContractHttpResponse SchemaResponse()
        {
            return ContractHttpResponse.Json(200,
                "{\"definitions\":{\"scores\":{\"properties\":{\"id\":{\"type\":\"integer\",\"format\":\"int64\"}}}}}");
        }

        private static void RemoveTestFolder(string folder)
        {
            var path = Path.GetFullPath(folder);
            var prefix = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar + "__SupabaseGeneratorTest_";
            Assert.IsTrue(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static SupabaseSettings Settings(string url)
        {
            var settings = ScriptableObject.CreateInstance<SupabaseSettings>();
            Set(settings, "projectUrl", url);
            Set(settings, "publishableKey", "sb_publishable_editor-test");
            return settings;
        }

        private static IEnumerator Received(LoopbackSupabaseFixture fixture)
        {
            var deadline = Time.realtimeSinceStartup + 5;
            while (fixture.Requests.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.Greater(fixture.Requests.Count, 0, "The loopback request did not arrive.");
        }

        private static IEnumerator Await(Task task)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "The Editor operation did not finish.");
            if (task.IsFaulted) throw task.Exception;
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static T Get<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        [Test]
        public void OutputFolder_RejectsTraversalOutsideAssets()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                SupabaseModelGeneratorWindow.ResolveOutputFolder("Assets/../../outside-project");
            });
        }

        [Test]
        public void OutputFolder_AcceptsFolderInsideAssets()
        {
            var result = SupabaseModelGeneratorWindow.ResolveOutputFolder("Assets/Supabase/Generated");
            var assets = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            StringAssert.StartsWith(assets + Path.DirectorySeparatorChar, result);
        }

        [Test]
        public void Namespace_RejectsInjectedOrInvalidCode()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                SupabaseModelGeneratorWindow.ValidateNamespace("Game.Database;System.Console.WriteLine(1)");
            });
            Assert.Throws<InvalidOperationException>(delegate
            {
                SupabaseModelGeneratorWindow.ValidateNamespace("Game.namespace");
            });
        }

        [Test]
        public void SchemaEndpoint_RejectsUnapprovedSecretHost()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                SupabaseModelGeneratorWindow.ResolveDatabaseEndpoint("https://attacker.example.com");
            });
            Assert.DoesNotThrow(delegate
            {
                SupabaseModelGeneratorWindow.ResolveDatabaseEndpoint("https://project.supabase.co");
            });
            Assert.DoesNotThrow(delegate
            {
                SupabaseModelGeneratorWindow.ResolveDatabaseEndpoint(
                    "https://self-hosted.example.com", allowCustomHost: true);
            });
        }

        [Test]
        public void GeneratedModel_IsPreservedFromManagedStripping()
        {
            // Generated models live in the consumer's assembly, which the package's own
            // link.xml does not cover. Without [Preserve] their properties are stripped
            // under IL2CPP and Json.NET silently hydrates nothing.
            var definition = JObject.Parse(
                "{\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"integer\",\"format\":\"int64\"}}}");

            var generated = SupabaseModelGeneratorWindow.GenerateModel(
                "scores", "ScoreRow", definition, "Game.Database");

            StringAssert.Contains("using UnityEngine.Scripting;", generated);
            StringAssert.Contains("[Preserve]", generated);
            StringAssert.Contains("[SupabaseTable(\"scores\")]", generated);
        }

        [Test]
        public void SetupConnectionRequest_UsesClientSafeAuthEndpoint()
        {
            var options = ConfigurationTests.ValidOptions();

            using (var request = SupabaseSetupWindow.CreateConnectionRequest(options))
            {
                Assert.AreEqual("https://example.supabase.co/auth/v1/settings", request.url);
                Assert.AreEqual("sb_publishable_test-value", request.GetRequestHeader("apikey"));
                Assert.AreEqual("application/json", request.GetRequestHeader("Accept"));
                Assert.IsNull(request.GetRequestHeader("Authorization"));
                Assert.AreEqual(0, request.redirectLimit);
                Assert.AreEqual(15, request.timeout);
            }
        }

        [Test]
        public void SetupConnectionFailure_GivesActionableCredentialGuidance()
        {
            var message = SupabaseSetupWindow.FormatConnectionFailure(401, "Unauthorized");

            StringAssert.Contains("Project URL", message);
            StringAssert.Contains("publishable key", message);
            StringAssert.Contains("Connect dialog", message);
        }
    }
}
