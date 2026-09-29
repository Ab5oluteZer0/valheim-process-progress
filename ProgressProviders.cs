using System;
using HarmonyLib;
using UnityEngine;

namespace ProcessProgress
{
    // Stan jednego procesu w chwili odczytu. Fraction 0..1, SecondsLeft < 0 = nieznany.
    // Warning (juz przetlumaczony) = proces stoi albo sie zeruje, pasek pokazuje wtedy ostrzezenie.
    internal struct ProgressInfo
    {
        public string Title;
        // Opcjonalny szczegol zamiast procentu (np. "2/4" miodu w ulu).
        public string Detail;
        public float Fraction;
        public double SecondsLeft;
        public string Warning;
        public bool Ready;
    }

    // Jeden rodzaj obiektu z postepem (roslina, krzak, fermentor...). Nowy rodzaj = nowa klasa,
    // bez zmian w skanowaniu i pasach. Wartosci licza prywatne funkcje samej gry, wiec pasek
    // zawsze zgadza sie z tym, co gra naprawde zrobi.
    internal interface IProgressProvider
    {
        Type ComponentType { get; }
        bool Enabled { get; }
        bool TryGetProgress(Component target, out ProgressInfo info);
    }

    internal static class ProgressText
    {
        public static string Localize(string text) =>
            Localization.instance != null ? Localization.instance.Localize(text) : text;

        public static double SecondsSince(long ticks) => (ZNet.instance.GetTime() - new DateTime(ticks)).TotalSeconds;

        public static bool TryGetZdo(Component target, out ZDO zdo)
        {
            var nview = target.GetComponent<ZNetView>();
            zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo != null;
        }
    }

    // Roslina w trakcie wzrostu: warzywa, zboza, sadzonki drzew. Czas wzrostu jest losowany z
    // przedzialu na podstawie ziarna rosliny - liczy go prywatna GetGrowTime gry.
    internal sealed class PlantProgress : IProgressProvider
    {
        private static readonly Func<Plant, float> GetGrowTime =
            AccessTools.MethodDelegate<Func<Plant, float>>(AccessTools.Method(typeof(Plant), "GetGrowTime"));
        private static readonly Func<Plant, double> TimeSincePlanted =
            AccessTools.MethodDelegate<Func<Plant, double>>(AccessTools.Method(typeof(Plant), "TimeSincePlanted"));
        private static readonly AccessTools.FieldRef<Plant, Plant.Status> StatusRef =
            AccessTools.FieldRefAccess<Plant, Plant.Status>("m_status");

        private readonly Func<bool> _enabled;
        public PlantProgress(Func<bool> enabled) => _enabled = enabled;

        public Type ComponentType => typeof(Plant);
        public bool Enabled => _enabled();

        public bool TryGetProgress(Component target, out ProgressInfo info)
        {
            info = default;
            var plant = (Plant)target;
            if (!ProgressText.TryGetZdo(plant, out _))
                return false;

            float growTime = GetGrowTime(plant);
            double elapsed = TimeSincePlanted(plant);
            info.Title = ProgressText.Localize(plant.m_name);
            info.Fraction = growTime > 0f ? Mathf.Clamp01((float)(elapsed / growTime)) : 1f;
            info.SecondsLeft = Math.Max(0.0, growTime - elapsed);
            info.Ready = info.Fraction >= 1f;
            info.Warning = StatusWarning(StatusRef(plant));
            return true;
        }

        // Te same teksty, ktore gra pokazuje po najechaniu na rosline.
        private static string StatusWarning(Plant.Status status)
        {
            string token = status switch
            {
                Plant.Status.NoSpace => "$piece_plant_nospace",
                Plant.Status.NoSun => "$piece_plant_nosun",
                Plant.Status.WrongBiome => "$piece_plant_wrongbiome",
                Plant.Status.NotCultivated => "$piece_plant_notcultivated",
                Plant.Status.TooHot => "$piece_plant_toohot",
                Plant.Status.TooCold => "$piece_plant_toocold",
                Plant.Status.NoAttachPiece => "$piece_plant_nowall",
                _ => null,
            };
            return token != null ? ProgressText.Localize(token) : null;
        }
    }

    // Zebrany krzak/roslina, ktora odrasta (jagody, grzyby, ziola). Gra zapisuje czas zebrania
    // w ZDO, a sprawdza odrosniecie co minute - stad "gotowe" moze chwile poczekac na wlasciwe odrosniecie.
    internal sealed class PickableProgress : IProgressProvider
    {
        private readonly Func<bool> _enabled;
        public PickableProgress(Func<bool> enabled) => _enabled = enabled;

        public Type ComponentType => typeof(Pickable);
        public bool Enabled => _enabled();

        public bool TryGetProgress(Component target, out ProgressInfo info)
        {
            info = default;
            var pickable = (Pickable)target;
            if (pickable.m_respawnTimeMinutes <= 0f || pickable.GetEnabled == 0)
                return false;
            if (!ProgressText.TryGetZdo(pickable, out var zdo) || !zdo.GetBool(ZDOVars.s_picked, pickable.m_defaultPicked))
                return false;

            // 0/1 = gra jeszcze nie ustalila czasu zebrania (robi to przy najblizszym sprawdzeniu).
            long pickedTicks = zdo.GetLong(ZDOVars.s_pickedTime, 0L);
            if (pickedTicks <= 1L)
                return false;

            double respawnSeconds = pickable.m_respawnTimeMinutes * 60.0;
            double elapsed = ProgressText.SecondsSince(pickedTicks);
            info.Title = PickableName(pickable);
            info.Fraction = Mathf.Clamp01((float)(elapsed / respawnSeconds));
            info.SecondsLeft = Math.Max(0.0, respawnSeconds - elapsed);
            info.Ready = info.Fraction >= 1f;
            return true;
        }

        private static string PickableName(Pickable pickable)
        {
            if (!string.IsNullOrEmpty(pickable.m_overrideName))
                return ProgressText.Localize(pickable.m_overrideName);
            var item = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
            return item != null ? ProgressText.Localize(item.m_itemData.m_shared.m_name) : pickable.name;
        }
    }

    // Ul gracza. Miod zbiera sie w poziomach 0..m_maxHoney: co 10 s gra dolicza czas od ostatniej
    // aktualizacji do s_product i po m_secPerUnit sekundach podnosi poziom. Produkcja idzie tez w
    // nocy (noc wylacza tylko efekt pszczol), stoi przy zlym biomie albo zbyt zaslonietym ulu -
    // warunki sprawdzaja prywatne CheckBiome/HaveFreeSpace gry.
    internal sealed class BeehiveProgress : IProgressProvider
    {
        private static readonly Func<Beehive, bool> CheckBiome =
            AccessTools.MethodDelegate<Func<Beehive, bool>>(AccessTools.Method(typeof(Beehive), "CheckBiome"));
        private static readonly Func<Beehive, bool> HaveFreeSpace =
            AccessTools.MethodDelegate<Func<Beehive, bool>>(AccessTools.Method(typeof(Beehive), "HaveFreeSpace"));

        private readonly Func<bool> _enabled;
        public BeehiveProgress(Func<bool> enabled) => _enabled = enabled;

        public Type ComponentType => typeof(Beehive);
        public bool Enabled => _enabled();

        public bool TryGetProgress(Component target, out ProgressInfo info)
        {
            info = default;
            var hive = (Beehive)target;
            if (!ProgressText.TryGetZdo(hive, out var zdo) || hive.m_maxHoney <= 0 || hive.m_secPerUnit <= 0f)
                return false;

            int level = Mathf.Clamp(zdo.GetInt(ZDOVars.s_level), 0, hive.m_maxHoney);
            info.Title = ProgressText.Localize(hive.m_name);
            info.Detail = $"{level}/{hive.m_maxHoney}";
            if (level >= hive.m_maxHoney)
            {
                info.Fraction = 1f;
                info.Ready = true;
                return true;
            }

            if (!CheckBiome(hive))
                info.Warning = ProgressText.Localize(hive.m_areaText);
            else if (!HaveFreeSpace(hive))
                info.Warning = ProgressText.Localize(hive.m_freespaceText);

            // Czas zebrany do nastepnego miodu = zapisany s_product + to, co gra doliczy przy
            // najblizszej aktualizacji (od s_lastTime). Gdy ul stoi, gra tego nie dolicza.
            double pending = 0.0;
            long lastTicks = zdo.GetLong(ZDOVars.s_lastTime, 0L);
            if (info.Warning == null && lastTicks > 0L)
                pending = Math.Max(0.0, ProgressText.SecondsSince(lastTicks));
            double product = Math.Min(zdo.GetFloat(ZDOVars.s_product) + pending, hive.m_secPerUnit);

            info.Fraction = Mathf.Clamp01((float)((level + product / hive.m_secPerUnit) / hive.m_maxHoney));
            info.SecondsLeft = info.Warning == null ? Math.Max(0.0, hive.m_secPerUnit - product) : -1.0;
            return true;
        }
    }

    // Fermentor. Bez dachu albo odsloniety gra co 10 s ZERUJE licznik fermentacji - pasek
    // pokazuje wtedy ostrzezenie zamiast postepu, ktory i tak by sie cofal.
    internal sealed class FermenterProgress : IProgressProvider
    {
        private static readonly Func<Fermenter, double> GetFermentationTime =
            AccessTools.MethodDelegate<Func<Fermenter, double>>(AccessTools.Method(typeof(Fermenter), "GetFermentationTime"));
        private static readonly AccessTools.FieldRef<Fermenter, bool> HasRoofRef = AccessTools.FieldRefAccess<Fermenter, bool>("m_hasRoof");
        private static readonly AccessTools.FieldRef<Fermenter, bool> ExposedRef = AccessTools.FieldRefAccess<Fermenter, bool>("m_exposed");

        private readonly Func<bool> _enabled;
        public FermenterProgress(Func<bool> enabled) => _enabled = enabled;

        public Type ComponentType => typeof(Fermenter);
        public bool Enabled => _enabled();

        public bool TryGetProgress(Component target, out ProgressInfo info)
        {
            info = default;
            var fermenter = (Fermenter)target;
            if (!ProgressText.TryGetZdo(fermenter, out var zdo))
                return false;

            // Pustosc TYLKO po zawartosci (hash skladnika, 0 = pusty), tak jak robi to gra. Po
            // nalaniu gra "zeruje" czas startu wartoscia int, a czyta go jako long - w ZDO zostaje
            // wiec stary czas i pusty fermentor wygladalby jak gotowy.
            if (zdo.GetInt(ZDOVars.s_content) == 0)
                return false;
            double elapsed = GetFermentationTime(fermenter);
            if (elapsed < 0.0)
                return false;

            info.Title = ProgressText.Localize(fermenter.m_name);
            info.Fraction = fermenter.m_fermentationDuration > 0f
                ? Mathf.Clamp01((float)(elapsed / fermenter.m_fermentationDuration))
                : 1f;
            info.SecondsLeft = Math.Max(0.0, fermenter.m_fermentationDuration - elapsed);
            info.Ready = info.Fraction >= 1f;
            if (!info.Ready)
            {
                if (!HasRoofRef(fermenter))
                    info.Warning = ProgressText.Localize("$piece_fermenter_needroof");
                else if (ExposedRef(fermenter))
                    info.Warning = ProgressText.Localize("$piece_fermenter_exposed");
            }
            return true;
        }
    }
}
