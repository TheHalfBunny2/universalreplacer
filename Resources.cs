using BepInEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace UniversalReplacer
{
    public static class ResourceManager
    {
        private class TexEntry { public Texture2D Tex; public DateTime Stamp; }
        private struct SpriteEntry { public Sprite Sprite; public Texture2D Source; }

        private static readonly Dictionary<string, TexEntry> _textures = new Dictionary<string, TexEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SpriteEntry> _sprites = new Dictionary<string, SpriteEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AudioClip> _audios = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _reportedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _basePath, _texturesDir, _audioDir;
        internal static bool AudioPreloadDone;

        public static void Init()
        {
            _basePath = Path.Combine(Paths.ConfigPath, "ColorizedInk");
            _texturesDir = Path.Combine(_basePath, "Textures");
            _audioDir = Path.Combine(_basePath, "Audio");
            try { Directory.CreateDirectory(_texturesDir); Directory.CreateDirectory(_audioDir); }
            catch (Exception e) { Plugin.Log.LogError($"ResourceManager: could not create asset folders: {e.Message}"); }
        }

        public static IEnumerator PreloadAudioClips()
        {
            if (!Directory.Exists(_audioDir)) yield break;
            foreach (string file in Directory.GetFiles(_audioDir))
            {
                AudioType type = GetAudioType(Path.GetExtension(file));
                if (type == (AudioType)(-1)) continue;

                using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, type))
                {
                    yield return req.SendWebRequest();
                    if (string.IsNullOrEmpty(req.error))
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                        if (clip != null)
                        {
                            string fileName = Path.GetFileName(file);
                            clip.name = fileName;
                            _audios[fileName] = clip;
                        }
                    }
                    else Plugin.Log.LogWarning($"Failed to load audio '{file}': {req.error}");
                }
                yield return null; // breathe between files
            }
            AudioPreloadDone = true;
        }

        public static AudioClip GetAudio(string name)
        {
            if (_audios.TryGetValue(name, out AudioClip c)) return c;
            if (AudioPreloadDone) WarnOnce("audio", name);
            return null;
        }

        public static Texture2D GetTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_textures.TryGetValue(name, out TexEntry e) && e.Tex != null) return e.Tex;
            if (_reportedMissing.Contains("tex:" + name)) return null;

            Texture2D tex = LoadTextureFromDisk(name);
            if (tex == null) { WarnOnce("tex", name); return null; }
            _textures[name] = new TexEntry { Tex = tex, Stamp = StampOf(name) };
            return tex;
        }

        public static Sprite GetSprite(string name)
        {
            if (_sprites.TryGetValue(name, out SpriteEntry entry) && entry.Sprite != null)
                if (!_textures.TryGetValue(name, out TexEntry tex) || tex.Tex == entry.Source)
                    return entry.Sprite;

            Texture2D t = GetTexture(name);
            if (t == null) return null;
            Sprite sp = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
            sp.name = name;
            _sprites[name] = new SpriteEntry { Sprite = sp, Source = t };
            return sp;
        }

        /// Called on config reload (main thread): reloads edited files, drops deleted ones, keeps the rest.
        internal static void RefreshChangedTextures()
        {
            List<string> gone = null, changed = null;
            foreach (KeyValuePair<string, TexEntry> kvp in _textures)
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(Path.Combine(_texturesDir, kvp.Key)); }
                catch { if (gone == null) gone = new List<string>(); gone.Add(kvp.Key); continue; }
                if (stamp != kvp.Value.Stamp) { if (changed == null) changed = new List<string>(); changed.Add(kvp.Key); }
            }
            if (gone != null) foreach (string n in gone) _textures.Remove(n);
            if (changed != null) foreach (string n in changed)
            {
                Texture2D t = LoadTextureFromDisk(n);
                if (t != null) _textures[n] = new TexEntry { Tex = t, Stamp = StampOf(n) };
                else _textures.Remove(n);
            }
            _reportedMissing.Clear();
        }

        /// Textures now survive scene loads (validated by timestamps); stale sprites are pruned.
        public static void ClearCaches()
        {
            List<string> stale = null;
            foreach (KeyValuePair<string, SpriteEntry> kvp in _sprites)
                if (!_textures.TryGetValue(kvp.Key, out TexEntry t) || t.Tex != kvp.Value.Source)
                { if (stale == null) stale = new List<string>(); stale.Add(kvp.Key); }
            if (stale != null) foreach (string n in stale) _sprites.Remove(n);
            _reportedMissing.Clear();
        }

        private static Texture2D LoadTextureFromDisk(string name)
        {
            string path = Path.Combine(_texturesDir, name);
            if (!File.Exists(path)) return null;
            try
            {
                Texture2D tex = new Texture2D(2, 2);
                if (ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { tex.name = name; return tex; }
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Failed to load texture '{name}': {e.Message}"); return null; }
        }

        private static DateTime StampOf(string name)
        {
            try { return File.GetLastWriteTimeUtc(Path.Combine(_texturesDir, name)); }
            catch { return DateTime.MinValue; }
        }

        private static AudioType GetAudioType(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".mp3": return AudioType.MPEG;
                default: return (AudioType)(-1);
            }
        }

        private static void WarnOnce(string kind, string name)
        {
            if (_reportedMissing.Add(kind + ":" + name))
                Plugin.Log.LogWarning($"{kind} asset '{name}' not found (looked in '{(kind == "tex" ? _texturesDir : _audioDir)}') — rules using it are skipped.");
        }
    }
}