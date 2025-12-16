using System;
using System.Collections.Generic;
using System.ComponentModel;

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
using RiskOfOptions.Options;

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
    internal static ConfigEntry<bool> DisableWhenOperator;
    internal static ConfigEntry<string> TimerExemptDrones;
    internal static ConfigEntry<bool> DisableDroneAggro;
    internal static ConfigEntry<Color> TimerColor;
    internal static ConfigEntry<float> DifficultyExponent;
    internal static ConfigEntry<float> DifficultyCoefficientMultiplier;
    internal static ConfigEntry<float> RepairCostMultiplier;
    internal static ConfigEntry<float> DroneBaseCostMultiplier;
    internal static readonly Dictionary<(DroneUptimeRarityTier tier, DroneUptimeTierClass tierClass), ConfigEntry<float>> DurationSeconds = new();
    internal static readonly Dictionary<DroneIndex, ConfigEntry<float>> PerDroneDurationSeconds = new();

    private static readonly HashSet<ConfigEntryBase> riskOfOptionsRegisteredEntries = new();

    private static readonly Dictionary<PurchaseInteraction, int> originalDroneCosts = new();

    private static string cachedTimerExemptValue;
    private static HashSet<string> cachedTimerExemptTokens;

    private void Awake()
    {
        Instance = this;
        Log.Init(Logger);

        TryRegisterColorTomlConverter();
        BindConfig();
        RegisterRiskOfOptionsOptions();

        if (!ModEnabled.Value)
        {
            Log.Info("AdjustedDrones is disabled via config (General.Enabled=false). Restart required to apply.");
            return;
        }

        // Capture vanilla drone costs early (before we apply any run-gated modifications).
        CaptureOriginalDroneCostsIfNeeded();
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
    }

    private void Run_onRunStartGlobal(Run run)
    {
        // Ensure all DroneDefs are present and configs exist for every drone.
        InitializePerDroneConfigs();

        // Always restore vanilla costs at the start of every run, then optionally apply our multipliers
        // if the Operator-only condition is satisfied.
        StartCoroutine(ApplyRunGatedSystems());
    }

    private void OnDestroy()
    {
        CharacterBody.onBodyStartGlobal -= OnBodyStartGlobal;
        On.RoR2.BullseyeSearch.GetResults -= BullseyeSearch_GetResults;
        On.RoR2.UI.AllyCardController.Awake -= AllyCardController_Awake;
        Run.onRunStartGlobal -= Run_onRunStartGlobal;
    }
    private void BindConfig()
    {
        ModEnabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Enable/disable the AdjustedDrones mod. Requires restart."
        );

        DisableWhenOperator = Config.Bind(
            "General",
            "DisableWhenOperator",
            false,
            "If true, this mod is disabled when at least one player is Operator/DroneTech (body name contains 'Operator' or 'DroneTech')."
        );

        TimerExemptDrones = Config.Bind(
            "General",
            "TimerExemptDrones",
            string.Join("\n", new[]
            {
                "Drone_11_DroneBomberBodySeconds",
                "Drone_14_DTGunnerDroneBodySeconds",
                "Drone_15_DTHaulerDroneBodySeconds",
                "Drone_16_DTHealingDroneBodySeconds",
                "Drone_9_DroneCommanderBodySecons",
            }),
            "List of drone body names (or per-drone config keys like 'Drone_11_DroneBomberBodySeconds') that should NEVER auto-break from the timer. One entry per line."
        );

        TimerColor = Config.Bind(
            "General",
            "TimerColor",
            new Color(0.3f, 0.6f, 1f, 1f),
            "Drone timer UI color."
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
        BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Utility, 300f);
        BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Combat, 270f);
        BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Utility, 270f);
        BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Combat, 240f);
        BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Utility, 240f);
        BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Combat, 180f);
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
        // Backwards-compatible wrapper: apply multipliers (active=true) for the current run.
        SetGlobalDroneCosts(active: true);
    }

    internal static bool IsModActiveNow()
    {
        if (ModEnabled == null || !ModEnabled.Value)
        {
            return false;
        }

        if (DisableWhenOperator != null && DisableWhenOperator.Value && IsOperatorPlayerPresent())
        {
            return false;
        }

        return true;
    }

    private static bool IsOperatorPlayerPresent()
    {
        try
        {
            var instances = PlayerCharacterMasterController.instances;
            if (instances == null)
            {
                return false;
            }

            foreach (var pcmc in instances)
            {
                if (!pcmc)
                {
                    continue;
                }

                var master = pcmc.master;
                var body = master ? master.GetBody() : null;
                if (!body)
                {
                    continue;
                }

                string bodyName = BodyCatalog.GetBodyName(body.bodyIndex);
                if (string.IsNullOrWhiteSpace(bodyName))
                {
                    bodyName = body.name;
                }

                if (!string.IsNullOrWhiteSpace(bodyName)
                    && (bodyName.IndexOf("Operator", StringComparison.OrdinalIgnoreCase) >= 0
                        || bodyName.IndexOf("DroneTech", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private System.Collections.IEnumerator ApplyRunGatedSystems()
    {
        // Always restore vanilla costs at run start.
        CaptureOriginalDroneCostsIfNeeded();
        SetGlobalDroneCosts(active: false);

        // If Operator-gated disabling is enabled, wait briefly for player bodies to exist so we can
        // reliably detect Operator and keep the mod disabled for that run.
        if (DisableWhenOperator != null && DisableWhenOperator.Value)
        {
            const float timeoutSeconds = 5f;
            float startTime = Time.unscaledTime;
            while ((Time.unscaledTime - startTime) < timeoutSeconds && !IsOperatorPlayerPresent())
            {
                yield return null;
            }
        }

        if (IsModActiveNow())
        {
            SetGlobalDroneCosts(active: true);
        }
    }

    private static void CaptureOriginalDroneCostsIfNeeded()
    {
        try
        {
            var cards = Resources.LoadAll<InteractableSpawnCard>("SpawnCards/InteractableSpawnCard");
            if (cards == null || cards.Length == 0)
            {
                return;
            }

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

                if (!originalDroneCosts.ContainsKey(purchase))
                {
                    originalDroneCosts[purchase] = purchase.cost;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void SetGlobalDroneCosts(bool active)
    {
        // Adjust the prefab costs up-front so the displayed price is correct everywhere.
        // We keep a snapshot of original costs so we can restore when the mod is inactive (e.g. non-Operator runs).
        try
        {
            var cards = Resources.LoadAll<InteractableSpawnCard>("SpawnCards/InteractableSpawnCard");
            if (cards == null || cards.Length == 0)
            {
                return;
            }

            float baseMult = active ? Mathf.Max(0f, DroneBaseCostMultiplier.Value) : 1f;
            float repairMult = active ? Mathf.Max(0f, RepairCostMultiplier.Value) : 1f;

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

                if (!originalDroneCosts.TryGetValue(purchase, out int originalCost))
                {
                    originalCost = purchase.cost;
                    originalDroneCosts[purchase] = originalCost;
                }

                bool isBroken = !string.IsNullOrEmpty(card.name) && card.name.StartsWith("iscBroken", StringComparison.OrdinalIgnoreCase);
                float mult = isBroken ? repairMult : baseMult;
                purchase.cost = Mathf.Max(0, Mathf.RoundToInt(originalCost * mult));
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
            new ConfigDescription(
                "Base active duration in seconds before the drone/turret breaks and requires repair. Set to 0 for infinite duration (no timer UI)."
            )
        );
        DurationSeconds[(tier, tierClass)] = entry;

        RegisterRiskOfOptionsUnlimitedFloat(entry);
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
            new ConfigDescription(
                $"Per-drone duration override for '{displayName}' in seconds. Set to -1 to use the tier/class duration. Set to 0 for infinite duration (no timer UI)."
            )
        );
        PerDroneDurationSeconds[idx] = entry;

        RegisterRiskOfOptionsUnlimitedFloat(entry);
        return entry;
    }

    private void RegisterRiskOfOptionsOptions()
    {
        try
        {
            // General
            RegisterRiskOfOptionsCheckbox(ModEnabled);
            RegisterRiskOfOptionsCheckbox(DisableWhenOperator);
            RegisterRiskOfOptionsStringInput(TimerExemptDrones);
            RegisterRiskOfOptionsColorPicker(TimerColor);

            // Aggro
            RegisterRiskOfOptionsCheckbox(DisableDroneAggro);

            // Scaling
            RegisterRiskOfOptionsUnlimitedFloat(DifficultyExponent);
            RegisterRiskOfOptionsUnlimitedFloat(DifficultyCoefficientMultiplier);

            // Repair / cost
            RegisterRiskOfOptionsUnlimitedFloat(RepairCostMultiplier);
            RegisterRiskOfOptionsUnlimitedFloat(DroneBaseCostMultiplier);

            // Duration tier/class sliders are registered in BindDuration().
            // Per-drone override sliders are registered in EnsurePerDroneEntry().

            ModSettingsManager.SetModDescription("Adjust drone uptime/duration scaling and drone costs. Includes per-drone overrides.");
        }
        catch (Exception e)
        {
            // If RiskOfOptions isn't present/misconfigured, don't hard-fail the mod.
            Log.Error(e);
        }
    }

    private static void RegisterRiskOfOptionsUnlimitedFloat(ConfigEntry<float> entry)
    {
        if (entry == null || riskOfOptionsRegisteredEntries.Contains(entry))
        {
            return;
        }

        // Avoid max-limited sliders for values that should be unbounded.
        // Prefer the numeric field option (name varies by RiskOfOptions version) via reflection.
        if (!TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.FloatFieldOption", entry)
            && !TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.FloatInputFieldOption", entry)
            && !TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.InputFieldOption", entry))
        {
            Log.Warning($"RiskOfOptions float field option not found; skipping menu option for '{entry.Definition.Section}.{entry.Definition.Key}' to avoid imposing an upper limit.");
            return;
        }

        riskOfOptionsRegisteredEntries.Add(entry);
    }

    private static bool TryAddRiskOfOptionsOptionByTypeName(string fullTypeName, object ctorArg)
    {
        try
        {
            var asm = typeof(ModSettingsManager).Assembly;
            var optionType = asm.GetType(fullTypeName, throwOnError: false);
            if (optionType == null)
            {
                return false;
            }

            object optionInstance = Activator.CreateInstance(optionType, ctorArg);
            if (optionInstance == null)
            {
                return false;
            }

            // Call ModSettingsManager.AddOption(optionInstance) via reflection to avoid hard dependency on the option base type.
            var addOption = typeof(ModSettingsManager)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddOption" && m.GetParameters().Length == 1);

            if (addOption == null)
            {
                return false;
            }

            addOption.Invoke(null, new[] { optionInstance });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterRiskOfOptionsCheckbox(ConfigEntry<bool> entry)
    {
        if (entry == null || riskOfOptionsRegisteredEntries.Contains(entry))
        {
            return;
        }

        ModSettingsManager.AddOption(new CheckBoxOption(entry));
        riskOfOptionsRegisteredEntries.Add(entry);
    }

    private static void RegisterRiskOfOptionsStringInput(ConfigEntry<string> entry)
    {
        if (entry == null || riskOfOptionsRegisteredEntries.Contains(entry))
        {
            return;
        }

        // RiskOfOptions string input option names vary by version; prefer string-specific, then generic.
        if (!TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.StringInputFieldOption", entry)
            && !TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.InputFieldOption", entry))
        {
            return;
        }

        riskOfOptionsRegisteredEntries.Add(entry);
    }

    internal static bool IsDroneExemptFromTimer(DroneDef droneDef)
    {
        if (!droneDef)
        {
            return false;
        }

        string raw = TimerExemptDrones?.Value ?? string.Empty;
        if (!string.Equals(raw, cachedTimerExemptValue, StringComparison.Ordinal))
        {
            cachedTimerExemptValue = raw;
            cachedTimerExemptTokens = ParseTimerExemptTokens(raw);
        }

        if (cachedTimerExemptTokens == null || cachedTimerExemptTokens.Count == 0)
        {
            return false;
        }

        string bodyName = droneDef.bodyPrefab ? droneDef.bodyPrefab.name : string.Empty;
        string safeKeyPrefix = GetSafeDroneConfigKeyName(droneDef);

        return cachedTimerExemptTokens.Contains(NormalizeToken(bodyName))
            || cachedTimerExemptTokens.Contains(NormalizeToken(safeKeyPrefix))
            || cachedTimerExemptTokens.Contains(NormalizeToken(safeKeyPrefix + "Seconds"));
    }

    private static HashSet<string> ParseTimerExemptTokens(string raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            string t = NormalizeToken(line);
            if (t.Length == 0)
            {
                continue;
            }

            // Accept common suffix typos.
            t = StripSuffix(t, "seconds");
            t = StripSuffix(t, "secons");

            set.Add(t);

            // If the token looks like our per-drone key, also add just the body name part.
            // Example: Drone_11_DroneBomberBodySeconds -> dronebomberbody
            if (t.StartsWith("drone_", StringComparison.Ordinal))
            {
                int idxSep = t.IndexOf('_', "drone_".Length);
                if (idxSep >= 0 && idxSep + 1 < t.Length)
                {
                    string remainder = t.Substring(idxSep + 1);
                    if (remainder.Length > 0)
                    {
                        set.Add(remainder);
                    }
                }
            }
        }

        return set;
    }

    private static string NormalizeToken(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        // Keep only a simple, case-insensitive token. Underscores are preserved.
        return s.Trim().ToLowerInvariant();
    }

    private static string StripSuffix(string s, string suffix)
    {
        if (s != null && suffix != null && s.EndsWith(suffix, StringComparison.Ordinal))
        {
            return s.Substring(0, s.Length - suffix.Length);
        }
        return s;
    }

    private static void RegisterRiskOfOptionsColorPicker(ConfigEntry<Color> entry)
    {
        if (entry == null || riskOfOptionsRegisteredEntries.Contains(entry))
        {
            return;
        }

        // RiskOfOptions has a color picker option, but its exact type name can vary across versions.
        // Try common names first, then fall back to scanning for an option type that can take this ConfigEntry.
        if (!TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.ColorOption", entry)
            && !TryAddRiskOfOptionsOptionByTypeName("RiskOfOptions.Options.ColorPickerOption", entry)
            && !TryAddRiskOfOptionsOptionByHeuristic(entry))
        {
            return;
        }

        riskOfOptionsRegisteredEntries.Add(entry);
    }

    private static bool TryAddRiskOfOptionsOptionByHeuristic(object entry)
    {
        try
        {
            var asm = typeof(ModSettingsManager).Assembly;
            var entryType = entry.GetType();
            var options = asm.GetTypes()
                .Where(t => t != null
                    && string.Equals(t.Namespace, "RiskOfOptions.Options", StringComparison.Ordinal)
                    && t.Name.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            foreach (var optionType in options)
            {
                var ctor = optionType
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(entryType);
                    });

                if (ctor == null)
                {
                    continue;
                }

                object optionInstance = ctor.Invoke(new[] { entry });
                if (optionInstance == null)
                {
                    continue;
                }

                var addOption = typeof(ModSettingsManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "AddOption" && m.GetParameters().Length == 1);

                if (addOption == null)
                {
                    return false;
                }

                addOption.Invoke(null, new[] { optionInstance });
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static void TryRegisterColorTomlConverter()
    {
        try
        {
            // Avoid hard dependency on a specific TomlTypeConverter API shape.
            var converterType = typeof(BepInEx.Configuration.TomlTypeConverter);
            var addConverter = converterType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddConverter" && m.GetParameters().Length == 2);

            if (addConverter == null)
            {
                return;
            }

            addConverter.Invoke(null, new object[] { typeof(Color), new UnityColorTypeConverter() });
        }
        catch
        {
            // ignore
        }
    }

    private sealed class UnityColorTypeConverter : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
        {
            if (value is string s)
            {
                s = s.Trim();
                if (s.Length == 0)
                {
                    return Color.white;
                }

                // Accept HTML colors (#RRGGBB / #RRGGBBAA) and comma-separated floats (r,g,b[,a]).
                string html = s.StartsWith("#", StringComparison.Ordinal) ? s : ("#" + s);
                if (ColorUtility.TryParseHtmlString(html, out var parsed))
                {
                    return parsed;
                }

                var parts = s.Split(',');
                if (parts.Length is 3 or 4
                    && float.TryParse(parts[0], out float r)
                    && float.TryParse(parts[1], out float g)
                    && float.TryParse(parts[2], out float b))
                {
                    float a = 1f;
                    if (parts.Length == 4)
                    {
                        float.TryParse(parts[3], out a);
                    }
                    return new Color(r, g, b, a);
                }
            }

            return base.ConvertFrom(context, culture, value);
        }

        public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is Color c)
            {
                return "#" + ColorUtility.ToHtmlStringRGBA(c);
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
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

        if (!IsModActiveNow() || !AdjustedDrones.DisableDroneAggro.Value)
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

        if (!IsModActiveNow())
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
        // Exemptions override everything: exempt drones never auto-break from the timer.
        if (AdjustedDrones.IsDroneExemptFromTimer(droneDef))
        {
            return 0f;
        }

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

        // Safety: if the mod is inactive (e.g. Operator present), ensure any accidental timer configuration
        // results in no breaking behavior.
        if (!AdjustedDrones.IsModActiveNow())
        {
            return 0f;
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
        if (!AdjustedDrones.IsModActiveNow())
        {
            return;
        }

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
            // Spawn the broken repairable directly. Relying on death-state behavior is brittle
            // (e.g. TrueKill can bypass the vanilla broken spawn path for some drones).
            DroneUptimeRepairSpawner.SpawnBrokenAndRemove(body, def);
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

            ApplyTimerColorIfConfigured();
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
            fallbackText.color = new Color(0.3f, 0.6f, 1f, 1f);
            fallbackText.alignment = TMPro.TextAlignmentOptions.Center;
            fallbackText.enableWordWrapping = false;
            fallbackText.raycastTarget = false;

            ApplyTimerColorIfConfigured();
        }
    }

    private void ApplyTimerColorIfConfigured()
    {
        var entry = AdjustedDrones.TimerColor;
        if (entry == null)
        {
            return;
        }

        Color c = entry.Value;

        if (timerImage)
        {
            timerImage.color = c;
        }

        if (fallbackText)
        {
            fallbackText.color = c;
        }
    }

    private void LateUpdate()
    {
        if (!timerImage && !fallbackText)
        {
            TryBuildUi();
        }

        // Keep color in sync in case config changes at runtime.
        ApplyTimerColorIfConfigured();

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
