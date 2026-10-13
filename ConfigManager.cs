using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UniversalReplacer
{
    public class ConfigManager
    {
        private class RawRule
        {
            public string Root, RootContains, Path, PathContains, ComponentType, PropertyName;
            public JToken Value;
            public int MaterialIndex = -1;
        }

        private const float ReloadDebounceSeconds = 0.5f;

        private readonly string _path;
        private FileSystemWatcher _watcher;
        private float _reloadAt = -1f;
        private int _reloadVersion;

        public List<TargetRule> Rules { get; private set; } = new List<TargetRule>();
        public Dictionary<string, List<TargetRule>> RulesByRoot { get; } = new Dictionary<string, List<TargetRule>>(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, List<TargetRule>>> RulesByRootContains { get; } = new List<KeyValuePair<string, List<TargetRule>>>();
        public List<TargetRule> RulesByPathContains { get; } = new List<TargetRule>();

        public Action OnConfigReloaded;

        public ConfigManager(string path)
        {
            _path = path;
            ReloadConfig(false);
            SetupWatcher();
        }

        /// Call once per frame (from Plugin.Update) so watcher events can debounce.
        internal void Tick()
        {
            if (_reloadAt >= 0f && Time.time >= _reloadAt) { _reloadAt = -1f; ReloadConfig(true); }
        }

        public void ReloadConfig(bool trigger)
        {
            try { if (!File.Exists(_path)) WriteTemplate(_path); }
            catch (Exception e) { Plugin.Log.LogError($"Erro ao criar template: {e.Message}"); return; }

            string content;
            try { content = File.ReadAllText(_path); }
            catch (Exception e) { Plugin.Log.LogError($"Erro ao ler JSON: {e.Message}"); return; }

            int version = ++_reloadVersion;
            Task.Run(() =>
            {
                List<RawRule> raw;
                try { raw = ParseRules(content); }
                catch (Exception e) { Plugin.EnqueueAction(() => Plugin.Log.LogError($"Erro ao ler JSON: {e.Message}")); return; }

                Plugin.EnqueueAction(() => { if (version == _reloadVersion) BuildRules(raw, trigger); });
            });
        }

        private static void WriteTemplate(string path)
        {
            var template = new
            {
                Instructions = "Place your rules in the TargetRules array. Supported targets: Renderer, Graphic (UI), ParticleSystem, AudioSource, Light.",
                Examples = new[]
                {
            new { Description = "Change an Image color to Red", Root = "PlayerUI", RootContains = "", PathContains = "", ComponentType = "Image", PropertyName = "color", Value = new { r = 1.0, g = 0.0, b = 0.0, a = 1.0 } },
            new { Description = "Change a Renderer material emission color", Root = "", RootContains = "", PathContains = "Boss/Body", ComponentType = "Renderer", PropertyName = "_EmissionColor", Value = new { r = 0.0, g = 1.0, b = 0.0, a = 0.0 } },
            new { Description = "Change particle intensity", Root = "", RootContains = "", PathContains = "Explosion", ComponentType = "ParticleSystem", PropertyName = "startColor", Value = new { r = 1.0, g = 1.0, b = 0.0, a = 1.0 } }
        },
                TargetRules = new List<TargetRule>()
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(template, Formatting.Indented));
        }

        private static List<RawRule> ParseRules(string content)
        {
            var data = JObject.Parse(content);
            var result = new List<RawRule>();
            JToken rulesToken = data["TargetRules"] ?? data["targetRules"];
            if (!(rulesToken is JArray array)) return result;
            foreach (JToken item in array)
            {
                var rule = new RawRule
                {
                    Root = item["Root"]?.ToString() ?? "",
                    RootContains = item["RootContains"]?.ToString() ?? "",
                    Path = item["Path"]?.ToString() ?? "",
                    PathContains = item["PathContains"]?.ToString() ?? "",
                    ComponentType = item["ComponentType"]?.ToString() ?? "",
                    PropertyName = item["PropertyName"]?.ToString() ?? "",
                    Value = item["Value"]
                };
                JToken matIdx = item["MaterialIndex"];
                if (matIdx != null && matIdx.Type != JTokenType.Null)
                    try { rule.MaterialIndex = matIdx.ToObject<int>(); } catch { rule.MaterialIndex = -1; }
                result.Add(rule);
            }
            return result;
        }

        private void BuildRules(List<RawRule> raw, bool trigger)
        {
            ResourceManager.RefreshChangedTextures();

            var newRules = new List<TargetRule>(raw.Count);
            var byRoot = new Dictionary<string, List<TargetRule>>(StringComparer.OrdinalIgnoreCase);
            var rootContainsMap = new Dictionary<string, List<TargetRule>>(StringComparer.OrdinalIgnoreCase);
            var byPathContains = new List<TargetRule>();

            int index = 0;
            foreach (RawRule r in raw)
            {
                index++;
                TargetRule rule;
                try
                {
                    rule = new TargetRule
                    {
                        Root = r.Root,
                        RootContains = r.RootContains,
                        Path = r.Path,
                        PathContains = r.PathContains,
                        ComponentType = r.ComponentType,
                        PropertyName = r.PropertyName,
                        Value = r.Value,
                        MaterialIndex = r.MaterialIndex
                    };
                    rule.Init();
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Skipping rule #{index} ({r.PropertyName}): {e.Message}"); continue; }

                newRules.Add(rule);
                if (!string.IsNullOrEmpty(rule.Root))
                {
                    if (!byRoot.TryGetValue(rule.Root, out List<TargetRule> list)) { list = new List<TargetRule>(); byRoot[rule.Root] = list; }
                    list.Add(rule);
                }
                else if (!string.IsNullOrEmpty(rule.RootContains))
                {
                    if (!rootContainsMap.TryGetValue(rule.RootContains, out List<TargetRule> list)) { list = new List<TargetRule>(); rootContainsMap[rule.RootContains] = list; }
                    list.Add(rule);
                }
                else if (!string.IsNullOrEmpty(rule.PathContains)) byPathContains.Add(rule);
            }

            Rules = newRules;
            RulesByRoot.Clear();
            foreach (KeyValuePair<string, List<TargetRule>> kvp in byRoot) RulesByRoot[kvp.Key] = kvp.Value;
            RulesByRootContains.Clear();
            foreach (KeyValuePair<string, List<TargetRule>> kvp in rootContainsMap) RulesByRootContains.Add(new KeyValuePair<string, List<TargetRule>>(kvp.Key, kvp.Value));
            RulesByPathContains.Clear();
            RulesByPathContains.AddRange(byPathContains);

            Plugin.Log.LogInfo($"ColorizedInk: {newRules.Count} rules ({byRoot.Count} root / {rootContainsMap.Count} rootContains / {byPathContains.Count} pathContains).");

            if (trigger) { try { OnConfigReloaded?.Invoke(); } catch (Exception e) { Plugin.Log.LogDebug($"Aviso: {e.Message}"); } }
        }

        private void SetupWatcher()
        {
            string dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watcher.Changed += (s, e) => Plugin.EnqueueAction(() => _reloadAt = Time.time + ReloadDebounceSeconds);
            _watcher.Error += (s, e) => Plugin.EnqueueAction(() =>
            {
                Plugin.Log.LogWarning($"Config watcher error: {e.GetException().Message} — re-enabling.");
                try { _watcher.EnableRaisingEvents = true; } catch { }
            });
        }

        public void EnableWatcher(bool en) { if (_watcher != null) _watcher.EnableRaisingEvents = en; }
    }
}