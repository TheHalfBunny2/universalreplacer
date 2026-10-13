using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniversalReplacer
{
    [BepInPlugin("com.thehalfbunny.universalreplacer", "Universal Replacer", "2.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; }
        public static Plugin Instance { get; private set; }

        private Harmony _harmony;
        private ConfigManager _config;
        private RuleProcessor _processor;

        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly Queue<GameObject> _scanQueue = new Queue<GameObject>();
        private readonly HashSet<GameObject> _queuedSet = new HashSet<GameObject>();

        // Object-backed processed map: dead entries are detected via Unity's overloaded null,
        // so recycled instance IDs can no longer cause new objects to be silently skipped.
        private readonly Dictionary<int, GameObject> _processedMap = new Dictionary<int, GameObject>();
        private static readonly List<int> _pruneScratch = new List<int>(256);
        private int _lastPruneFrame;

        private static readonly System.Diagnostics.Stopwatch _scanBudget = new System.Diagnostics.Stopwatch();
        private static readonly WaitForSeconds _enforceInterval = new WaitForSeconds(0.3f);

        public bool IsBursting = false;
        private float _burstCooldown = 0f;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            try
            {
                ResourceManager.Init();
                _config = new ConfigManager(Path.Combine(Paths.ConfigPath, "ColorizedInk.json"));
                _processor = new RuleProcessor(_config);

                _harmony = new Harmony(Info.Metadata.GUID);
                _harmony.PatchAll(typeof(HarmonyPatcher));

                Log.LogInfo("Universal Replacer loaded! (Apex Edition)");
                StartCoroutine(ResourceManager.PreloadAudioClips());
            }
            catch (Exception e) { Log.LogError($"Awake error: {e}"); }
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (_config != null) { _config.EnableWatcher(true); _config.OnConfigReloaded += ReapplyAll; }
            StartCoroutine(EnforcerLoop());
            if (SceneManager.GetActiveScene().isLoaded) ReapplyAll();
        }

        private void OnDisable()
        {
            // Coroutines stop automatically when the behaviour disables.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_config != null) { _config.OnConfigReloaded -= ReapplyAll; _config.EnableWatcher(false); }
        }

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out Action action)) action?.Invoke();
            _config?.Tick();

            if (IsBursting)
            {
                _burstCooldown -= Time.deltaTime;
                if (_burstCooldown <= 0f) { IsBursting = false; _processor?.ScanFullScene(); }
                return;
            }

            if (_scanQueue.Count > 0)
            {
                _scanBudget.Restart();
                while (_scanQueue.Count > 0 && _scanBudget.Elapsed.TotalMilliseconds < 5.0)
                {
                    GameObject go = _scanQueue.Dequeue();
                    _queuedSet.Remove(go);
                    if (go != null) _processor?.ProcessObject(go);
                }
                _scanBudget.Stop();
            }

            if (_processedMap.Count > 4096 && Time.frameCount - _lastPruneFrame > 300) PruneProcessed();

            if (Input.GetKeyDown(KeyCode.F6)) _config?.ReloadConfig(true);
        }

        public void HandleNewObjectInstant(GameObject go)
        {
            if (go == null || _config == null || IsBursting) return;
            InstantProcessor.ApplyInstant(go, _config.Rules);
            if (_queuedSet.Add(go)) _scanQueue.Enqueue(go);
        }

        public bool HasProcessed(GameObject go) => _processedMap.TryGetValue(go.GetInstanceID(), out GameObject stored) && stored != null;
        public void MarkProcessed(GameObject go) => _processedMap[go.GetInstanceID()] = go;

        private void PruneProcessed()
        {
            _lastPruneFrame = Time.frameCount;
            _pruneScratch.Clear();
            foreach (KeyValuePair<int, GameObject> kvp in _processedMap)
                if (kvp.Value == null) _pruneScratch.Add(kvp.Key);
            for (int i = 0; i < _pruneScratch.Count; i++) _processedMap.Remove(_pruneScratch[i]);
        }

        private void ReapplyAll()
        {
            _processor?.RevertAll();
            ResourceManager.ClearCaches();
            _scanQueue.Clear();
            _queuedSet.Clear();
            _processedMap.Clear();
            IsBursting = true;
            _burstCooldown = 0.5f;
            _processor?.ScanFullScene();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ReapplyAll();

        private IEnumerator EnforcerLoop()
        {
            while (true) { yield return _enforceInterval; if (!IsBursting) _processor?.EnforceActiveRules(); }
        }

        public static void EnqueueAction(Action a) { if (a != null) _mainThreadQueue.Enqueue(a); }
    }
}