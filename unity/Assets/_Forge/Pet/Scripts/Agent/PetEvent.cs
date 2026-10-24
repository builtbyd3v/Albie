using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// One WebSocket message: { "type": "...", "payload": { ... } }. Payload is never null.
    /// Also hosts the outbound builder <see cref="Build"/> and the inbound <see cref="Parse"/>.
    /// </summary>
    public class PetEvent
    {
        public string type;
        public JObject payload; // never shown in the inspector; built from JSON at runtime

        public PetEvent() { payload = new JObject(); }
        public PetEvent(string type, JObject payload) { this.type = type; this.payload = payload ?? new JObject(); }

        // ---- typed getters (missing / wrong type → default) ----
        public JToken Get(string key) => payload != null ? payload[key] : null;
        public bool Has(string key) => Get(key) != null && Get(key).Type != JTokenType.Null;

        public string GetString(string key, string fallback = "")
        {
            var t = Get(key);
            return t == null || t.Type == JTokenType.Null ? fallback : t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        public int GetInt(string key, int fallback = 0)
        {
            var t = Get(key);
            return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? t.Value<int>() : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            var t = Get(key);
            return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? t.Value<float>() : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            var t = Get(key);
            return t != null && t.Type == JTokenType.Boolean ? t.Value<bool>() : fallback;
        }

        public JObject GetObject(string key) => Get(key) as JObject;
        public JArray GetArray(string key) => Get(key) as JArray;

        // ---- serialization ----

        /// <summary>Builds an outbound text frame. <paramref name="payload"/> may be a JObject, a POCO or null.</summary>
        public static string Build(string type, object payload)
        {
            JToken p = payload as JToken ?? (payload != null ? JToken.FromObject(payload) : new JObject());
            var msg = new JObject { ["type"] = type, ["payload"] = p };
            return msg.ToString(Formatting.None);
        }

        /// <summary>Parses an inbound text frame. Returns null (and warns) on malformed JSON or missing type.</summary>
        public static PetEvent Parse(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var type = (string)obj["type"];
                if (string.IsNullOrEmpty(type)) { Debug.LogWarning("[PetEvent] Message without type: " + json); return null; }
                return new PetEvent(type, obj["payload"] as JObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PetEvent] Bad JSON (" + e.Message + "): " + json);
                return null;
            }
        }

        public override string ToString() => Build(type, payload);
    }
}
