using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HG;
using RoR2;
using RoR2.UI;
using EntityStates.Drone;
using UnityEngine;
using UnityEngine.UI;
using RiskOfOptions;

namespace AdjustedDrones;

// Main plugin.
[BepInDependency("com.bepis.r2api", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.rune580.riskofoptions")]
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class AdjustedDrones : BaseUnityPlugin
{
    public const string PluginAuthor = "AdoptedFatty";
    public const string PluginName = "TempDrones";
    public const string PluginGUID = PluginAuthor + "." + PluginName;
    public const string PluginVersion = "1.0.0";

    internal static AdjustedDrones Instance { get; private set; }

    internal static ConfigEntry<bool> ModEnabled;
    internal static ConfigEntry<bool> DisableDroneAggro;
    internal static ConfigEntry<float> DifficultyExponent;
    internal static ConfigEntry<float> DifficultyCoefficientMultiplier;
    internal static ConfigEntry<float> RepairCostMultiplier;
    internal static ConfigEntry<float> DroneBaseCostMultiplier;
    internal static readonly Dictionary<(DroneUptimeRarityTier tier, DroneUptimeTierClass tierClass), ConfigEntry<float>> DurationSeconds = new();
    internal static readonly Dictionary<DroneIndex, ConfigEntry<float>> PerDroneDurationSeconds = new();

    private static bool appliedGlobalDroneCostMultipliers;
    private static readonly HashSet<ConfigEntryBase> riskOfOptionsRegistered = new();

    private void Awake()
    {
        Instance = this;
        Log.Init(Logger);
        BindConfig();

        if (!ModEnabled.Value)
        {
            Log.Info("AdjustedDrones is disabled via config (General.Enabled=false). Restart required to apply.");
            return;
        }

        ApplyGlobalDroneCostMultipliers();
        TryRegisterRiskOfOptions();
        StartCoroutine(InitializePerDroneConfigsWhenReady());

        CharacterBody.onBodyStartGlobal += OnBodyStartGlobal;
        On.RoR2.BullseyeSearch.GetResults += BullseyeSearch_GetResults;
        On.RoR2.UI.AllyCardController.Awake += AllyCardController_Awake;
        Run.onRunStartGlobal += Run_onRunStartGlobal;
    }

    private System.Collections.IEnumerator InitializePerDroneConfigsWhenReady()
    {
        // DroneCatalog is populated by RoR2 SystemInitializer. If we run too early in Awake(), it can still be empty,
        // which causes missing per-drone config entries. Wait until it is ready.
        const float timeoutSeconds = 10f;
        float startTime = Time.unscaledTime;
        while (DroneCatalog.droneCount <= 0 && (Time.unscaledTime - startTime) < timeoutSeconds)
        {
            yield return null;
        }

        InitializePerDroneConfigs();
        TryRegisterRiskOfOptions();
    }

    private void Run_onRunStartGlobal(Run run)
    {
        // Ensure all DroneDefs are present and configs exist for every drone.
        InitializePerDroneConfigs();
        TryRegisterRiskOfOptions();
    }

    private void OnDestroy()
    {
        CharacterBody.onBodyStartGlobal -= OnBodyStartGlobal;
        On.RoR2.BullseyeSearch.GetResults -= BullseyeSearch_GetResults;
        On.RoR2.UI.AllyCardController.Awake -= AllyCardController_Awake;
        Run.onRunStartGlobal -= Run_onRunStartGlobal;
    }

    private void TryRegisterRiskOfOptions()
    {
        // RiskOfOptions does NOT automatically show BepInEx configs; options must be registered.
        // We use reflection so the mod still compiles/runs without RiskOfOptions installed.
        try
        {
            var riskOfOptionsAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "RiskOfOptions", StringComparison.OrdinalIgnoreCase));
            if (riskOfOptionsAssembly == null)
            {
                return;
            }

            Type managerType = riskOfOptionsAssembly.GetType("RiskOfOptions.ModSettingsManager", throwOnError: false);
            if (managerType == null)
            {
                return;
            }

            MethodInfo addOptionMethod = managerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddOption" && m.GetParameters().Length == 1);
            if (addOptionMethod == null)
            {
                return;
            }

            // Core options.
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, ModEnabled);
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, DisableDroneAggro);
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, DifficultyExponent);
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, DifficultyCoefficientMultiplier);
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, RepairCostMultiplier);
            RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, DroneBaseCostMultiplier);

            // Tier/class durations.
            foreach (var kvp in DurationSeconds)
            {
                RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, kvp.Value);
            }

            // Per-drone overrides.
            foreach (var kvp in PerDroneDurationSeconds)
            {
                RegisterRiskOfOptionsEntry(riskOfOptionsAssembly, addOptionMethod, kvp.Value);
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    private static void RegisterRiskOfOptionsEntry(Assembly riskOfOptionsAssembly, MethodInfo addOptionMethod, ConfigEntryBase entry)
    {
        if (entry == null)
        {
            return;
        }

        if (!riskOfOptionsRegistered.Add(entry))
        {
            return;
        }

        Type entryValueType = entry.SettingType;
        string[] candidateOptionTypeNames;

        if (entryValueType == typeof(bool))
        {
            candidateOptionTypeNames = new[] { "RiskOfOptions.Options.CheckBoxOption" };
        }
        else if (entryValueType == typeof(float))
        {
            // Prefer input-field style options if present (supports -1 and 0).
            candidateOptionTypeNames = new[]
            {
                "RiskOfOptions.Options.FloatInputFieldOption",
                "RiskOfOptions.Options.FloatFieldOption",
                "RiskOfOptions.Options.StepSliderOption",
                "RiskOfOptions.Options.FloatSliderOption",
                "RiskOfOptions.Options.SliderOption",
            };
        }
        else if (entryValueType == typeof(int))
        {
            candidateOptionTypeNames = new[]
            {
                "RiskOfOptions.Options.IntInputFieldOption",
                "RiskOfOptions.Options.IntFieldOption",
                "RiskOfOptions.Options.IntSliderOption",
            };
        }
        else
        {
            return;
        }

        foreach (string typeName in candidateOptionTypeNames)
        {
            Type optionType = riskOfOptionsAssembly.GetType(typeName, throwOnError: false);
            if (optionType == null)
            {
                continue;
            }

            object optionInstance = TryCreateRiskOfOptionsOptionInstance(optionType, entry);
            if (optionInstance == null)
            {
                continue;
            }

            addOptionMethod.Invoke(null, new[] { optionInstance });
            return;
        }
    }

    private static object TryCreateRiskOfOptionsOptionInstance(Type optionType, ConfigEntryBase entry)
    {
        foreach (var ctor in optionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length == 1)
            {
                if (parameters[0].ParameterType.IsAssignableFrom(entry.GetType()) || parameters[0].ParameterType == typeof(ConfigEntryBase))
                {
                    return ctor.Invoke(new object[] { entry });
                }
            }
            else if (parameters.Length == 2)
            {
                if (!(parameters[0].ParameterType.IsAssignableFrom(entry.GetType()) || parameters[0].ParameterType == typeof(ConfigEntryBase)))
                {
                    continue;
                }

                object configObj = null;
                try
                {
                    configObj = Activator.CreateInstance(parameters[1].ParameterType);
                }
                catch
                {
                    configObj = null;
                }

                try
                {
                    return ctor.Invoke(new object[] { entry, configObj });
                }
                catch
                {
                    // try next
                }
            }
        }

        return null;
    }

    private void BindConfig()
    {
        ModEnabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Enable/disable the AdjustedDrones mod. Requires restart."
        );

        DisableDroneAggro = Config.Bind(
            "Aggro",
            "DisableDroneAggro",
            true,
            "If true, enemies will not target player-owned drones/turrets (they won't draw aggro)."
        );

        DifficultyExponent = Config.Bind(
            "Scaling",
            "DifficultyCoefficientExponent",
            1f,
            "Effective duration = baseDuration / ((difficultyCoefficient * multiplier) ^ exponent). Uses Run.difficultyCoefficient only. Updates dynamically as the run difficulty ramps. Set to 0 to disable scaling."
        );

        DifficultyCoefficientMultiplier = Config.Bind(
            "Scaling",
            "DifficultyCoefficientMultiplier",
            1f,
            "Multiplier applied to Run.difficultyCoefficient before exponentiation. 1 = unchanged."
        );

        RepairCostMultiplier = Config.Bind(
            "Repair",
            "RepairCostMultiplier",
            0.5f,
            "Multiplier applied to the base repair cost of broken drone interactables 0.5 = half price."
        );

        DroneBaseCostMultiplier = Config.Bind(
            "Repair",
            "DroneBaseCostMultiplier",
            0.5f,
            "Multiplier applied to the base purchase cost of drone purchase interactables (non-broken). 0.5 = half price."
        );

        // Only 3 drone tiers: white/green/red.
        // Defaults: higher rarity + combat => lower duration.
        BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Utility, 240f);
        BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Combat, 180f);
        BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Utility, 180f);
        BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Combat, 135f);
        BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Utility, 120f);
        BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Combat, 90f);
    }

    private void InitializePerDroneConfigs()
    {
        // Create config entries for all drones at startup instead of lazily.
        // IMPORTANT: iterate the catalog, not the enum, so we only target real drone defs.
        for (int i = 0; i < DroneCatalog.droneCount; i++)
        {
            DroneDef droneDef = DroneCatalog.GetDroneDef((DroneIndex)i);
            if (droneDef != null)
            {
                EnsurePerDroneEntry(droneDef);
            }
        }
    }

    private void ApplyGlobalDroneCostMultipliers()
    {
        if (appliedGlobalDroneCostMultipliers)
        {
            return;
        }
        appliedGlobalDroneCostMultipliers = true;

        // Adjust the prefab costs up-front so the displayed price is correct everywhere.
        // This avoids per-instance hooks and ensures any future spawns inherit the changed base cost.
        try
        {
            var cards = Resources.LoadAll<InteractableSpawnCard>("SpawnCards/InteractableSpawnCard");
            if (cards == null || cards.Length == 0)
            {
                return;
            }

            float baseMult = Mathf.Max(0f, DroneBaseCostMultiplier.Value);
            float repairMult = Mathf.Max(0f, RepairCostMultiplier.Value);

            foreach (var card in cards)
            {
                if (!card || !card.prefab)
                {
                    continue;
                }

                var purchase = card.prefab.GetComponent<PurchaseInteraction>();
                if (!purchase || !purchase.isDrone)
                {
                    continue;
                }

                bool isBroken = !string.IsNullOrEmpty(card.name) && card.name.StartsWith("iscBroken", StringComparison.OrdinalIgnoreCase);
                float mult = isBroken ? repairMult : baseMult;
                if (Mathf.Approximately(mult, 1f))
                {
                    continue;
                }

                purchase.cost = Mathf.Max(0, Mathf.RoundToInt(purchase.cost * mult));
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    private void BindDuration(DroneUptimeRarityTier tier, DroneUptimeTierClass tierClass, float defaultSeconds)
    {
        var entry = Config.Bind(
            $"Duration.{tier}",
            $"{tierClass}Seconds",
            defaultSeconds,
            "Base active duration in seconds before the drone/turret breaks and requires repair. Set to 0 for infinite duration (no timer UI)."
        );
        DurationSeconds[(tier, tierClass)] = entry;
    }

    internal ConfigEntry<float> EnsurePerDroneEntry(DroneDef droneDef)
    {
        DroneIndex idx = droneDef ? droneDef.droneIndex : DroneIndex.None;
        if (idx == DroneIndex.None)
        {
            return null;
        }
        if (PerDroneDurationSeconds.TryGetValue(idx, out var existing))
        {
            return existing;
        }

        string safeName = GetSafeDroneConfigKeyName(droneDef);
        string displayName = GetDroneDisplayNameForConfig(droneDef);
        var entry = Config.Bind(
            "Overrides.PerDrone",
            safeName + "Seconds",
            -1f,
            $"Per-drone duration override for '{displayName}' in seconds. Set to -1 to use the tier/class duration. Set to 0 for infinite duration (no timer UI)."
        );
        PerDroneDurationSeconds[idx] = entry;
        return entry;
    }

    private static string GetDroneDisplayNameForConfig(DroneDef droneDef)
    {
        if (!droneDef)
        {
            return "Unknown Drone";
        }

        // Prefer the localized name token.
        if (!string.IsNullOrWhiteSpace(droneDef.nameToken))
        {
            string localized = Language.GetString(droneDef.nameToken);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
            return droneDef.nameToken;
        }

        if (droneDef.bodyPrefab)
        {
            return droneDef.bodyPrefab.name;
        }

        return $"Drone {(int)droneDef.droneIndex}";
    }

    private static string GetSafeDroneConfigKeyName(DroneDef droneDef)
    {
        // Prefer stable id: droneIndex + body prefab name.
        string bodyName = "UnknownBody";
        if (droneDef && droneDef.bodyPrefab)
        {
            bodyName = droneDef.bodyPrefab.name;
        }
        return $"Drone_{(int)droneDef.droneIndex}_{bodyName}";
    }

    private void OnBodyStartGlobal(CharacterBody body)
    {
        try
        {
            if (!body || !body.teamComponent)
            {
                return;
            }

            // Only apply to player-owned drones/turrets.
            if (body.teamComponent.teamIndex != TeamIndex.Player)
            {
                return;
            }

            if (!body.master || !body.master.minionOwnership || !body.master.minionOwnership.ownerMaster)
            {
                return;
            }
            if (!body.master.minionOwnership.ownerMaster.playerCharacterMasterController)
            {
                return;
            }

            DroneIndex droneIndex = DroneCatalog.GetDroneIndexFromBodyIndex(body.bodyIndex);
            if (droneIndex == DroneIndex.None)
            {
                return;
            }

            var timer = body.GetComponent<DroneUptimeTimer>();
            if (!timer)
            {
                timer = body.gameObject.AddComponent<DroneUptimeTimer>();
            }

            DroneDef droneDef = DroneCatalog.GetDroneDef(droneIndex);
            EnsurePerDroneEntry(droneDef);
            timer.Configure(droneDef);
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    private IEnumerable<HurtBox> BullseyeSearch_GetResults(On.RoR2.BullseyeSearch.orig_GetResults orig, BullseyeSearch self)
    {
        var results = orig(self);

        if (!AdjustedDrones.DisableDroneAggro.Value)
        {
            return results;
        }

        if (self == null || self.viewer == null || self.viewer.teamComponent == null)
        {
            return results;
        }

        // Enemy AI should not consider player drones/turrets as valid targets.
        if (self.viewer.teamComponent.teamIndex == TeamIndex.Player)
        {
            return results;
        }

        if (results is not HurtBox[] array || array.Length == 0)
        {
            return results;
        }

        var filtered = array.Where(hb => !ShouldIgnoreAsTarget(hb)).ToArray();
        return filtered;
    }

    private static bool ShouldIgnoreAsTarget(HurtBox hb)
    {
        if (!hb || hb.teamIndex != TeamIndex.Player)
        {
            return false;
        }

        var hc = hb.healthComponent;
        if (!hc || !hc.body)
        {
            return false;
        }

        var body = hc.body;
        if (DroneCatalog.GetDroneIndexFromBodyIndex(body.bodyIndex) == DroneIndex.None)
        {
            return false;
        }

        var master = body.master;
        if (!master || master.playerCharacterMasterController)
        {
            return false;
        }

        if (!master.minionOwnership || !master.minionOwnership.ownerMaster)
        {
            return false;
        }

        return master.minionOwnership.ownerMaster.playerCharacterMasterController;
    }

    private void AllyCardController_Awake(On.RoR2.UI.AllyCardController.orig_Awake orig, AllyCardController self)
    {
        orig(self);
        if (!self)
        {
            return;
        }

        if (!self.GetComponent<DroneUptimeAllyCardOverlay>())
        {
            self.gameObject.AddComponent<DroneUptimeAllyCardOverlay>();
        }
    }
}

internal enum DroneUptimeTierClass
{
    Utility,
    Combat,
}

internal enum DroneUptimeRarityTier
{
    White,
    Green,
    Red,
}

internal static class DroneUptimeDuration
{
    public static float ComputeBaseDurationSeconds(DroneDef droneDef)
    {
        DroneUptimeRarityTier tier = DroneUptimeRarityTier.Green;
        if (droneDef)
        {
            tier = droneDef.tier switch
            {
                ItemTier.Tier1 => DroneUptimeRarityTier.White,
                ItemTier.Tier2 => DroneUptimeRarityTier.Green,
                ItemTier.Tier3 => DroneUptimeRarityTier.Red,
                _ => DroneUptimeRarityTier.Green,
            };
        }

        DroneUptimeTierClass tierClass = DroneUptimeTierClass.Utility;
        if (droneDef && droneDef.droneType == DroneType.Combat)
        {
            tierClass = DroneUptimeTierClass.Combat;
        }

        // Per-drone overrides (always available). -1 means "use tier/class".
        if (droneDef)
        {
            var plugin = AdjustedDrones.Instance;
            var perDroneEntry = plugin ? plugin.EnsurePerDroneEntry(droneDef) : null;
            if (perDroneEntry != null && perDroneEntry.Value >= 0f)
            {
                return Mathf.Max(0f, perDroneEntry.Value);
            }
        }

        float baseSeconds = 180f;
        if (AdjustedDrones.DurationSeconds.TryGetValue((tier, tierClass), out var entry))
        {
            baseSeconds = Mathf.Max(0f, entry.Value);
        }
        return baseSeconds;
    }

    public static float GetScaledDurationSeconds(float baseSeconds)
    {
        float coeff = 1f;
        if (Run.instance)
        {
            coeff = Mathf.Max(1f, Run.instance.difficultyCoefficient);
        }

        float multiplier = Mathf.Max(0f, AdjustedDrones.DifficultyCoefficientMultiplier.Value);
        coeff = Mathf.Max(1f, coeff * multiplier);

        float exponent = Mathf.Max(0f, AdjustedDrones.DifficultyExponent.Value);
        float scale = (exponent <= 0f) ? 1f : Mathf.Pow(coeff, exponent);
        return (scale <= 0f) ? baseSeconds : (baseSeconds / scale);
    }
}

internal sealed class DroneUptimeTimer : MonoBehaviour
{
    public float duration => DroneUptimeDuration.GetScaledDurationSeconds(baseDuration);
    public float age { get; private set; }
    public DroneDef droneDef { get; private set; }

    private float baseDuration;

    private HealthComponent health;
    private bool configured;
    private bool broke;

    public float remaining => Mathf.Max(0f, duration - age);
    public float remainingFraction => duration > 0f ? Mathf.Clamp01(remaining / duration) : 0f;

    public void Configure(DroneDef def)
    {
        droneDef = def;
        baseDuration = DroneUptimeDuration.ComputeBaseDurationSeconds(def);
        age = 0f;
        configured = true;
        broke = false;
    }

    private void Start()
    {
        TryGetComponent(out health);
    }

    private void Update()
    {
        if (!configured)
        {
            return;
        }

        float currentDuration = duration;
        if (currentDuration <= 0f)
        {
            return;
        }

        age += Time.deltaTime;
        if (!broke && age >= currentDuration)
        {
            broke = true;
            if (UnityEngine.Networking.NetworkServer.active)
            {
                BreakToRepair();
            }
        }
    }

    [UnityEngine.Networking.Server]
    private void BreakToRepair()
    {
        if (!health || !health.body)
        {
            return;
        }
        CharacterBody body = health.body;
        DroneDef def = droneDef;

        // Only mega drones spawn broken repairables; normal drones just die
        bool isMegaDrone = IsMegaDrone(body, def);
        if (isMegaDrone)
        {
            // Let the MegaDroneDeathState hook spawn the broken repairable (if enabled).
            if (body.master)
            {
                body.master.TrueKill();
            }
        }
        else
        {
            // Use normal death sequence for regular drones
            if (body.master)
            {
                body.master.TrueKill();
            }
        }
    }

    private static bool IsMegaDrone(CharacterBody body, DroneDef droneDef)
    {
        if (!body)
        {
            return false;
        }

        // Check body name for "MegaDrone"
        string bodyName = BodyCatalog.GetBodyName(body.bodyIndex);
        if (!string.IsNullOrEmpty(bodyName) && bodyName.Contains("MegaDrone"))
        {
            return true;
        }

        // Fallback: check GameObject name
        if (body.name.Contains("MegaDrone"))
        {
            return true;
        }

        return false;
    }
}

internal sealed class DroneUptimeAllyCardOverlay : MonoBehaviour
{
    private AllyCardController allyCard;
    private RectTransform portraitRect;
    private GameObject timerRoot;
    private GameObject tempItemIndicatorInstance;
    private Image timerImage;
    private TMPro.TextMeshProUGUI fallbackText;

    private static bool triedLoadTempItemIndicator;
    private static GameObject cachedTempItemIndicatorPrefab;

    private void Awake()
    {
        allyCard = GetComponent<AllyCardController>();
        TryBuildUi();
    }

    private void TryBuildUi()
    {
        if (!allyCard || !allyCard.portraitIconImage)
        {
            return;
        }

        portraitRect = allyCard.portraitIconImage.GetComponent<RectTransform>();
        if (!portraitRect)
        {
            return;
        }

        if (!triedLoadTempItemIndicator)
        {
            triedLoadTempItemIndicator = true;
            try
            {
                // Load the exact temp item indicator used by PickupDisplay
                var hiddenPickupModel = PickupCatalog.GetHiddenPickupDisplayPrefab();
                var pickupDisplay = hiddenPickupModel ? hiddenPickupModel.GetComponentInChildren<PickupDisplay>(true) : null;
                cachedTempItemIndicatorPrefab = pickupDisplay ? pickupDisplay.temporaryItemIndicator : null;
            }
            catch
            {
                cachedTempItemIndicatorPrefab = null;
            }
        }

        if (!timerRoot)
        {
            timerRoot = new GameObject("DroneUptimeTimer", typeof(RectTransform));
            timerRoot.transform.SetParent(portraitRect, worldPositionStays: false);
            timerRoot.transform.SetAsLastSibling();

            var rt = (RectTransform)timerRoot.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(0f, 0f);
            rt.sizeDelta = new Vector2(18f, 18f);
        }

        if (!tempItemIndicatorInstance && cachedTempItemIndicatorPrefab)
        {
            // Instantiate the exact temp item indicator - don't modify it
            tempItemIndicatorInstance = UnityEngine.Object.Instantiate(cachedTempItemIndicatorPrefab, timerRoot.transform, worldPositionStays: false);
            tempItemIndicatorInstance.name = "TempItemIndicator";
            tempItemIndicatorInstance.transform.localPosition = Vector3.zero;
            tempItemIndicatorInstance.transform.localRotation = Quaternion.identity;
            tempItemIndicatorInstance.transform.localScale = Vector3.one;

            // Find the filled Image to drive fillAmount - leave everything else untouched
            var images = tempItemIndicatorInstance.GetComponentsInChildren<Image>(true);
            foreach (var img in images)
            {
                if (img && img.type == Image.Type.Filled)
                {
                    timerImage = img;
                    break;
                }
            }
        }

        // Fallback: if temp item indicator failed, use text timer
        if (!timerImage && !fallbackText)
        {
            var textObj = new GameObject("FallbackTimerText", typeof(RectTransform));
            textObj.transform.SetParent(timerRoot.transform, worldPositionStays: false);

            var rt = (RectTransform)textObj.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;

            fallbackText = textObj.AddComponent<TMPro.TextMeshProUGUI>();
            fallbackText.fontSize = 12f;
            fallbackText.color = new Color(0.3f, 0.6f, 1f, 1f); // Blue color
            fallbackText.alignment = TMPro.TextAlignmentOptions.Center;
            fallbackText.enableWordWrapping = false;
            fallbackText.raycastTarget = false;
        }
    }

    private void LateUpdate()
    {
        if (!timerImage && !fallbackText)
        {
            TryBuildUi();
        }

        if (!allyCard)
        {
            return;
        }

        var master = allyCard.sourceMaster;
        var body = master ? master.GetBody() : null;
        if (!body)
        {
            if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
            if (fallbackText) fallbackText.enabled = false;
            return;
        }

        var uptime = body.GetComponent<DroneUptimeTimer>();
        if (!uptime)
        {
            if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
            if (fallbackText) fallbackText.enabled = false;
            return;
        }

        // Infinite duration (duration == 0): keep the drone alive forever and don't show any timer UI.
        if (uptime.duration <= 0f)
        {
            if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
            if (fallbackText) fallbackText.enabled = false;
            return;
        }

        // Only show for drones/turrets (not all allies).
        if (DroneCatalog.GetDroneIndexFromBodyIndex(body.bodyIndex) == DroneIndex.None)
        {
            if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
            if (fallbackText) fallbackText.enabled = false;
            return;
        }

        // Update temp item indicator if available
        if (timerImage)
        {
            if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(true);
            timerImage.fillAmount = uptime.remainingFraction;
            if (fallbackText) fallbackText.enabled = false;
        }
        // Otherwise use fallback text timer
        else if (fallbackText)
        {
            fallbackText.enabled = true;
            float remaining = uptime.remaining;
            int minutes = Mathf.FloorToInt(remaining / 60f);
            int seconds = Mathf.FloorToInt(remaining % 60f);
            fallbackText.text = $"{minutes}:{seconds:00}";
        }
    }
}

internal static class DroneUptimeRepairSpawner
{
    [UnityEngine.Networking.Server]
    public static void SpawnBrokenAndRemove(CharacterBody droneBody, DroneDef droneDef)
    {
        if (!UnityEngine.Networking.NetworkServer.active)
        {
            return;
        }
        if (!droneBody)
        {
            return;
        }

        SpawnCard spawnCard = GetBrokenDroneSpawnCard(droneBody, droneDef);
        if (spawnCard != null && DirectorCore.instance)
        {
            Vector3 spawnPos = FindGround(droneBody.corePosition, droneBody.radius) + Vector3.up * 0.25f;
            var placementRule = new DirectorPlacementRule
            {
                placementMode = DirectorPlacementRule.PlacementMode.Direct,
                position = spawnPos
            };

            var rng = Run.instance != null
                ? new Xoroshiro128Plus(Run.instance.seed + (ulong)Run.instance.fixedTime)
                : new Xoroshiro128Plus(0uL);

            GameObject spawned = DirectorCore.instance.TrySpawnObject(new DirectorSpawnRequest(spawnCard, placementRule, rng));
            if (spawned)
            {
                var purchase = spawned.GetComponent<PurchaseInteraction>();
                if (purchase && purchase.costType == CostTypeIndex.Money && Run.instance)
                {
                    purchase.Networkcost = Mathf.RoundToInt((float)Run.instance.GetDifficultyScaledCost(purchase.cost));
                }
            }
        }

        // Remove the current drone/turret so the player must repair via the spawned broken interactable.
        var master = droneBody.master;
        if (master)
        {
            // Use TrueKill to ensure the master and all associated objects are properly destroyed
            master.TrueKill();
        }
        else
        {
            // Fallback: destroy the body directly if no master exists
            UnityEngine.Networking.NetworkServer.Destroy(droneBody.gameObject);
        }
    }

    private static SpawnCard GetBrokenDroneSpawnCard(CharacterBody droneBody, DroneDef droneDef)
    {
        if (droneDef && droneDef.droneBrokenSpawnCard)
        {
            return droneDef.droneBrokenSpawnCard;
        }

        // Mirror vanilla fallback logic (see EntityStates.Drone.DeathState): iscBroken{BodyNameWithoutBody}
        string bodyName = BodyCatalog.GetBodyName(droneBody.bodyIndex);
        if (string.IsNullOrWhiteSpace(bodyName))
        {
            bodyName = droneBody.name;
        }
        bodyName = bodyName.Replace("Body", "");
        string spawnCardName = "iscBroken" + bodyName;
        return LegacyResourcesAPI.Load<SpawnCard>("SpawnCards/InteractableSpawnCard/" + spawnCardName);
    }

    private static Vector3 FindGround(Vector3 origin, float radius)
    {
        // Mega drones can die/"break" while airborne; use a longer ground probe so the broken interactable spawns on terrain.
        const float upDistance = 200f;
        const float maxDistance = 1200f;
        Vector3 start = origin + Vector3.up * upDistance;

        if (Physics.Raycast(start, Vector3.down, out var hit, maxDistance, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
        {
            return hit.point;
        }

        float sphereRadius = Mathf.Max(0.25f, radius);
        if (Physics.SphereCast(start, sphereRadius, Vector3.down, out hit, maxDistance, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
        {
            return hit.point;
        }

        return origin;
    }
}
