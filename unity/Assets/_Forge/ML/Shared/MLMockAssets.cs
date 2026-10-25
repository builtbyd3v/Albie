using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Forge.ML
{
    public class MLAtlasMetadata { public string atlas_url; public int[] cell; public int cols; public Dictionary<string, int> index; }
    [Serializable] public class MLDatasetEntry
    {
        public string id, thumb_url, name, path, split, kind, text;
        public int label_idx, width, height, channels;
        public long file_size;
        public bool removed;
    }
    [Serializable] public class MLGraph { public MLLayer[] layers; }
    [Serializable] public class MLResults { public MLSample[] samples; }
    public class MLConfusion { public string[] labels; public int[][] matrix; }
    [Serializable] public class MLReplayPayload : MLMetrics
    {
        public string status, reason, id;
        public int total_epochs, label_idx;
        public bool removed;
        public int[] pred_idx;
        public float?[] per_class;
        public MLProbeSample[] samples;
    }
    [Serializable] public class MLReplayEvent { public float t; public string type; public MLReplayPayload payload; }

    // References to the generated source assets. Never writes to their files or importer settings.
    public class MLMockAssets : ScriptableObject
    {
        public TextAsset classes, dataset, atlasMetadata, graph, results, confusion, events;
        public Texture2D atlas;

        public MLFixture Load(out MLAtlasMetadata metadata, out MLReplayEvent[] timeline, out int[] cells)
        {
            if (classes == null || dataset == null || atlasMetadata == null || graph == null || results == null || confusion == null || events == null || atlas == null)
                throw new InvalidOperationException("ML mock references are missing. Run Forge > ML > Build Prefabs and Sandbox.");
            metadata = JsonConvert.DeserializeObject<MLAtlasMetadata>(atlasMetadata.text);
            timeline = JsonConvert.DeserializeObject<MLReplayEvent[]>(events.text);
            var entries = JsonConvert.DeserializeObject<MLDatasetEntry[]>(dataset.text);
            var predictions = JsonConvert.DeserializeObject<MLResults>(results.text);
            var byId = new Dictionary<string, MLSample>();
            foreach (var sample in predictions.samples) byId.Add(sample.id, sample);
            var samples = new MLSample[entries.Length]; cells = new int[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i]; var sample = byId[entry.id];
                sample.label_idx = entry.label_idx;
                sample.name = string.IsNullOrEmpty(entry.name) ? entry.id : entry.name;
                sample.path = entry.path;
                sample.split = string.IsNullOrEmpty(entry.split) ? "val" : entry.split;
                sample.width = entry.width > 0 ? entry.width : 32;
                sample.height = entry.height > 0 ? entry.height : 32;
                sample.channels = entry.channels > 0 ? entry.channels : 3;
                sample.file_size = entry.file_size;
                if (sample.true_idx != entry.label_idx) throw new InvalidOperationException("Dataset/result label mismatch for " + entry.id);
                samples[i] = sample; cells[i] = metadata.index[entry.id];
            }
            if (metadata.cell.Length != 2 || metadata.cols * metadata.cell[0] != atlas.width || atlas.height % metadata.cell[1] != 0)
                throw new InvalidOperationException("ML atlas metadata does not match the imported texture dimensions.");
            int capacity = metadata.cols * (atlas.height / metadata.cell[1]);
            foreach (int cell in cells) if (cell < 0 || cell >= capacity) throw new InvalidOperationException("ML atlas index is outside the texture.");
            var ticks = new List<MLMetrics>(); float previous = -1;
            foreach (var e in timeline)
            {
                if (e.t < previous) throw new InvalidOperationException("ML replay timestamps must be sorted.");
                previous = e.t;
                if (e.type == "ml.metrics") ticks.Add(e.payload);
            }
            var evaluation = JsonConvert.DeserializeObject<MLConfusion>(confusion.text);
            return new MLFixture
            {
                classes = JsonConvert.DeserializeObject<string[]>(classes.text), samples = samples,
                layers = JsonConvert.DeserializeObject<MLGraph>(graph.text).layers,
                metrics = ticks.ToArray(), confusion = evaluation.matrix
            };
        }
    }
}
