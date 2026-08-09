using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Supabase.Unity.Editor;
using UnityEngine;

namespace Supabase.Unity.Tests
{
    public sealed class ModelGeneratorTests
    {
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
    }
}
