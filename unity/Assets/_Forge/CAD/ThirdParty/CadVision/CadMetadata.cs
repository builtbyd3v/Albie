using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADVision
{
    /// <summary>Export schemas 1.0 and 2.1; retains unknown engineering fields.</summary>
    public sealed class CadMetadata
    {
        private readonly JObject document;
        private readonly Dictionary<string, JObject> objects;
        private readonly Dictionary<string, int> nodeIndices;
        private readonly Dictionary<string, string> parents;

        public string RawJson { get; }
        public bool HasVerifiedNodeMapping { get; }
        public string RootId { get; }
        public IReadOnlyDictionary<string, int> NodeIndices { get; }
        // Return copies so consumers cannot invalidate the validated registry contract.
        public JObject Document => (JObject)document.DeepClone();
        public JObject Project => (JObject)document["project"].DeepClone();
        public JObject GetObject(string id) => objects.TryGetValue(id, out var value)
            ? (JObject)value.DeepClone() : throw new KeyNotFoundException($"Unknown CAD ID '{id}'.");
        public string GetParentId(string id) => parents[id];

        public CadMetadata(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Metadata is empty.");
            RawJson = json;
            try
            {
                document = JObject.Parse(json, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (JsonException e) { throw new InvalidDataException("Metadata is not a valid JSON object.", e); }

            var version = document["schemaVersion"];
            if (version == null || (version.Type != JTokenType.String && version.Type != JTokenType.Integer)
                || (version.ToString() != "1" && version.ToString() != "1.0" && version.ToString() != "2.1"))
                throw new InvalidDataException("Unsupported schemaVersion; expected 1, 1.0 or 2.1.");
            var mappingStatus = document["mappingStatus"];
            if (version.ToString() == "2.1" && mappingStatus != null &&
                mappingStatus.Type != JTokenType.Null && mappingStatus.Type != JTokenType.String)
                throw new InvalidDataException("mappingStatus must be a string.");
            bool uncorrelated = version.ToString() == "2.1" &&
                (string)mappingStatus == "not_correlated_to_glb";
            if (uncorrelated && document["glbMapping"] != null && document["glbMapping"].Type != JTokenType.Null)
                throw new InvalidDataException("Uncorrelated metadata must not claim a GLB mapping.");
            HasVerifiedNodeMapping = !uncorrelated;
            if (!(document["project"] is JObject)) throw new InvalidDataException("Missing project object.");
            RootId = RequiredString((JObject)document["project"], "rootObjectId");
            if (!(document["objects"] is JArray entries) || entries.Count == 0)
                throw new InvalidDataException("objects must be a nonempty array.");

            objects = new Dictionary<string, JObject>(StringComparer.Ordinal);
            nodeIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            parents = new Dictionary<string, string>(StringComparer.Ordinal);
            var exportedNodes = ReadExporterMapping(version.ToString());
            var usedNodes = new HashSet<int>();
            foreach (var entry in entries)
            {
                if (!(entry is JObject obj)) throw new InvalidDataException("Every object entry must be an object.");
                string id = RequiredString(obj, "id");
                if (objects.ContainsKey(id)) throw new InvalidDataException($"Duplicate CAD ID '{id}'.");
                var node = obj["glbNodeIndex"];
                if (uncorrelated)
                {
                    if (node != null && node.Type != JTokenType.Null)
                        throw new InvalidDataException("Uncorrelated metadata must not claim GLB node indices.");
                }
                else
                {
                    int index;
                    if (exportedNodes != null)
                    {
                        if (!exportedNodes.TryGetValue(id, out index))
                            throw new InvalidDataException($"'{id}' is missing from glbMapping.objects.");
                        if (node != null && ReadNodeIndex(node, id) != index)
                            throw new InvalidDataException($"'{id}' has conflicting inline and exported node indices.");
                    }
                    else index = ReadNodeIndex(node, id);
                    if (!usedNodes.Add(index)) throw new InvalidDataException($"GLB node {index} is mapped more than once.");
                    nodeIndices.Add(id, index);
                }
                var parent = obj["parentId"];
                if (parent != null && parent.Type != JTokenType.Null &&
                    (parent.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)parent)))
                    throw new InvalidDataException($"'{id}' has an invalid parentId.");
                objects.Add(id, obj);
                parents.Add(id, (string)parent);
            }
            if (exportedNodes != null)
                foreach (string id in exportedNodes.Keys)
                    if (!objects.ContainsKey(id))
                        throw new InvalidDataException($"GLB mapping references unknown CAD ID '{id}'.");
            if (!objects.ContainsKey(RootId)) throw new InvalidDataException("rootObjectId does not exist in objects.");
            if (parents[RootId] != null) throw new InvalidDataException("The root object must not have a parent.");
            foreach (string id in objects.Keys)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string cursor = id;
                while (cursor != RootId)
                {
                    if (!visited.Add(cursor)) throw new InvalidDataException($"Hierarchy cycle at '{cursor}'.");
                    if (!parents.TryGetValue(cursor, out string parent) || parent == null || !objects.ContainsKey(parent))
                        throw new InvalidDataException($"'{cursor}' does not connect to root '{RootId}'.");
                    cursor = parent;
                }
                if (objects[id]["childIds"] is JToken childToken)
                {
                    if (!(childToken is JArray children)) throw new InvalidDataException($"'{id}' has invalid childIds.");
                    var listed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var child in children)
                    {
                        if (child.Type != JTokenType.String || !listed.Add((string)child) ||
                            !parents.TryGetValue((string)child, out var owner) || owner != id)
                            throw new InvalidDataException($"'{id}' has inconsistent childIds.");
                    }
                    foreach (var parent in parents)
                        if (parent.Value == id && !listed.Contains(parent.Key))
                            throw new InvalidDataException($"'{id}' is missing child '{parent.Key}'.");
                }
            }
            NodeIndices = new ReadOnlyDictionary<string, int>(nodeIndices);
        }

        // Normalize correspondence separately; never add fields to the source document.
        private Dictionary<string, int> ReadExporterMapping(string version)
        {
            var mapping = document["glbMapping"];
            if (version != "2.1" || mapping == null || mapping.Type == JTokenType.Null)
                return null; // Preserve legacy inline-only documents.
            if (document["mappingStatus"]?.Type != JTokenType.String ||
                (string)document["mappingStatus"] != "assigned_by_exporter")
                throw new InvalidDataException("Nested GLB mappings require mappingStatus assigned_by_exporter.");
            if (!(mapping is JObject container) || !(container["objects"] is JArray rows))
                throw new InvalidDataException("glbMapping must contain an objects array.");
            var status = container["status"];
            if (status != null && (status.Type != JTokenType.String || (string)status != "assigned_by_exporter"))
                throw new InvalidDataException("glbMapping.status contradicts mappingStatus.");

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodes = new HashSet<int>();
            foreach (var entry in rows)
            {
                if (!(entry is JObject row)) throw new InvalidDataException("Every GLB mapping row must be an object.");
                string id = RequiredString(row, "objectId");
                if (result.ContainsKey(id)) throw new InvalidDataException($"Duplicate GLB mapping ID '{id}'.");
                if (row["status"]?.Type != JTokenType.String || (string)row["status"] != "matched")
                    throw new InvalidDataException($"GLB mapping for '{id}' must be matched.");
                int index = ReadNodeIndex(row["glbNodeIndex"], id);
                if (!nodes.Add(index)) throw new InvalidDataException($"GLB node {index} is mapped more than once.");
                result.Add(id, index);
            }
            return result;
        }

        private static int ReadNodeIndex(JToken node, string id)
        {
            if (node?.Type != JTokenType.Integer || !int.TryParse(node.ToString(), out int index) || index < 0)
                throw new InvalidDataException($"'{id}' requires a nonnegative Int32 glbNodeIndex.");
            return index;
        }

        private static string RequiredString(JObject obj, string key)
        {
            if (obj[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)obj[key]))
                throw new InvalidDataException($"Missing or invalid '{key}'.");
            return (string)obj[key];
        }
    }
}
