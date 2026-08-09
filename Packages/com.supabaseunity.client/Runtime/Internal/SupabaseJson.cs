using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Supabase.Unity
{
    internal static class SupabaseJson
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset,
            ContractResolver = new SupabaseContractResolver()
        };

        internal static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        internal static T Deserialize<T>(string value)
        {
            return JsonConvert.DeserializeObject<T>(value, Settings);
        }

        private sealed class SupabaseContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);
                var column = member.GetCustomAttribute<SupabaseColumnAttribute>();
                if (column != null && !string.IsNullOrWhiteSpace(column.Name))
                    property.PropertyName = column.Name;
                return property;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class SupabaseTableAttribute : Attribute
    {
        public string Name { get; private set; }

        public SupabaseTableAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Table name cannot be empty.", "name");
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class SupabaseColumnAttribute : Attribute
    {
        public string Name { get; private set; }

        public SupabaseColumnAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Column name cannot be empty.", "name");
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class SupabasePrimaryKeyAttribute : Attribute
    {
    }
}
