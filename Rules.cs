using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace UniversalReplacer
{
    public class RuleProcessor
    {
        private readonly ConfigManager _config;
        private readonly Dictionary<CacheKey, CachedState> _cache = new Dictionary<CacheKey, CachedState>();
        private static readonly List<Component> _sharedCompList = new List<Component>(64);
        private static readonly Queue<Transform> _deepQueue = new Queue<Transform>(256);
        private static readonly List<CacheKey> _deadKeys = new List<CacheKey>(32);

        private readonly Dictionary<(Type, string), List<MemberInfo>> _reflectionCache = new Dictionary<(Type, string), List<MemberInfo>>();
        private readonly Dictionary<Type, Dictionary<string, bool>> _typeMatchCache = new Dictionary<Type, Dictionary<string, bool>>();

        private struct CacheKey : IEquatable<CacheKey>
        {
            public int InstanceID;
            public string PropertyName;
            public int MaterialIndex;
            public bool Equals(CacheKey other) => InstanceID == other.InstanceID && MaterialIndex == other.MaterialIndex && PropertyName == other.PropertyName;
            public override int GetHashCode() => (InstanceID * 397) ^ (PropertyName?.GetHashCode() ?? 0) ^ MaterialIndex;
        }

        private class CachedState
        {
            public Component Target; public TargetRule Rule; public object OriginalValue; public Action<Component, TargetRule, object> RevertAction;
            public List<MemberInfo> Chain;          // resolved reflection chain for enforcement; null = enforcement is a no-op
            public Type FinalMemberType;
            public object EnforceValue; public bool EnforceValueCached;
        }

        public RuleProcessor(ConfigManager config) => _config = config;

        public void ScanFullScene() { foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) ProcessObject(go); }

        public void ProcessObject(GameObject root)
        {
            if (root == null || _config.Rules == null || _config.Rules.Count == 0) return;
            Transform t = root.transform;

            if (_config.RulesByRoot.TryGetValue(root.name, out List<TargetRule> exactRules)) { for (int i = 0; i < exactRules.Count; i++) ApplyPathing(t, exactRules[i]); }
            for (int i = 0; i < _config.RulesByRootContains.Count; i++)
            {
                var kvp = _config.RulesByRootContains[i];
                if (kvp.Value != null && kvp.Value.Count > 0 && MatchesPath(t, kvp.Value[0].RootContainsParts)) { for (int j = 0; j < kvp.Value.Count; j++) ApplyPathing(t, kvp.Value[j]); }
            }
            for (int i = 0; i < _config.RulesByPathContains.Count; i++)
            {
                TargetRule rule = _config.RulesByPathContains[i];
                if (rule.IsDeepSearch) { if (MatchesPath(t, rule.PathParts)) DoDeepSearch(t, rule); }
                else if (MatchesPath(t, rule.PathParts)) ApplyToTarget(t.gameObject, rule);
            }
        }

        private void ApplyPathing(Transform root, TargetRule rule)
        {
            if (string.IsNullOrEmpty(rule.Path) && string.IsNullOrEmpty(rule.PathContains)) ApplyToTarget(root.gameObject, rule);
            else if (rule.IsDeepSearch) DoDeepSearch(root, rule);
            else { Transform found = root.Find(!string.IsNullOrEmpty(rule.Path) ? rule.Path : rule.PathContains); if (found != null) ApplyToTarget(found.gameObject, rule); }
        }

        private void DoDeepSearch(Transform root, TargetRule rule)
        {
            _deepQueue.Clear();
            _deepQueue.Enqueue(root);
            while (_deepQueue.Count > 0)
            {
                Transform curr = _deepQueue.Dequeue();
                if (curr.name.IndexOf(rule.CleanDeepTarget, StringComparison.OrdinalIgnoreCase) >= 0) { ApplyToTarget(curr.gameObject, rule); continue; }
                for (int i = 0; i < curr.childCount; i++) _deepQueue.Enqueue(curr.GetChild(i));
            }
        }

        private static bool MatchesPath(Transform t, string[] parts)
        {
            if (parts == null || parts.Length == 0) return true;
            Transform curr = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                bool found = false;
                while (curr != null) { if (curr.name.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0) { found = true; curr = curr.parent; break; } curr = curr.parent; }
                if (!found) return false;
            }
            return true;
        }

        private bool IsComponentTypeMatch(Type compType, string ruleTargetType)
        {
            if (string.IsNullOrEmpty(ruleTargetType)) return true;
            if (!_typeMatchCache.TryGetValue(compType, out var ruleMap)) { ruleMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); _typeMatchCache[compType] = ruleMap; }
            if (!ruleMap.TryGetValue(ruleTargetType, out bool matches)) { matches = compType.Name.IndexOf(ruleTargetType, StringComparison.OrdinalIgnoreCase) >= 0; ruleMap[ruleTargetType] = matches; }
            return matches;
        }

        private void ApplyToTarget(GameObject go, TargetRule rule)
        {
            go.GetComponents(_sharedCompList);
            for (int i = 0; i < _sharedCompList.Count; i++)
            {
                Component comp = _sharedCompList[i];
                if (comp == null || !IsComponentTypeMatch(comp.GetType(), rule.ComponentType)) continue;

                CacheKey key = new CacheKey { InstanceID = comp.GetInstanceID(), PropertyName = rule.PropertyName, MaterialIndex = rule.MaterialIndex };
                if (_cache.ContainsKey(key)) continue;

                bool applied = false; object origVal = null; Action<Component, TargetRule, object> revertAct = null;

                if (rule.NormalizedPropName == "enabled" && comp is Behaviour b) { if (rule.ParsedValue is bool bVal) { origVal = b.enabled; b.enabled = bVal; applied = true; revertAct = RevertBehaviour; } }
                if (!applied)
                {
                    if (comp is Graphic g && TryPatchGraphic(g, rule, out origVal)) { applied = true; revertAct = RevertGraphic; }
                    else if (comp is ParticleSystem ps && TryPatchParticleSystem(ps, rule)) applied = true;
                    else if (comp is Renderer r && TryPatchRendererMaterial(r, rule)) { applied = true; revertAct = RevertRenderer; }
                    else if ((comp is LineRenderer || comp is TrailRenderer) && TryPatchTrailColors(comp, rule)) applied = true;
                    else if (TryPatchViaReflection(comp, rule, out object dummyOrig)) { origVal = dummyOrig; applied = true; revertAct = RevertReflection; }
                }
                if (applied)
                {
                    // Capture (once) whether enforcement can ever do anything for this entry.
                    List<MemberInfo> chain = ResolveChain(comp.GetType(), rule.PropertyName);
                    Type finalType = null;
                    if (chain != null)
                    {
                        MemberInfo last = chain[chain.Count - 1];
                        finalType = last is PropertyInfo pt ? pt.PropertyType : ((FieldInfo)last).FieldType;
                    }
                    _cache[key] = new CachedState { Target = comp, Rule = rule, OriginalValue = origVal, RevertAction = revertAct, Chain = chain, FinalMemberType = finalType };
                }
            }
            _sharedCompList.Clear();
        }

        private static void RevertBehaviour(Component comp, TargetRule rule, object orig) { if (comp is Behaviour b && orig is bool boolVal) b.enabled = boolVal; }

        private static bool TryPatchGraphic(Graphic g, TargetRule rule, out object orig)
        {
            orig = null; if (rule.ParsedValue == null) return false;
            if (rule.IsColorRule && rule.ParsedValue is Color c) { orig = g.color; if (g.color != c) g.color = c; return true; }
            if (g is UnityEngine.UI.Image img && rule.NormalizedPropName == "sprite" && rule.ParsedValue is Sprite sp) { orig = img.sprite; if (img.sprite != sp) img.sprite = sp; return true; }
            if (g is RawImage rawImg && (rule.NormalizedPropName == "texture" || rule.NormalizedPropName == "maintex") && rule.ParsedValue is Texture2D tex) { orig = rawImg.texture; if (rawImg.texture != tex) rawImg.texture = tex; return true; }
            return false;
        }

        private static void RevertGraphic(Component comp, TargetRule rule, object orig)
        {
            if (comp is Graphic g && orig != null)
            {
                if (orig is Color c) { if (g.color != c) g.color = c; }
                else if (comp is UnityEngine.UI.Image img && orig is Sprite sp) img.sprite = sp;
                else if (comp is RawImage rawImg && orig is Texture2D tex) rawImg.texture = tex;
            }
        }

        private static bool TryPatchParticleSystem(ParticleSystem ps, TargetRule rule)
        {
            bool applied = false;
            if (rule.NormalizedPropName.Contains("color") && rule.ParsedValue is Color)
            {
                try { var main = ps.main; main.startColor = new ParticleSystem.MinMaxGradient(rule.BakedColor); applied = true; var trails = ps.trails; if (trails.enabled) { trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(rule.BakedColor); trails.colorOverTrail = new ParticleSystem.MinMaxGradient(rule.BakedColor); } } catch { }
            }
            if (ps.TryGetComponent<ParticleSystemRenderer>(out var r))
            {
                Material[] mats = r.materials; bool changedRenderer = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (rule.MaterialIndex >= 0 && rule.MaterialIndex != i) continue;
                    Material m = mats[i]; if (m == null) continue;
                    if (rule.ParsedValue is Color) { if (m.HasProperty("_TintColor")) { m.SetColor("_TintColor", rule.BakedColor); changedRenderer = true; } if (m.HasProperty("_Color")) { m.SetColor("_Color", rule.BakedColor); changedRenderer = true; } if (m.HasProperty("_EmissionColor")) { m.SetColor("_EmissionColor", rule.BakedColor); changedRenderer = true; } if (m.HasProperty(rule.PropertyName)) { m.SetColor(rule.PropertyName, rule.BakedColor); changedRenderer = true; } }
                }
                if (changedRenderer) { r.materials = mats; applied = true; }
            }
            return applied;
        }

        private static bool TryPatchRendererMaterial(Renderer r, TargetRule rule)
        {
            if (r is ParticleSystemRenderer) return false;
            Material[] mats = r.materials; if (mats == null || mats.Length == 0) return false;
            bool changedMat = false; string prop = rule.PropertyName;
            for (int i = 0; i < mats.Length; i++)
            {
                if (rule.MaterialIndex >= 0 && rule.MaterialIndex != i) continue;
                Material m = mats[i]; if (m == null) continue;
                string actualProp = prop; bool foundProp = false;
                if (m.HasProperty(prop)) foundProp = true; else if (m.HasProperty("_" + prop)) { actualProp = "_" + prop; foundProp = true; } else if (prop == "MainTex" || prop == "_MainTex") { actualProp = "_MainTex"; foundProp = true; }
                if (!foundProp) continue;
                if (rule.ParsedValue is Texture2D tex) { if (actualProp == "_MainTex" || actualProp == "MainTex") m.mainTexture = tex; else m.SetTexture(actualProp, tex); changedMat = true; }
                else if (rule.ParsedValue is Color) { m.SetColor(actualProp, rule.BakedColor); changedMat = true; }
                else if (rule.ParsedValue is float || rule.ParsedValue is int) { m.SetFloat(actualProp, rule.BakedFloat); changedMat = true; }
            }
            if (changedMat) { r.materials = mats; return true; }
            return false;
        }

        private static void RevertRenderer(Component comp, TargetRule rule, object orig) { if (comp is Renderer r && !(r is ParticleSystemRenderer)) { if (rule.MaterialIndex >= 0 && rule.MaterialIndex < r.sharedMaterials.Length) r.SetPropertyBlock(null, rule.MaterialIndex); else r.SetPropertyBlock(null); } }

        private static bool TryPatchTrailColors(Component comp, TargetRule rule)
        {
            string prop = rule.NormalizedPropName;
            if (prop != "color" && prop != "startcolor" && prop != "endcolor" && prop != "tintcolor" && prop != "width" && prop != "startwidth" && prop != "endwidth" && prop != "widthmultiplier" && prop != "multiplier") return false;
            if (prop.Contains("width") || prop.Contains("multiplier"))
            {
                if (comp is LineRenderer lr) { if (prop.Contains("multiplier")) lr.widthMultiplier = rule.BakedFloat; else { lr.startWidth = rule.BakedFloat; lr.endWidth = rule.BakedFloat; } return true; }
                if (comp is TrailRenderer tr) { if (prop.Contains("multiplier")) tr.widthMultiplier = rule.BakedFloat; else { tr.startWidth = rule.BakedFloat; tr.endWidth = rule.BakedFloat; } return true; }
            }
            else if (rule.ParsedValue is Color)
            {
                if (comp is LineRenderer lr) { lr.colorGradient = rule.BakedGradient; lr.startColor = rule.BakedColor; lr.endColor = rule.BakedColor; return true; }
                if (comp is TrailRenderer tr) { tr.colorGradient = rule.BakedGradient; tr.startColor = rule.BakedColor; tr.endColor = rule.BakedColor; return true; }
            }
            return false;
        }

        private static MemberInfo FindMemberRobust(Type type, string name) { while (type != null && type != typeof(object)) { var member = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase) ?? (MemberInfo)type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase); if (member != null) return member; type = type.BaseType; } return null; }

        private List<MemberInfo> ResolveChain(Type type, string propertyName)
        {
            if (_reflectionCache.TryGetValue((type, propertyName), out List<MemberInfo> cached)) return cached;
            List<MemberInfo> chain = null;
            try
            {
                var build = new List<MemberInfo>(); Type currentType = type; bool ok = true;
                foreach (string part in propertyName.Split('.'))
                {
                    if (part.Equals("Behavior", StringComparison.OrdinalIgnoreCase) || part.Equals("Behaviour", StringComparison.OrdinalIgnoreCase)) continue;
                    MemberInfo m = FindMemberRobust(currentType, part);
                    if (m == null) { ok = false; break; }
                    build.Add(m);
                    currentType = m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
                }
                chain = ok && build.Count > 0 ? build : null;
            }
            catch { chain = null; }
            _reflectionCache[(type, propertyName)] = chain;
            return chain;
        }

        private bool TryPatchViaReflection(Component comp, TargetRule rule, out object originalValue)
        {
            originalValue = null;
            List<MemberInfo> chain = ResolveChain(comp.GetType(), rule.PropertyName);
            if (chain == null) return false;

            object currentObj = comp;
            for (int i = 0; i < chain.Count - 1; i++) { MemberInfo m = chain[i]; currentObj = m is PropertyInfo p ? p.GetValue(currentObj, null) : ((FieldInfo)m).GetValue(currentObj); if (currentObj == null) return false; }
            MemberInfo finalMember = chain[chain.Count - 1]; PropertyInfo pi = finalMember as PropertyInfo; FieldInfo fi = finalMember as FieldInfo;
            Type targetType = pi?.PropertyType ?? fi.FieldType; originalValue = pi != null ? pi.GetValue(currentObj, null) : fi.GetValue(currentObj);
            object valueToSet = ParseValueDynamic(rule, targetType);
            if (valueToSet != null) { if (pi != null && pi.CanWrite) pi.SetValue(currentObj, valueToSet, null); else if (fi != null && !fi.IsInitOnly) fi.SetValue(currentObj, valueToSet); else return false; return true; }
            return false;
        }

        private static object ParseValueDynamic(TargetRule rule, Type targetType)
        {
            if (rule.ParsedValue == null) return null;
            if (targetType == typeof(Sprite) && !string.IsNullOrEmpty(rule.BakedString)) return ResourceManager.GetSprite(rule.BakedString);
            if (targetType.IsEnum && !string.IsNullOrEmpty(rule.BakedString)) { try { return Enum.Parse(targetType, rule.BakedString, true); } catch { } }
            if (targetType == typeof(string) || targetType == typeof(object) || targetType == typeof(Color) || targetType == typeof(Texture2D)) return rule.ParsedValue;
            try { return Convert.ChangeType(rule.ParsedValue, targetType); } catch { }
            return rule.ParsedValue;
        }

        private static void ApplyChain(object root, List<MemberInfo> chain, object value)
        {
            object currentObj = root;
            for (int i = 0; i < chain.Count - 1; i++)
            {
                MemberInfo m = chain[i];
                currentObj = m is PropertyInfo p ? p.GetValue(currentObj, null) : ((FieldInfo)m).GetValue(currentObj);
                if (currentObj == null) return;
            }
            MemberInfo finalMember = chain[chain.Count - 1];
            if (finalMember is PropertyInfo pi && pi.CanWrite) pi.SetValue(currentObj, value, null);
            else if (finalMember is FieldInfo fi && !fi.IsInitOnly) fi.SetValue(currentObj, value);
        }

        private void RevertReflection(Component comp, TargetRule rule, object origVal)
        {
            if (origVal == null) return; JToken safeToken = null; try { safeToken = origVal is JToken j ? j : JToken.FromObject(origVal); } catch { }
            TargetRule tempRule = new TargetRule { PropertyName = rule.PropertyName, Value = safeToken }; tempRule.Init(); TryPatchViaReflection(comp, tempRule, out _);
        }

        public void RevertAll()
        {
            foreach (CachedState s in _cache.Values)
            {
                if (s.RevertAction == null || s.Target == null) continue; // destroyed during scene switch — skip instead of throwing
                try { s.RevertAction(s.Target, s.Rule, s.OriginalValue); }
                catch (Exception e) { Plugin.Log.LogDebug($"Revert skipped: {e.Message}"); }
            }
            _cache.Clear();
        }

        public void EnforceActiveRules()
        {
            _deadKeys.Clear();
            foreach (KeyValuePair<CacheKey, CachedState> kvp in _cache)
            {
                CachedState s = kvp.Value;
                if (s.Target == null) { _deadKeys.Add(kvp.Key); continue; }

                if (s.Rule.NormalizedPropName == "enabled" && s.Target is Behaviour b && s.Rule.ParsedValue is bool boolVal) { b.enabled = boolVal; continue; }

                if (s.Chain == null) continue; // enforcement could never apply anything here — skip the wasted reflection walk

                object value;
                if (s.EnforceValueCached) value = s.EnforceValue;
                else
                {
                    value = ParseValueDynamic(s.Rule, s.FinalMemberType);
                    if (value == null) continue; // keep retrying on later ticks (e.g., texture not loaded yet)
                    s.EnforceValue = value; s.EnforceValueCached = true;
                }
                ApplyChain(s.Target, s.Chain, value);
            }
            for (int i = 0; i < _deadKeys.Count; i++) _cache.Remove(_deadKeys[i]);
        }
    }
}