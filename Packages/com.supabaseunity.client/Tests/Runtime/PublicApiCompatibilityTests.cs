using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Supabase.Unity.Tests
{
    public sealed class PublicApiCompatibilityTests
    {
        private const string BaselineAssetPath =
            "Packages/com.supabaseunity.client/Tests/Runtime/PublicApiSurface.txt";
        private const string SnapshotOutputVariable = "SUPABASE_PUBLIC_API_OUTPUT";

        [Test]
        public void RuntimeAssembly_MatchesApprovedPublicApi()
        {
            var actual = PublicApiSnapshot.Capture(typeof(SupabaseClient).Assembly);
            var outputPath = Environment.GetEnvironmentVariable(SnapshotOutputVariable);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.GetFullPath(outputPath);
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(outputPath, actual, new UTF8Encoding(false));
                Assert.Pass("Wrote the public API candidate to " + outputPath);
            }

            var baseline = AssetDatabase.LoadAssetAtPath<TextAsset>(BaselineAssetPath);
            Assert.IsNotNull(baseline, "The approved public API baseline is missing.");
            var expected = PublicApiSnapshot.Normalize(baseline.text);
            Assert.AreEqual(expected, actual,
                "The public API changed. Review the signature diff before updating the baseline.");
        }
    }

    internal static class PublicApiSnapshot
    {
        private const BindingFlags PublicDeclaredMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        internal static string Capture(Assembly assembly)
        {
            var builder = new StringBuilder();
            var types = assembly.GetTypes()
                .Where(type => type.IsPublic || type.IsNestedPublic)
                .OrderBy(FormatType, StringComparer.Ordinal)
                .ToArray();

            foreach (var type in types)
            {
                AppendType(builder, type);
                builder.AppendLine();
            }

            return Normalize(builder.ToString());
        }

        internal static string Normalize(string value)
        {
            return value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";
        }

        private static void AppendType(StringBuilder builder, Type type)
        {
            if (type.IsEnum)
            {
                AppendEnum(builder, type);
                return;
            }

            if (typeof(MulticastDelegate).IsAssignableFrom(type.BaseType))
            {
                AppendDelegate(builder, type);
                return;
            }

            var declaration = new StringBuilder("public ");
            if (type.IsClass && type.IsAbstract && type.IsSealed)
                declaration.Append("static ");
            else
            {
                if (type.IsClass && type.IsAbstract)
                    declaration.Append("abstract ");
                if (type.IsClass && type.IsSealed)
                    declaration.Append("sealed ");
            }

            declaration.Append(type.IsInterface ? "interface " : type.IsValueType ? "struct " : "class ");
            declaration.Append(FormatType(type));

            var contracts = new List<string>();
            if (type.BaseType != null && type.BaseType != typeof(object) &&
                type.BaseType != typeof(ValueType))
            {
                contracts.Add(FormatType(type.BaseType));
            }
            contracts.AddRange(GetDirectInterfaces(type).Select(FormatType));
            if (contracts.Count > 0)
            {
                declaration.Append(" : ");
                declaration.Append(string.Join(", ", contracts.Distinct().OrderBy(
                    value => value, StringComparer.Ordinal).ToArray()));
            }

            var attributeUsage = FormatAttributeUsage(type);
            if (!string.IsNullOrEmpty(attributeUsage))
                builder.AppendLine(attributeUsage);
            builder.AppendLine(declaration.ToString());
            AppendGenericConstraints(builder, type.GetGenericArguments(), "  ");

            var members = new List<string>();
            members.AddRange(type.GetConstructors(PublicDeclaredMembers).Select(FormatConstructor));
            members.AddRange(type.GetFields(PublicDeclaredMembers).Select(FormatField));
            members.AddRange(type.GetProperties(PublicDeclaredMembers).Select(FormatProperty));
            members.AddRange(type.GetEvents(PublicDeclaredMembers).Select(FormatEvent));
            members.AddRange(type.GetMethods(PublicDeclaredMembers)
                .Where(method => !method.IsSpecialName || method.Name.StartsWith(
                    "op_", StringComparison.Ordinal))
                .Select(FormatMethod));

            foreach (var member in members.OrderBy(value => value, StringComparer.Ordinal))
                builder.Append("  ").AppendLine(member);
        }

        private static void AppendEnum(StringBuilder builder, Type type)
        {
            var flags = type.IsDefined(typeof(FlagsAttribute), false) ? "flags " : string.Empty;
            builder.Append("public ").Append(flags).Append("enum ").Append(FormatType(type))
                .Append(" : ").AppendLine(FormatType(Enum.GetUnderlyingType(type)));
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                builder.Append("  ").Append(field.Name).Append(" = ")
                    .AppendLine(FormatConstant(
                        field.GetRawConstantValue(), Enum.GetUnderlyingType(type)));
            }
        }

        private static void AppendDelegate(StringBuilder builder, Type type)
        {
            var invoke = type.GetMethod("Invoke", PublicDeclaredMembers);
            if (invoke == null)
                throw new InvalidOperationException("Public delegate has no Invoke method: " + type);
            builder.Append("public delegate ").Append(FormatType(invoke.ReturnType)).Append(' ')
                .Append(FormatType(type)).Append('(')
                .Append(string.Join(", ", invoke.GetParameters().Select(FormatParameter).ToArray()))
                .AppendLine(")");
            AppendGenericConstraints(builder, type.GetGenericArguments(), "  ");
        }

        private static string FormatConstructor(ConstructorInfo constructor)
        {
            return FormatObsolete(constructor) + "constructor " +
                FormatType(constructor.DeclaringType) + "(" +
                string.Join(", ", constructor.GetParameters().Select(FormatParameter).ToArray()) +
                ")";
        }

        private static string FormatField(FieldInfo field)
        {
            var modifiers = field.IsLiteral ? "const " : field.IsStatic ? "static " : string.Empty;
            if (field.IsInitOnly)
                modifiers += "readonly ";
            var result = FormatObsolete(field) + "field " + modifiers + FormatType(field.FieldType) +
                " " + field.Name;
            if (field.IsLiteral)
                result += " = " + FormatConstant(field.GetRawConstantValue(), field.FieldType);
            return result;
        }

        private static string FormatProperty(PropertyInfo property)
        {
            var getter = property.GetGetMethod(false);
            var setter = property.GetSetMethod(false);
            var accessor = getter ?? setter;
            var modifiers = accessor != null && accessor.IsStatic ? "static " : string.Empty;
            var indexes = property.GetIndexParameters();
            var name = indexes.Length == 0
                ? property.Name
                : "this[" + string.Join(", ", indexes.Select(FormatParameter).ToArray()) + "]";
            var accessors = new List<string>();
            if (getter != null)
                accessors.Add("get;");
            if (setter != null)
                accessors.Add("set;");
            return FormatObsolete(property) + "property " + modifiers + FormatType(property.PropertyType) +
                " " + name + " { " + string.Join(" ", accessors.ToArray()) + " }";
        }

        private static string FormatEvent(EventInfo eventInfo)
        {
            var add = eventInfo.GetAddMethod(false);
            var modifiers = add != null && add.IsStatic ? "static " : string.Empty;
            return FormatObsolete(eventInfo) + "event " + modifiers +
                FormatType(eventInfo.EventHandlerType) + " " + eventInfo.Name;
        }

        private static string FormatMethod(MethodInfo method)
        {
            var modifiers = new List<string>();
            if (method.IsDefined(typeof(ExtensionAttribute), false))
                modifiers.Add("extension");
            if (method.IsStatic)
                modifiers.Add("static");
            if (method.IsAbstract)
                modifiers.Add("abstract");
            else if (method.IsVirtual)
            {
                var baseDefinition = method.GetBaseDefinition();
                if (baseDefinition != method)
                    modifiers.Add(method.IsFinal ? "sealed override" : "override");
                else if (!method.IsFinal)
                    modifiers.Add("virtual");
            }

            var prefix = modifiers.Count == 0 ? string.Empty : string.Join(" ", modifiers.ToArray()) + " ";
            var name = method.Name;
            if (method.IsGenericMethodDefinition)
            {
                name += "<" + string.Join(", ", method.GetGenericArguments()
                    .Select(FormatGenericParameterDeclaration).ToArray()) + ">";
            }
            var result = FormatObsolete(method) + "method " + prefix + FormatType(method.ReturnType) +
                " " + name + "(" +
                string.Join(", ", method.GetParameters().Select(FormatParameter).ToArray()) + ")";
            var constraints = FormatGenericConstraints(method.GetGenericArguments());
            if (constraints.Count > 0)
                result += " " + string.Join(" ", constraints.ToArray());
            return result;
        }

        private static string FormatParameter(ParameterInfo parameter)
        {
            var prefix = string.Empty;
            var type = parameter.ParameterType;
            if (parameter.IsDefined(typeof(ParamArrayAttribute), false))
                prefix = "params ";
            else if (type.IsByRef)
            {
                prefix = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
                type = type.GetElementType();
            }

            var result = prefix + FormatType(type) + " " + parameter.Name;
            if (parameter.IsOptional || parameter.HasDefaultValue)
                result += " = " + FormatConstant(parameter.DefaultValue, type);
            return result;
        }

        private static string FormatType(Type type)
        {
            if (type == null)
                return "<null>";
            if (type.IsByRef)
                return FormatType(type.GetElementType()) + "&";
            if (type.IsPointer)
                return FormatType(type.GetElementType()) + "*";
            if (type.IsArray)
                return FormatType(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter)
                return type.Name;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return FormatType(type.GetGenericArguments()[0]) + "?";

            var name = type.IsNested
                ? FormatType(type.DeclaringType) + "." + TrimGenericArity(type.Name)
                : (string.IsNullOrEmpty(type.Namespace) ? string.Empty : type.Namespace + ".") +
                  TrimGenericArity(type.Name);
            if (!type.IsGenericType)
                return name;

            var arguments = type.GetGenericArguments();
            var formattedArguments = type.IsGenericTypeDefinition
                ? arguments.Select(FormatGenericParameterDeclaration)
                : arguments.Select(FormatType);
            return name + "<" + string.Join(", ", formattedArguments.ToArray()) + ">";
        }

        private static string FormatGenericParameterDeclaration(Type parameter)
        {
            var variance = parameter.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
            if (variance == GenericParameterAttributes.Covariant)
                return "out " + parameter.Name;
            if (variance == GenericParameterAttributes.Contravariant)
                return "in " + parameter.Name;
            return parameter.Name;
        }

        private static IEnumerable<Type> GetDirectInterfaces(Type type)
        {
            var inherited = new HashSet<Type>();
            if (type.BaseType != null)
            {
                foreach (var interfaceType in type.BaseType.GetInterfaces())
                    inherited.Add(interfaceType);
            }
            foreach (var interfaceType in type.GetInterfaces())
            {
                foreach (var parent in interfaceType.GetInterfaces())
                    inherited.Add(parent);
            }
            return type.GetInterfaces().Where(interfaceType => !inherited.Contains(interfaceType));
        }

        private static void AppendGenericConstraints(
            StringBuilder builder, IEnumerable<Type> parameters, string indentation)
        {
            foreach (var constraint in FormatGenericConstraints(parameters))
                builder.Append(indentation).AppendLine(constraint);
        }

        private static List<string> FormatGenericConstraints(IEnumerable<Type> parameters)
        {
            var results = new List<string>();
            foreach (var parameter in parameters.Where(item => item.IsGenericParameter))
            {
                var constraints = new List<string>();
                var attributes = parameter.GenericParameterAttributes &
                                 GenericParameterAttributes.SpecialConstraintMask;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    constraints.Add("class");
                if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                    constraints.Add("struct");
                constraints.AddRange(parameter.GetGenericParameterConstraints()
                    .Where(type => type != typeof(ValueType)).Select(FormatType));
                if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                    !constraints.Contains("struct"))
                {
                    constraints.Add("new()");
                }
                if (constraints.Count > 0)
                {
                    results.Add("where " + parameter.Name + " : " + string.Join(", ",
                        constraints.Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray()));
                }
            }
            return results;
        }

        private static string FormatConstant(object value, Type declaredType)
        {
            if (value == null || value == DBNull.Value || value == Missing.Value)
            {
                return declaredType != null && declaredType.IsValueType &&
                       Nullable.GetUnderlyingType(declaredType) == null
                    ? "default(" + FormatType(declaredType) + ")"
                    : "null";
            }
            if (declaredType != null && declaredType.IsEnum)
            {
                var name = Enum.GetName(declaredType, value);
                return name == null
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : FormatType(declaredType) + "." + name;
            }
            if (value is string)
            {
                return "\"" + ((string)value).Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            }
            if (value is char)
                return "'" + value.ToString().Replace("'", "\\'") + "'";
            if (value is bool)
                return (bool)value ? "true" : "false";
            if (value is float)
                return ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f";
            if (value is double)
                return ((double)value).ToString("R", CultureInfo.InvariantCulture) + "d";
            if (value is decimal)
                return ((decimal)value).ToString(CultureInfo.InvariantCulture) + "m";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string FormatAttributeUsage(Type type)
        {
            var usage = type.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
                .Cast<AttributeUsageAttribute>().FirstOrDefault();
            if (usage == null)
                return string.Empty;
            return "[attribute-usage targets=" + usage.ValidOn + " allow-multiple=" +
                   usage.AllowMultiple.ToString().ToLowerInvariant() + " inherited=" +
                   usage.Inherited.ToString().ToLowerInvariant() + "]";
        }

        private static string FormatObsolete(MemberInfo member)
        {
            var obsolete = member.GetCustomAttributes(typeof(ObsoleteAttribute), false)
                .Cast<ObsoleteAttribute>().FirstOrDefault();
            if (obsolete == null)
                return string.Empty;
            return "obsolete(message=" + FormatConstant(obsolete.Message, typeof(string)) +
                   ", error=" + obsolete.IsError.ToString().ToLowerInvariant() + ") ";
        }

        private static string TrimGenericArity(string name)
        {
            var marker = name.IndexOf('`');
            return marker < 0 ? name : name.Substring(0, marker);
        }
    }
}
