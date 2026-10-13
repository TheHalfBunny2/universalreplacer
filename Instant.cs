using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UniversalReplacer
{
    public static class InstantProcessor
    {
        private static readonly List<Transform> _transformBuffer = new List<Transform>(256);
        private static readonly List<TargetRule> _candidateRules = new List<TargetRule>(32);
        private static readonly List<int> _candidateMemo = new List<int>(32);
        private static readonly List<string> _memoKeys = new List<string>(4);
        private static readonly List<bool[]> _memoArrays = new List<bool[]>(4);
        private static readonly Dictionary<int, bool> _idScratch = new Dictionary<int, bool>(512);

        public static void ApplyInstant(GameObject root, List<TargetRule> rules)
        {
            if (root == null || rules == null || rules.Count == 0) return;

            _transformBuffer.Clear();
            root.GetComponentsInChildren(true, _transformBuffer); // preorder: parents always precede children
            string rootName = root.name;

            // Rule filtering by root happens ONCE, not per node.
            _candidateRules.Clear();
            bool anyRootContains = false;
            for (int i = 0; i < rules.Count; i++)
            {
                TargetRule rule = rules[i];
                if (!string.IsNullOrEmpty(rule.Root) && rootName != rule.Root) continue;
                _candidateRules.Add(rule);
                if (!string.IsNullOrEmpty(rule.RootContains)) anyRootContains = true;
            }
            if (_candidateRules.Count == 0) return;

            // One memo (flag array aligned to the transform buffer) per distinct RootContains value.
            _memoKeys.Clear();
            if (anyRootContains)
                for (int j = 0; j < _candidateRules.Count; j++)
                {
                    string rc = _candidateRules[j].RootContains;
                    if (string.IsNullOrEmpty(rc)) continue;
                    bool exists = false;
                    for (int k = 0; k < _memoKeys.Count; k++)
                        if (string.Equals(_memoKeys[k], rc, StringComparison.Ordinal)) { exists = true; break; }
                    if (!exists) { _memoKeys.Add(rc); BuildMemo(rc, _memoKeys.Count - 1, root.transform); }
                }

            _candidateMemo.Clear();
            for (int j = 0; j < _candidateRules.Count; j++)
            {
                string rc = _candidateRules[j].RootContains;
                int mi = -1;
                if (!string.IsNullOrEmpty(rc))
                    for (int k = 0; k < _memoKeys.Count; k++)
                        if (string.Equals(_memoKeys[k], rc, StringComparison.Ordinal)) { mi = k; break; }
                _candidateMemo.Add(mi);
            }

            int memoCount = _memoKeys.Count;
            for (int i = 0; i < _transformBuffer.Count; i++)
            {
                Transform t = _transformBuffer[i];
                string nodeName = t.name;
                for (int j = 0; j < _candidateRules.Count; j++)
                {
                    if (memoCount > 0)
                    {
                        int mi = _candidateMemo[j];
                        if (mi >= 0 && !_memoArrays[mi][i]) continue;
                    }
                    TargetRule rule = _candidateRules[j];
                    if (!string.IsNullOrEmpty(rule.Path) && nodeName != rule.Path) continue;
                    string pc = rule.CleanPathContains;
                    if (pc != null && pc.Length != 0 && nodeName.IndexOf(pc, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    ApplyRuleToGameObject(t.gameObject, rule);
                }
            }
        }

        private static void BuildMemo(string rootContains, int memoIndex, Transform root)
        {
            if (_memoArrays.Count <= memoIndex) _memoArrays.Add(new bool[64]);
            bool[] flags = _memoArrays[memoIndex];
            if (flags.Length < _transformBuffer.Count)
            {
                flags = new bool[Math.Max(_transformBuffer.Count, flags.Length * 2)];
                _memoArrays[memoIndex] = flags;
            }
            _idScratch.Clear();

            // Preserve original semantics: ancestors ABOVE the instantiated root also count.
            bool above = false;
            for (Transform a = root.parent; a != null; a = a.parent)
                if (a.name.IndexOf(rootContains, StringComparison.OrdinalIgnoreCase) >= 0) { above = true; break; }

            for (int i = 0; i < _transformBuffer.Count; i++)
            {
                Transform t = _transformBuffer[i];
                bool matched = t.name.IndexOf(rootContains, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!matched && i > 0 && t.parent != null && _idScratch.TryGetValue(t.parent.GetInstanceID(), out bool pm)) matched = pm;
                else if (!matched && i == 0 && above) matched = true;
                _idScratch[t.GetInstanceID()] = matched;
                flags[i] = matched;
            }
        }

        private static void ApplyRuleToGameObject(GameObject go, TargetRule rule)
        {
            bool checkType = !string.IsNullOrEmpty(rule.ComponentType);
            if (go.TryGetComponent<Graphic>(out var g)) { if (!checkType || g.GetType().Name.IndexOf(rule.ComponentType, StringComparison.OrdinalIgnoreCase) >= 0) ApplyToGraphic(g, rule); }
            if (go.TryGetComponent<Renderer>(out var r)) { if (!checkType || r.GetType().Name.IndexOf(rule.ComponentType, StringComparison.OrdinalIgnoreCase) >= 0) ApplyToRenderer(r, rule); }
            if (go.TryGetComponent<Light>(out var l)) { if (!checkType || l.GetType().Name.IndexOf(rule.ComponentType, StringComparison.OrdinalIgnoreCase) >= 0) ApplyToLight(l, rule); }
            if (go.TryGetComponent<ParticleSystem>(out var ps)) { if (!checkType || ps.GetType().Name.IndexOf(rule.ComponentType, StringComparison.OrdinalIgnoreCase) >= 0) ApplyToParticle(ps, rule); }
            if (go.TryGetComponent<AudioSource>(out var audio)) { if (!checkType || audio.GetType().Name.IndexOf(rule.ComponentType, StringComparison.OrdinalIgnoreCase) >= 0) ApplyToAudio(audio, rule); }
        }

        private static void ApplyToGraphic(Graphic g, TargetRule rule)
        {
            if (rule.ParsedValue == null) return;
            if (rule.IsColorRule && rule.ParsedValue is Color c) { if (g.color != c) g.color = c; }
            if (g is UnityEngine.UI.Image img && rule.NormalizedPropName == "sprite" && rule.ParsedValue is Sprite sp) { if (img.sprite != sp) img.sprite = sp; }
            else if (g is RawImage rawImg && (rule.NormalizedPropName == "texture" || rule.NormalizedPropName == "maintex") && rule.ParsedValue is Texture2D tex) { if (rawImg.texture != tex) rawImg.texture = tex; }
        }

        private static void ApplyToRenderer(Renderer r, TargetRule rule)
        {
            if (rule.ParsedValue == null) return;
            if (rule.IsColorRule && rule.ParsedValue is Color)
            {
                if (r is TrailRenderer tr) { tr.colorGradient = rule.BakedGradient; tr.startColor = rule.BakedColor; tr.endColor = rule.BakedColor; }
                else if (r is LineRenderer lr) { lr.colorGradient = rule.BakedGradient; lr.startColor = rule.BakedColor; lr.endColor = rule.BakedColor; }
                else if (r is SpriteRenderer sr) { if (sr.color != rule.BakedColor) sr.color = rule.BakedColor; }
            }
            else if (rule.NormalizedPropName.Contains("width") || rule.NormalizedPropName.Contains("multiplier"))
            {
                if (r is TrailRenderer tr) tr.widthMultiplier = rule.BakedFloat;
                else if (r is LineRenderer lr) lr.widthMultiplier = rule.BakedFloat;
            }

            Material[] mats = r.materials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (rule.MaterialIndex >= 0 && rule.MaterialIndex != i) continue;
                Material m = mats[i];
                if (m == null) continue;

                if (rule.ParsedValue is Color)
                {
                    if (m.HasProperty("_Color")) { m.SetColor("_Color", rule.BakedColor); changed = true; }
                    if (m.HasProperty("_TintColor")) { m.SetColor("_TintColor", rule.BakedColor); changed = true; }
                    if (m.HasProperty("_EmissionColor")) { m.SetColor("_EmissionColor", rule.BakedColor); changed = true; }
                    if (m.HasProperty(rule.PropertyName)) { m.SetColor(rule.PropertyName, rule.BakedColor); changed = true; }
                }
                else if (rule.ParsedValue is Texture2D tex)
                {
                    m.mainTexture = tex; // setter routes to _MainTex or the shader's first texture property — same net effect as the old SetTexture + mainTexture pair
                    changed = true;
                }
            }
            if (changed) r.materials = mats;
        }

        private static void ApplyToLight(Light l, TargetRule rule)
        {
            if (rule.IsColorRule && rule.ParsedValue is Color) l.color = rule.BakedColor;
            if (rule.NormalizedPropName == "intensity") l.intensity = rule.BakedFloat;
        }

        private static void ApplyToParticle(ParticleSystem ps, TargetRule rule)
        {
            if (rule.IsColorRule && rule.ParsedValue is Color)
            {
                var main = ps.main; main.startColor = new ParticleSystem.MinMaxGradient(rule.BakedColor);
                var trails = ps.trails; if (trails.enabled) trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(rule.BakedColor);
            }
            if (ps.TryGetComponent<ParticleSystemRenderer>(out var psr)) ApplyToRenderer(psr, rule);
        }

        private static void ApplyToAudio(AudioSource a, TargetRule rule)
        {
            if (rule.NormalizedPropName == "clip" && rule.ParsedValue is AudioClip clip) a.clip = clip;
            else if (rule.NormalizedPropName == "volume") a.volume = rule.BakedFloat;
            else if (rule.NormalizedPropName == "pitch") a.pitch = rule.BakedFloat;
        }
    }
}