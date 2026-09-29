using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TMPro;
using UnityEngine;

namespace ProcessProgress
{
    // Paski postepu nad obiektami w swiecie: rosliny w trakcie wzrostu (warzywa, zboza, sadzonki
    // drzew), zebrane krzaki do odrosniecia (jagody, grzyby, ziola), fermentory i ule - z procentem,
    // czasem do gotowosci albo ostrzezeniem (brak slonca, potrzebny dach...), tylko w zasiegu
    // gracza. Skan okolicy co ScanInterval, odczyt wartosci co RefreshInterval, pozycje pasow co
    // klatke (LateUpdate, po ruchu kamery) - czesciej nie ma sensu, a to glowny koszt moda.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class ProcessProgressPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.michal.valheim.processprogress";
        public const string PluginName = "Process Progress";
        public const string PluginVersion = "1.0.2";

        private const float ScanInterval = 1f;
        private const float RefreshInterval = 0.5f;

        internal static ManualLogSource Log;

        private ConfigEntry<float> _range;
        private ConfigEntry<bool> _showPlants;
        private ConfigEntry<bool> _showPickables;
        private ConfigEntry<bool> _showFermenters;
        private ConfigEntry<bool> _showBeehives;

        private IProgressProvider[] _providers;
        private ProgressBarLayer _bars;
        // Poczatkowy rozmiar - podwajany, gdy w zasiegu jest wiecej koliderow.
        private const int InitialScanBufferSize = 256;
        private Collider[] _scanBuffer = new Collider[InitialScanBufferSize];
        private readonly Dictionary<Component, IProgressProvider> _targets = new Dictionary<Component, IProgressProvider>();
        private float _scanTimer = ScanInterval;
        private float _refreshTimer;
        private bool _faulted;

        private void Awake()
        {
            Log = Logger;
            // Gra prosi mody o ustawienie tej flagi: w menu pojawia sie napis, ze gra jest
            // zmodowana (Iron Gate wymaga oznaczania modow jako nieoficjalnych).
            Game.isModded = true;
            _range = Config.Bind("General", "Range", 10f,
                new ConfigDescription("How far from you (meters) progress bars are shown.", new AcceptableValueRange<float>(3f, 40f)));
            _showPlants = Config.Bind("General", "ShowPlants", true, "Growing plants: crops and tree saplings.");
            _showPickables = Config.Bind("General", "ShowPickables", true, "Picked bushes and plants regrowing: berries, mushrooms, herbs.");
            _showFermenters = Config.Bind("General", "ShowFermenters", true, "Fermenters.");
            _showBeehives = Config.Bind("General", "ShowBeehives", true, "Beehives: honey stored and time to the next one.");

            _providers = new IProgressProvider[]
            {
                new PlantProgress(() => _showPlants.Value),
                new PickableProgress(() => _showPickables.Value),
                new FermenterProgress(() => _showFermenters.Value),
                new BeehiveProgress(() => _showBeehives.Value),
            };
        }

        private void Update()
        {
            if (_faulted)
                return;
            try
            {
                RunUpdate();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private void LateUpdate()
        {
            if (_faulted || _bars == null || !_bars.IsAlive || Player.m_localPlayer == null)
                return;
            try
            {
                var camera = Utils.GetMainCamera();
                if (camera != null)
                    _bars.UpdatePositions(camera, Player.m_localPlayer.transform.position, _range.Value);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        // Wyjatek co klatke zapchalby log i FPS - mod wylacza sie po pierwszym i mowi dlaczego.
        private void Fault(Exception e)
        {
            _faulted = true;
            Log.LogError($"Process Progress wylaczony po nieoczekiwanym bledzie: {e}");
            try
            {
                _bars?.Clear();
            }
            catch (Exception clearError)
            {
                Log.LogError($"Nie udalo sie schowac paskow po bledzie: {clearError}");
            }
        }

        private void RunUpdate()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null)
            {
                _bars?.Clear();
                _targets.Clear();
                return;
            }

            if (_bars == null || !_bars.IsAlive)
            {
                _bars = ProgressBarLayer.TryCreate(FindGameFont());
                if (_bars == null)
                    return;
            }

            _scanTimer += Time.deltaTime;
            if (_scanTimer >= ScanInterval)
            {
                _scanTimer = 0f;
                Scan();
                _bars.Sync(_targets);
                _refreshTimer = RefreshInterval;
            }

            _refreshTimer += Time.deltaTime;
            if (_refreshTimer >= RefreshInterval)
            {
                _refreshTimer = 0f;
                _bars.Refresh();
            }
        }

        private void Scan()
        {
            _targets.Clear();
            if (!_providers.Any(p => p.Enabled))
                return;

            Vector3 center = Player.m_localPlayer.transform.position + Vector3.up;
            float range = _range.Value;
            int count = Physics.OverlapSphereNonAlloc(center, range, _scanBuffer);
            if (count >= _scanBuffer.Length)
            {
                Array.Resize(ref _scanBuffer, _scanBuffer.Length * 2);
                count = Physics.OverlapSphereNonAlloc(center, range, _scanBuffer);
            }

            for (int i = 0; i < count; i++)
            {
                var hit = _scanBuffer[i];
                if (hit == null)
                    continue;
                foreach (var provider in _providers)
                {
                    if (!provider.Enabled)
                        continue;
                    var component = hit.GetComponentInParent(provider.ComponentType);
                    if (component == null)
                        continue;
                    _targets[component] = provider;
                    break;
                }
            }
        }

        // Ta sama czcionka co napisy HUD-u gry.
        private static TMP_FontAsset FindGameFont()
        {
            var live = FindAnyObjectByType<TextMeshProUGUI>();
            if (live != null && live.font != null)
                return live.font;
            return TMP_Settings.defaultFontAsset;
        }
    }
}
