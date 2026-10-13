using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UniversalReplacer
{
    public class TargetRule
    {
        public string Root;
        public string RootContains;
        public string Path;
        public string PathContains;
        public string ComponentType;
        public string PropertyName;
        public int MaterialIndex = -1;
        public JToken Value;

        [JsonIgnore] public string[] RootContainsParts;
        [JsonIgnore] public string[] PathParts;
        [JsonIgnore] public bool IsDeepSearch;
        [JsonIgnore] public string CleanDeepTarget;
        [JsonIgnore] public string CleanPathContains;
        [JsonIgnore] public string NormalizedPropName;
        [JsonIgnore] public bool IsColorRule;

        [JsonIgnore] public object ParsedValue;
        [JsonIgnore] public Color BakedColor;
        [JsonIgnore] public Gradient BakedGradient;
        [JsonIgnore] public float BakedFloat;
        [JsonIgnore] public string BakedString;
        [JsonIgnore] public bool BakedBool;

        private static readonly char[] Slash = { '/' };
        private static readonly string[] DoubleSlash = { "//" };

        public void Init()
        {
            if (!string.IsNullOrEmpty(RootContains))
                RootContainsParts = RootContains.Split(Slash, StringSplitOptions.RemoveEmptyEntries);

            CleanPathContains = PathContains?.Replace("//", "");

            string pathData = !string.IsNullOrEmpty(Path) ? Path : PathContains;
            if (!string.IsNullOrEmpty(pathData))
            {
                if (pathData.Contains("//"))
                {
                    IsDeepSearch = true;
                    string[] split = pathData.Split(DoubleSlash, StringSplitOptions.None);
                    if (split.Length > 2)
                        Plugin.Log.LogWarning($"Rule '{PropertyName}': path '{pathData}' has multiple '//' segments; only '{split[1]}' is used.");
                    CleanDeepTarget = split.Length > 1 ? split[1].TrimEnd('*', '/') : "";
                    PathParts = split[0].Split(Slash, StringSplitOptions.RemoveEmptyEntries);
                }
                else
                    PathParts = pathData.TrimEnd('*', '/').Split(Slash, StringSplitOptions.RemoveEmptyEntries);
            }

            NormalizedPropName = (PropertyName ?? "").ToLowerInvariant()
                .Replace("_", "").Replace("behavior.", "").Replace("behaviour.", "");
            IsColorRule = NormalizedPropName.Contains("color") || NormalizedPropName.Contains("tint");
            BakeValue();
        }

        private void BakeValue()
        {
            if (Value == null) return;
            try
            {
                switch (Value.Type)
                {
                    case JTokenType.Object:
                        BakedColor = new Color(Comp("r") ?? Comp("x") ?? 0f,
                                               Comp("g") ?? Comp("y") ?? 0f,
                                               Comp("b") ?? Comp("z") ?? 0f,
                                               Comp("a") ?? Comp("w") ?? 1f);
                        ParsedValue = BakedColor;
                        BakedGradient = new Gradient();
                        BakedGradient.SetKeys(
                            new[] { new GradientColorKey(BakedColor, 0f), new GradientColorKey(BakedColor, 1f) },
                            new[] { new GradientAlphaKey(BakedColor.a, 0f), new GradientAlphaKey(BakedColor.a, 1f) });
                        break;

                    case JTokenType.String:
                        BakedString = (string)Value;
                        string s = BakedString.ToLowerInvariant();
                        if (s.EndsWith(".png") || s.EndsWith(".jpg")) ParsedValue = ResourceManager.GetTexture(BakedString);
                        else if (s.EndsWith(".wav") || s.EndsWith(".mp3") || s.EndsWith(".ogg")) ParsedValue = ResourceManager.GetAudio(BakedString);
                        else ParsedValue = BakedString;
                        break;

                    case JTokenType.Float:
                    case JTokenType.Integer:
                        BakedFloat = Value.Value<float>();
                        ParsedValue = BakedFloat;
                        break;

                    case JTokenType.Boolean:
                        BakedBool = Value.Value<bool>();
                        ParsedValue = BakedBool;
                        break;

                    default:
                        ParsedValue = Value.ToObject<object>();
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Rule '{PropertyName}': unreadable Value ({e.Message}) — rule will not apply.");
            }

            float? Comp(string key)
            {
                JToken t = Value[key];
                return t != null && t.Type != JTokenType.Null ? t.Value<float>() : (float?)null;
            }
        }
    }
}