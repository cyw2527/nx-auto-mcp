using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Protocol
{
    /// <summary>
    /// JSON-Line protocol encoder/decoder
    /// One complete JSON message per line, terminated by \n
    /// </summary>
    public static class JsonProtocol
    {
        public static string Serialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj, new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.None
            });
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });
        }

        /// <summary>
        /// Extract the "method" field from a JSON string
        /// </summary>
        public static string ExtractMethod(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj["method"] != null ? obj["method"].Value<string>() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract the "id" field from a JSON string
        /// </summary>
        public static int ExtractId(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj["id"] != null ? obj["id"].Value<int>() : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Extract the "params" field (if present)
        /// </summary>
        public static JToken ExtractParams(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj["params"];
            }
            catch
            {
                return null;
            }
        }
    }
}
