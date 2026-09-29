using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessProgress
{
    // Paski postepu na HUD-zie gry, w tym samym kontenerze co paski zycia przeciwnikow
    // (EnemyHud.m_hudRoot): chowaja sie razem z HUD-em (Hud.IsUserHidden) i respektuja skale
    // interfejsu z ustawien - tak jak paski gry, pozycjonowane przez WorldToScreenPointScaled.
    // Paski sa brane z puli, bez tworzenia/niszczenia obiektow przy kazdym skanie.
    internal sealed class ProgressBarLayer
    {
        private const float TextWidth = 220f;
        private const float TextHeight = 32f; // dwie linie: nazwa + ostrzezenie
        private const float BarWidth = 92f;
        private const float BarHeight = 8f;
        private const float MaxAnchorHeight = 4f;
        // Odstep paska nad wierzcholkiem obiektu (metry).
        private const float AnchorGap = 0.15f;

        private static readonly Color ProgressColor = new Color(0.45f, 0.78f, 0.32f, 1f);
        private static readonly Color ReadyColor = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color WarningColor = new Color(0.9f, 0.3f, 0.22f, 1f);
        private static readonly Color TextColor = new Color(0.8529f, 0.725f, 0.5331f, 1f);
        private static readonly Color WarningTextColor = new Color(1f, 0.55f, 0.45f, 1f);

        private sealed class BarView
        {
            public GameObject Root;
            public RectTransform Rect;
            public RectTransform FillRect;
            public Image Fill;
            public TextMeshProUGUI Text;
            public Component Target;
            public IProgressProvider Provider;
            public float AnchorHeight;
            public bool HasInfo;
        }

        private readonly RectTransform _root;
        private readonly TMP_FontAsset _font;
        private readonly Stack<BarView> _pool = new Stack<BarView>();
        private readonly Dictionary<Component, BarView> _bars = new Dictionary<Component, BarView>();
        private readonly List<Component> _toRelease = new List<Component>();

        private ProgressBarLayer(RectTransform root, TMP_FontAsset font)
        {
            _root = root;
            _font = font;
        }

        // null, dopoki HUD gry nie istnieje (menu glowne, wczytywanie swiata).
        public static ProgressBarLayer TryCreate(TMP_FontAsset font)
        {
            var hud = EnemyHud.instance;
            if (hud == null || hud.m_hudRoot == null)
                return null;
            var go = new GameObject("ProcessProgressBars", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(hud.m_hudRoot.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return new ProgressBarLayer(rect, font);
        }

        // HUD gry jest niszczony przy wyjsciu ze swiata - razem z nim ta warstwa.
        public bool IsAlive => _root != null;

        public void Sync(Dictionary<Component, IProgressProvider> targets)
        {
            _toRelease.Clear();
            foreach (var kv in _bars)
                if (kv.Key == null || !targets.ContainsKey(kv.Key))
                    _toRelease.Add(kv.Key);
            foreach (var target in _toRelease)
                Release(target);

            foreach (var kv in targets)
            {
                if (kv.Key == null || _bars.ContainsKey(kv.Key))
                    continue;
                var bar = _pool.Count > 0 ? _pool.Pop() : CreateBar();
                bar.Target = kv.Key;
                bar.Provider = kv.Value;
                bar.AnchorHeight = AnchorHeight(kv.Key);
                bar.HasInfo = false;
                _bars[kv.Key] = bar;
            }
        }

        public void Refresh()
        {
            _toRelease.Clear();
            foreach (var kv in _bars)
            {
                var bar = kv.Value;
                if (kv.Key == null)
                {
                    _toRelease.Add(kv.Key);
                    continue;
                }
                ProgressInfo info = default;
                bar.HasInfo = bar.Provider.Enabled && bar.Provider.TryGetProgress(kv.Key, out info);
                if (bar.HasInfo)
                    Apply(bar, info);
            }
            foreach (var target in _toRelease)
                Release(target);
        }

        public void UpdatePositions(Camera camera, Vector3 playerPos, float range)
        {
            foreach (var kv in _bars)
            {
                var bar = kv.Value;
                if (kv.Key == null || !bar.HasInfo)
                {
                    SetActive(bar, false);
                    continue;
                }

                Vector3 world = kv.Key.transform.position + Vector3.up * bar.AnchorHeight;
                float distance = Vector3.Distance(playerPos, world);
                Vector3 screen = camera.WorldToScreenPointScaled(world);
                bool onScreen = distance <= range && screen.z > 0f &&
                                screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height;
                SetActive(bar, onScreen);
                if (!onScreen)
                    continue;

                bar.Rect.position = screen;
                // Dalsze paski troche mniejsze - czytelna glebia, mniej zasloniety ekran.
                float scale = Mathf.Lerp(1f, 0.75f, distance / Mathf.Max(range, 0.01f));
                bar.Rect.localScale = new Vector3(scale, scale, 1f);
            }
        }

        public void Clear()
        {
            // Po wyjsciu ze swiata gra niszczy HUD razem z paskami - zostaja same martwe referencje.
            if (!IsAlive)
            {
                _bars.Clear();
                _pool.Clear();
                return;
            }
            _toRelease.Clear();
            _toRelease.AddRange(_bars.Keys);
            foreach (var target in _toRelease)
                Release(target);
        }

        private void Release(Component target)
        {
            if (!_bars.TryGetValue(target, out var bar))
                return;
            _bars.Remove(target);
            bar.Target = null;
            bar.Provider = null;
            bar.HasInfo = false;
            SetActive(bar, false);
            _pool.Push(bar);
        }

        private static void SetActive(BarView bar, bool active)
        {
            if (bar.Root == null) // zniszczony razem z HUD-em gry
                return;
            if (bar.Root.activeSelf != active)
                bar.Root.SetActive(active);
        }

        private static void Apply(BarView bar, ProgressInfo info)
        {
            Color fillColor;
            Color textColor = TextColor;
            string text;
            if (info.Warning != null)
            {
                fillColor = WarningColor;
                textColor = WarningTextColor;
                // Ostrzezenie w drugiej linii - komunikaty gry sa za dlugie na jedna.
                text = $"{info.Title}\n{info.Warning}";
            }
            else if (info.Ready)
            {
                fillColor = ReadyColor;
                text = $"{info.Title}: {ProgressText.Localize("$piece_fermenter_ready")}";
            }
            else
            {
                fillColor = ProgressColor;
                string time = info.SecondsLeft >= 0.0 ? $" · {FormatDuration(info.SecondsLeft)}" : "";
                string amount = info.Detail ?? $"{Mathf.FloorToInt(info.Fraction * 100f)}%";
                text = $"{info.Title} {amount}{time}";
            }

            if (bar.Text.text != text)
                bar.Text.text = text;
            bar.Text.color = textColor;
            bar.Fill.color = fillColor;
            bar.FillRect.anchorMax = new Vector2(info.Warning != null && !info.Ready ? Mathf.Max(info.Fraction, 0.02f) : info.Fraction, 1f);
        }

        private static string FormatDuration(double seconds)
        {
            const int secondsPerHour = 3600;
            int total = Mathf.CeilToInt((float)seconds);
            if (total >= secondsPerHour)
                return $"{total / secondsPerHour}h {total % secondsPerHour / 60}m";
            if (total >= 60)
                return $"{Mathf.CeilToInt(total / 60f)}m";
            return $"{total}s";
        }

        // Pasek nad wierzchem WIDOCZNEGO modelu obiektu (siatki), a nie nad jego pivotem na ziemi.
        // Kolidery zawodzily przy fermentorze - jego niewidoczne strefy siegaja wysoko ponad
        // beczke. Efekty czasteczkowe (babelki fermentacji) pomijane. Kolidery (bez wyzwalaczy)
        // tylko wtedy, gdy obiekt nie ma zadnej widocznej siatki.
        private static float AnchorHeight(Component target)
        {
            float baseY = target.transform.position.y;
            float top = float.NegativeInfinity;
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
                if (renderer.enabled && renderer.gameObject.activeInHierarchy &&
                    (renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                    top = Mathf.Max(top, renderer.bounds.max.y);

            if (float.IsNegativeInfinity(top))
                foreach (var collider in target.GetComponentsInChildren<Collider>())
                    if (collider.enabled && !collider.isTrigger)
                        top = Mathf.Max(top, collider.bounds.max.y);

            float height = float.IsNegativeInfinity(top) ? 0.5f : Mathf.Max(top - baseY, 0.3f);
            return Mathf.Min(height, MaxAnchorHeight) + AnchorGap;
        }

        private BarView CreateBar()
        {
            var root = new GameObject("ProgressBar", typeof(RectTransform));
            var rect = root.GetComponent<RectTransform>();
            rect.SetParent(_root, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(TextWidth, TextHeight + BarHeight + 2f);

            var bg = CreateImage("Background", rect, new Color(0f, 0f, 0f, 0.7f));
            bg.anchorMin = bg.anchorMax = new Vector2(0.5f, 0f);
            bg.pivot = new Vector2(0.5f, 0f);
            bg.sizeDelta = new Vector2(BarWidth, BarHeight);
            bg.anchoredPosition = Vector2.zero;

            var fillRect = CreateImage("Fill", bg, ProgressColor);
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.offsetMin = new Vector2(1f, 1f);
            fillRect.offsetMax = new Vector2(-1f, -1f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.SetParent(rect, false);
            textRect.anchorMin = textRect.anchorMax = new Vector2(0.5f, 0f);
            textRect.pivot = new Vector2(0.5f, 0f);
            textRect.sizeDelta = new Vector2(TextWidth, TextHeight);
            textRect.anchoredPosition = new Vector2(0f, BarHeight + 1f);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            if (_font != null)
                text.font = _font;
            text.fontSize = 13f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 9f;
            text.fontSizeMax = 13f;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Bottom;
            text.overflowMode = TextOverflowModes.Overflow;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.outlineWidth = 0.2f;
            text.outlineColor = new Color32(20, 12, 5, 255);
            text.raycastTarget = false;

            root.SetActive(false);
            return new BarView
            {
                Root = root,
                Rect = rect,
                FillRect = fillRect,
                Fill = fillRect.GetComponent<Image>(),
                Text = text,
            };
        }

        private static RectTransform CreateImage(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }
    }
}
