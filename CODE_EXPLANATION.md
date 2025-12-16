# AdjustedDrones — Complete Code Walkthrough

This document provides a step-by-step explanation of the entire `AdjustedDrones.cs` implementation.

---

## Table of Contents

1. [Overview](#overview)
2. [Imports and Namespace](#imports-and-namespace)
3. [The Main Plugin Class](#the-main-plugin-class)
4. [Configuration System](#configuration-system)
5. [Drone Detection and Timer Attachment](#drone-detection-and-timer-attachment)
6. [Aggro Suppression System](#aggro-suppression-system)
7. [Duration Calculation Logic](#duration-calculation-logic)
8. [Runtime Timer Component](#runtime-timer-component)
9. [UI Timer Display](#ui-timer-display)
10. [Repair Spawner System](#repair-spawner-system)
11. [Configuration Reference](#configuration-reference)
12. [Runtime Flow Summary](#runtime-flow-summary)

---

## Overview

### What This Mod Does

**AdjustedDrones** is a Risk of Rain 2 mod that implements three core behaviors:

1. **Reduced Enemy Aggro on Drones**  
   Player-owned drones and turrets are filtered out of enemy targeting searches, making them less likely to be attacked.

2. **Limited Active Duration with Repair Requirement**  
   Each drone/turret has a timer that counts down. When it expires, the drone "breaks" and spawns a repairable broken interactable instead of dying permanently.

3. **Visual Duration Indicator**  
   A blue radial ring (the same visual used for temporary items) appears on the drone's ally portrait icon, showing remaining uptime.

### Core Mechanics

- **Duration depends on**:

  - Drone tier (White/Green/Red)
  - Drone class (Utility vs Combat)
  - Optional per-drone overrides
  - Dynamic scaling with `Run.instance.difficultyCoefficient`

- **Scaling formula**:
  ```
  duration = baseDuration / ((difficultyCoefficient * multiplier) ^ exponent)
  ```
  As difficulty increases → duration decreases (drones break faster).

---

## Imports and Namespace

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HG;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustedDrones;
```

### Purpose of Each Import

| Import          | Purpose                                                    |
| --------------- | ---------------------------------------------------------- |
| `System.*`      | Standard C# collections and LINQ queries                   |
| `BepInEx.*`     | Plugin framework: `BaseUnityPlugin`, `ConfigEntry<T>`      |
| `HG`            | RoR2's custom utilities (contains `Xoroshiro128Plus` RNG)  |
| `RoR2`          | Core game types: drones, runs, teams, spawning             |
| `RoR2.UI`       | UI components: `AllyCardController`, `ItemIcon`            |
| `UnityEngine.*` | Unity essentials: `MonoBehaviour`, `Time`, `Transform`, UI |

---

## The Main Plugin Class

### Plugin Declaration

```csharp
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class AdjustedDrones : BaseUnityPlugin
```

The `[BepInPlugin]` attribute tells BepInEx to load this class as a plugin.

### Identity Constants

```csharp
public const string PluginAuthor = "AdoptedFatty";
public const string PluginName = "AdjustedDrones";
public const string PluginGUID = PluginAuthor + "." + PluginName;
public const string PluginVersion = "1.0.0";
```

These define the mod's identity for BepInEx and for inter-mod compatibility checking.

### Singleton Instance

```csharp
internal static AdjustedDrones Instance { get; private set; }
```

Used so static helper classes can access the plugin instance (especially for per-drone config entry creation).

---

## Configuration System

### Config Field Storage

```csharp
internal static ConfigEntry<float> DifficultyExponent;
internal static ConfigEntry<float> DifficultyCoefficientMultiplier;
internal static ConfigEntry<bool> EnablePerDroneOverrides;
internal static readonly Dictionary<(DroneUptimeRarityTier tier, DroneUptimeTierClass tierClass), ConfigEntry<float>> DurationSeconds = new();
internal static readonly Dictionary<DroneIndex, ConfigEntry<float>> PerDroneDurationSeconds = new();
```

#### What Each Field Does

- **`DifficultyExponent`**  
  Exponent in the scaling formula. Setting to `0` disables scaling entirely.

- **`DifficultyCoefficientMultiplier`**  
  Multiplies the difficulty coefficient before applying the exponent. Allows fine-tuning scaling intensity.

- **`EnablePerDroneOverrides`**  
  Master toggle: if `true`, per-drone duration overrides (if set) will be used instead of tier/class durations.

- **`DurationSeconds` Dictionary**  
  Maps `(tier, class)` pairs to config entries. Contains 6 entries:

  - White × Utility, White × Combat
  - Green × Utility, Green × Combat
  - Red × Utility, Red × Combat

- **`PerDroneDurationSeconds` Dictionary**  
  Maps `DroneIndex` to config entries, created lazily when a drone is first encountered.

### Plugin Lifecycle: Awake

```csharp
private void Awake()
{
    Instance = this;
    Log.Init(Logger);
    BindConfig();

    CharacterBody.onBodyStartGlobal += OnBodyStartGlobal;
    On.RoR2.BullseyeSearch.GetResults += BullseyeSearch_GetResults;
    On.RoR2.UI.AllyCardController.Awake += AllyCardController_Awake;
}
```

#### Step-by-Step

1. **Store singleton reference**  
   `Instance = this;` makes the plugin instance globally accessible.

2. **Initialize logging**  
   `Log.Init(Logger);` sets up the mod's logger helper.

3. **Bind configuration**  
   `BindConfig();` creates all config entries and loads values from the config file.

4. **Register event hooks**:
   - `CharacterBody.onBodyStartGlobal`: Fires when any body spawns; used to detect player-owned drones.
   - `BullseyeSearch.GetResults`: Intercepts enemy targeting queries to filter out drones.
   - `AllyCardController.Awake`: Adds the UI overlay component to ally cards.

### Plugin Lifecycle: OnDestroy

```csharp
private void OnDestroy()
{
    CharacterBody.onBodyStartGlobal -= OnBodyStartGlobal;
    On.RoR2.BullseyeSearch.GetResults -= BullseyeSearch_GetResults;
    On.RoR2.UI.AllyCardController.Awake -= AllyCardController_Awake;
}
```

Unregisters all hooks when the plugin is unloaded to prevent dangling references.

### Config Binding: BindConfig

```csharp
private void BindConfig()
{
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

    EnablePerDroneOverrides = Config.Bind(
        "Overrides",
        "EnablePerDroneDurationOverrides",
        false,
        "If true, use per-drone duration config entries when set (>= 0). If false, only tier/class durations are used."
    );

    // Bind tier/class durations (6 total)
    BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Utility, 240f);
    BindDuration(DroneUptimeRarityTier.White, DroneUptimeTierClass.Combat, 180f);
    BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Utility, 180f);
    BindDuration(DroneUptimeRarityTier.Green, DroneUptimeTierClass.Combat, 135f);
    BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Utility, 120f);
    BindDuration(DroneUptimeRarityTier.Red, DroneUptimeTierClass.Combat, 90f);
}
```

#### Default Duration Pattern

Notice the pattern: **higher tier** and **combat** class → **lower base duration**.

| Tier  | Class   | Default Duration |
| ----- | ------- | ---------------- |
| White | Utility | 240s             |
| White | Combat  | 180s             |
| Green | Utility | 180s             |
| Green | Combat  | 135s             |
| Red   | Utility | 120s             |
| Red   | Combat  | 90s              |

### Helper: BindDuration

```csharp
private void BindDuration(DroneUptimeRarityTier tier, DroneUptimeTierClass tierClass, float defaultSeconds)
{
    var entry = Config.Bind(
        $"Duration.{tier}",
        $"{tierClass}Seconds",
        defaultSeconds,
        "Base active duration in seconds before the drone/turret breaks and requires repair."
    );
    DurationSeconds[(tier, tierClass)] = entry;
}
```

Stores each config entry in the `DurationSeconds` dictionary for fast lookup.

---

## Per-Drone Override System

### EnsurePerDroneEntry

```csharp
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
    var entry = Config.Bind(
        "Overrides.PerDrone",
        safeName + "Seconds",
        -1f,
        "Per-drone duration override in seconds. Set to -1 to use the tier/class duration."
    );
    PerDroneDurationSeconds[idx] = entry;
    return entry;
}
```

#### How It Works

1. Checks if this drone type already has an entry in the dictionary.
2. If not, creates a new config entry with:
   - Section: `"Overrides.PerDrone"`
   - Key: `"Drone_<index>_<BodyName>Seconds"`
   - Default: `-1` (meaning "use tier/class duration")
3. Stores it in `PerDroneDurationSeconds` for future access.

### GetSafeDroneConfigKeyName

```csharp
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
```

Creates a human-readable config key like:

- `Drone_3_Drone1Body`
- `Drone_7_BackupDroneBody`

This makes the config file easier to edit manually.

---

## Drone Detection and Timer Attachment

### OnBodyStartGlobal

```csharp
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
```

#### Execution Flow

This method is called **every time any CharacterBody spawns in the game**. It uses early returns to filter down to only player-owned drones/turrets.

**Validation Chain**:

1. ✅ Body exists and has team component
2. ✅ Body is on `TeamIndex.Player`
3. ✅ Body has a master with minion ownership
4. ✅ Owner master has a player character controller
5. ✅ Body is recognized as a drone in `DroneCatalog`

If all checks pass:

- Add `DroneUptimeTimer` component to the body's GameObject (if not already present).
- Ensure per-drone config exists for this drone type.
- Configure the timer with the drone's definition.

---

## Aggro Suppression System

### BullseyeSearch_GetResults Hook

```csharp
private IEnumerable<HurtBox> BullseyeSearch_GetResults(On.RoR2.BullseyeSearch.orig_GetResults orig, BullseyeSearch self)
{
    var results = orig(self);
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
```

#### How It Works

`BullseyeSearch` is a common utility used by enemy AI to find targets. This hook:

1. Calls the original method to get candidate targets.
2. If the viewer is on the player team → returns unmodified (so players can still aim at allies).
3. Otherwise (enemy viewer) → filters the results to remove player-owned drones/turrets.

### ShouldIgnoreAsTarget

```csharp
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
```

#### Logic

Returns `true` only when **all** of these are true:

- HurtBox is on `TeamIndex.Player`
- HurtBox belongs to a body recognized as a drone in `DroneCatalog`
- That body's master is owned by a player (via minion ownership)

**Important Note**: Not all enemy AI uses `BullseyeSearch`. Some enemies may still target drones through other logic paths, so this is "reduced aggro" rather than "zero aggro".

---

## Duration Calculation Logic

### Tier and Class Enums

```csharp
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
```

These simplified categories are used for config lookup.

### DroneUptimeDuration Static Class

This class has two responsibilities:

1. Compute base duration from drone metadata
2. Apply difficulty scaling

#### ComputeBaseDurationSeconds

```csharp
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

    // Optional per-drone overrides.
    if (AdjustedDrones.EnablePerDroneOverrides.Value && droneDef)
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
```

##### Execution Order

1. **Determine tier**: Maps `ItemTier` from `DroneDef` to `White`/`Green`/`Red`.
2. **Determine class**: If `DroneType.Combat` → `Combat`, else → `Utility`.
3. **Check per-drone override**: If enabled and value ≥ 0, use that.
4. **Lookup tier/class duration**: From `DurationSeconds` dictionary.
5. **Fallback**: If missing, use `180` seconds.

#### GetScaledDurationSeconds

```csharp
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
```

##### Formula Breakdown

$$
\text{duration} = \frac{\text{baseSeconds}}{\left(\max(1, \text{difficultyCoefficient} \cdot \text{multiplier})\right)^{\text{exponent}}}
$$

**Example**:

- `baseSeconds = 180`
- `difficultyCoefficient = 3.0`
- `multiplier = 1.0`
- `exponent = 1.0`

→ `scale = 3.0^1.0 = 3.0`  
→ `duration = 180 / 3.0 = 60 seconds`

**Result**: Higher difficulty → shorter duration (drones break faster as the run gets harder).

---

## Runtime Timer Component

### DroneUptimeTimer MonoBehaviour

```csharp
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
```

#### Key Properties

- **`baseDuration`**  
  Stored at configuration time. Never changes for the lifetime of the drone.

- **`duration` (property)**  
  **Computed dynamically** every time it's accessed:

  ```csharp
  public float duration => DroneUptimeDuration.GetScaledDurationSeconds(baseDuration);
  ```

  This makes the timer responsive to difficulty changes mid-run.

- **`age`**  
  Increases by `Time.deltaTime` every frame.

- **`remaining` / `remainingFraction`**  
  Convenience properties for UI and expiry logic.

### Configure

```csharp
public void Configure(DroneDef def)
{
    droneDef = def;
    baseDuration = DroneUptimeDuration.ComputeBaseDurationSeconds(def);
    age = 0f;
    configured = true;
    broke = false;
}
```

Called from `OnBodyStartGlobal` when the drone spawns:

- Stores the drone definition
- Computes and stores `baseDuration`
- Resets age to 0
- Sets flags

### Update Loop

```csharp
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
```

#### Every Frame

1. Check if configured (skip if not).
2. Get current scaled duration (dynamic).
3. If duration is 0 or negative, do nothing.
4. Increment `age` by delta time.
5. If age exceeds duration and not already broken:
   - Mark as broken
   - If server: spawn broken interactable and remove drone

### BreakToRepair

```csharp
[UnityEngine.Networking.Server]
private void BreakToRepair()
{
    if (!health || !health.body)
    {
        return;
    }
    CharacterBody body = health.body;
    DroneDef def = droneDef;
    DroneUptimeRepairSpawner.SpawnBrokenAndRemove(body, def);
}
```

Server-only method that triggers the transition from active drone to broken repair interactable.

---

## UI Timer Display

### DroneUptimeAllyCardOverlay MonoBehaviour

```csharp
internal sealed class DroneUptimeAllyCardOverlay : MonoBehaviour
{
    private AllyCardController allyCard;
    private RectTransform portraitRect;
    private GameObject timerRoot;
    private GameObject tempItemIndicatorInstance;
    private Image timerImage;
```

This component is attached to every `AllyCardController` (the ally portrait UI).

### Static Cache Fields

```csharp
private static bool triedLoadDurationTemplate;
private static GameObject cachedTempItemIndicatorPrefab;
private static Sprite cachedDurationSprite;
private static Material cachedDurationMaterial;
private static Color cachedDurationColor = Color.white;
private static Image.Type cachedDurationType = Image.Type.Filled;
private static Image.FillMethod cachedDurationFillMethod = Image.FillMethod.Radial360;
private static int cachedDurationFillOrigin = (int)Image.Origin360.Bottom;
private static bool cachedDurationFillClockwise;
```

These are loaded **once** from RoR2's prefabs and reused for all overlay instances.

### TryBuildUi

This method constructs the UI overlay:

#### Step 1: Load Temp Item Indicator Prefab

```csharp
if (!triedLoadDurationTemplate)
{
    triedLoadDurationTemplate = true;
    try
    {
        // Prefer the same temp-item indicator object used by PickupDisplay.
        // This is typically a blue ring with no background.
        var hiddenPickupModel = PickupCatalog.GetHiddenPickupDisplayPrefab();
        var pickupDisplay = hiddenPickupModel ? hiddenPickupModel.GetComponentInChildren<PickupDisplay>(true) : null;
        cachedTempItemIndicatorPrefab = pickupDisplay ? pickupDisplay.temporaryItemIndicator : null;
    }
    catch
    {
        cachedTempItemIndicatorPrefab = null;
    }
```

Loads the "mystery pickup" prefab and extracts its `temporaryItemIndicator` child object.

#### Step 2: Load Fallback Duration Ring Settings

```csharp
    try
    {
        var itemIconPrefab = LegacyResourcesAPI.Load<GameObject>("Prefabs/UI/ItemIcon");
        var itemIcon = itemIconPrefab ? itemIconPrefab.GetComponent<ItemIcon>() : null;
        var template = itemIcon ? itemIcon.durationImage : null;
        if (template)
        {
            cachedDurationSprite = template.sprite;
            cachedDurationMaterial = template.material;
            cachedDurationColor = template.color;
            cachedDurationType = template.type;
            cachedDurationFillMethod = template.fillMethod;
            cachedDurationFillOrigin = template.fillOrigin;
            cachedDurationFillClockwise = template.fillClockwise;
        }
    }
    catch
    {
        cachedDurationSprite = null;
        cachedDurationMaterial = null;
    }
}
```

If the temp item indicator isn't available, fall back to copying settings from `ItemIcon.durationImage`.

#### Step 3: Create Timer Root

```csharp
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
```

Creates a 18×18 pixel container anchored to the **bottom-left** of the portrait icon.

#### Step 4: Instantiate Temp Item Indicator

```csharp
if (!tempItemIndicatorInstance && cachedTempItemIndicatorPrefab)
{
    tempItemIndicatorInstance = UnityEngine.Object.Instantiate(cachedTempItemIndicatorPrefab, timerRoot.transform, worldPositionStays: false);
    tempItemIndicatorInstance.name = "TempItemIndicator";
    tempItemIndicatorInstance.transform.localPosition = Vector3.zero;
    tempItemIndicatorInstance.transform.localRotation = Quaternion.identity;
    tempItemIndicatorInstance.transform.localScale = Vector3.one;

    // Ensure only the ring is visible (no background): disable non-filled Images.
    // Then select the filled Image as the one we drive.
    var images = tempItemIndicatorInstance.GetComponentsInChildren<Image>(true);
    Image best = null;
    for (int i = 0; i < images.Length; i++)
    {
        var img = images[i];
        if (!img)
        {
            continue;
        }
        img.raycastTarget = false;
        if (!best && img.type == Image.Type.Filled)
        {
            best = img;
            continue;
        }
    }

    if (best)
    {
        // Hide any other Image components so there is no background.
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] && images[i] != best)
            {
                images[i].enabled = false;
            }
        }
        timerImage = best;
    }
}
```

##### Key Steps

1. Instantiate the temp item indicator prefab.
2. Find all `Image` components in it.
3. Disable all **except** the first `Image.Type.Filled` one (the ring).
4. Disable `raycastTarget` on all images (so they don't interfere with UI input).

This ensures **only the blue ring shows, with no background**.

#### Step 5: Fallback Ring Creation

```csharp
// Fallback: create our own duration ring Image from the ItemIcon template.
if (!timerImage)
{
    var ringObj = new GameObject("DurationRing", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    ringObj.transform.SetParent(timerRoot.transform, worldPositionStays: false);
    ringObj.transform.SetAsLastSibling();

    var rt = (RectTransform)ringObj.transform;
    rt.anchorMin = new Vector2(0f, 0f);
    rt.anchorMax = new Vector2(1f, 1f);
    rt.pivot = new Vector2(0.5f, 0.5f);
    rt.anchoredPosition = Vector2.zero;
    rt.sizeDelta = Vector2.zero;

    timerImage = ringObj.GetComponent<Image>();
    timerImage.raycastTarget = false;
    timerImage.sprite = cachedDurationSprite;
    timerImage.material = cachedDurationMaterial;
    timerImage.color = cachedDurationColor;
    timerImage.type = cachedDurationType;
    timerImage.fillMethod = cachedDurationFillMethod;
    timerImage.fillOrigin = cachedDurationFillOrigin;
    timerImage.fillClockwise = cachedDurationFillClockwise;
    timerImage.fillAmount = 1f;
    timerImage.enabled = false;
}
```

If the temp item indicator couldn't be loaded or didn't have a suitable `Image`, create a basic filled `Image` manually using the cached settings from `ItemIcon.durationImage`.

### LateUpdate

```csharp
private void LateUpdate()
{
    if (!timerImage || !allyCard)
    {
        TryBuildUi();
        return;
    }

    var master = allyCard.sourceMaster;
    var body = master ? master.GetBody() : null;
    if (!body)
    {
        if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
        timerImage.enabled = false;
        return;
    }

    var uptime = body.GetComponent<DroneUptimeTimer>();
    if (!uptime)
    {
        if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
        timerImage.enabled = false;
        return;
    }

    // Only show for drones/turrets (not all allies).
    if (DroneCatalog.GetDroneIndexFromBodyIndex(body.bodyIndex) == DroneIndex.None)
    {
        if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(false);
        timerImage.enabled = false;
        return;
    }

    if (tempItemIndicatorInstance) tempItemIndicatorInstance.SetActive(true);
    timerImage.enabled = true;
    timerImage.fillAmount = uptime.remainingFraction;
}
```

#### Every Frame

1. Ensure UI is built.
2. Get the ally's `CharacterBody` via `sourceMaster.GetBody()`.
3. Require the body has a `DroneUptimeTimer` component.
4. Require the body is a drone/turret (`DroneCatalog` check).
5. If all valid:
   - Show the indicator
   - Set `fillAmount` to `uptime.remainingFraction`

The ring visually drains as the timer approaches zero.

---

## Repair Spawner System

### DroneUptimeRepairSpawner Static Class

This class handles the transition from active drone to broken repair interactable.

#### SpawnBrokenAndRemove

```csharp
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
        Vector3 spawnPos = FindGround(droneBody.corePosition, droneBody.radius);
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
    if (droneBody.master)
    {
        UnityEngine.Networking.NetworkServer.Destroy(droneBody.master.gameObject);
    }
    else
    {
        UnityEngine.Networking.NetworkServer.Destroy(droneBody.gameObject);
    }
}
```

##### Step-by-Step

1. **Validate server authority**: Only runs on server.
2. **Get broken spawn card**: Uses `GetBrokenDroneSpawnCard`.
3. **Find ground position**: Raycasts downward to place the interactable on the ground.
4. **Spawn via Director**: Uses RoR2's spawning system with seeded RNG.
5. **Scale repair cost**: Uses `Run.GetDifficultyScaledCost` (same as vanilla).
6. **Remove active drone**: Destroys the master (cleanly removes the entity).

#### GetBrokenDroneSpawnCard

```csharp
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
```

##### Fallback Strategy

1. **Preferred**: Use `DroneDef.droneBrokenSpawnCard` if set.
2. **Fallback**: Construct spawn card name from body name:
   - Get body name from `BodyCatalog`
   - Remove "Body" suffix
   - Prepend "iscBroken"
   - Example: `DroneBackup` → `iscBrokenDroneBackup`

This mirrors vanilla RoR2 broken drone naming conventions.

#### FindGround

```csharp
private static Vector3 FindGround(Vector3 origin, float radius)
{
    if (Physics.Raycast(origin + Vector3.up * 1f, Vector3.down, out var hit, 20f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
    {
        return hit.point;
    }
    return origin;
}
```

Raycasts downward from slightly above the drone to find a valid ground position. If no ground is found, spawns at the drone's position.

---

## Configuration Reference

### Config File Structure

The mod generates a config file at:  
`BepInEx/config/AdoptedFatty.AdjustedDrones.cfg`

### Scaling Section

```ini
[Scaling]

## Effective duration = baseDuration / ((difficultyCoefficient * multiplier) ^ exponent). Uses Run.difficultyCoefficient only. Updates dynamically as the run difficulty ramps. Set to 0 to disable scaling.
# Setting type: Single
# Default value: 1
DifficultyCoefficientExponent = 1

## Multiplier applied to Run.difficultyCoefficient before exponentiation. 1 = unchanged.
# Setting type: Single
# Default value: 1
DifficultyCoefficientMultiplier = 1
```

### Duration Sections

```ini
[Duration.White]
UtilitySeconds = 240
CombatSeconds = 180

[Duration.Green]
UtilitySeconds = 180
CombatSeconds = 135

[Duration.Red]
UtilitySeconds = 120
CombatSeconds = 90
```

### Overrides Section

```ini
[Overrides]
EnablePerDroneDurationOverrides = false

[Overrides.PerDrone]
# These are created dynamically when you encounter each drone type
Drone_3_Drone1BodySeconds = -1
Drone_7_BackupDroneBodySeconds = -1
# ... etc
```

### Config Value Meanings

| Value                                 | Effect                                      |
| ------------------------------------- | ------------------------------------------- |
| `DifficultyCoefficientExponent = 0`   | Disables scaling entirely (fixed durations) |
| `DifficultyCoefficientExponent = 1`   | Linear scaling with difficulty              |
| `DifficultyCoefficientExponent = 2`   | Exponential scaling (much faster reduction) |
| `DifficultyCoefficientMultiplier > 1` | Makes scaling more aggressive               |
| `DifficultyCoefficientMultiplier < 1` | Makes scaling less aggressive               |
| `PerDrone override = -1`              | Uses tier/class duration                    |
| `PerDrone override >= 0`              | Uses this specific value (before scaling)   |

---

## Runtime Flow Summary

### Startup (Awake)

1. Plugin loads → `Awake` called.
2. Config bound → all entries created/loaded.
3. Hooks registered:
   - Body spawn detection
   - Target filtering
   - UI overlay attachment

### During Gameplay

#### When a Drone Spawns

1. `OnBodyStartGlobal` fires.
2. Validates: player team, minion ownership, drone catalog entry.
3. Adds `DroneUptimeTimer` component.
4. Ensures per-drone config exists.
5. Configures timer with base duration.

#### Every Frame

1. **Timer Update**:

   - `DroneUptimeTimer.Update()` increments age.
   - Computes dynamic duration from current difficulty.
   - Checks if expired.

2. **UI Update**:

   - `DroneUptimeAllyCardOverlay.LateUpdate()` updates ring.
   - Sets `fillAmount = remainingFraction`.

3. **Target Queries**:
   - Enemies search for targets via `BullseyeSearch`.
   - Mod filters out player drones from results.

#### When Timer Expires (Server)

1. `DroneUptimeTimer` detects `age >= duration`.
2. Calls `BreakToRepair()`.
3. `DroneUptimeRepairSpawner.SpawnBrokenAndRemove()`:
   - Spawns broken repair interactable.
   - Scales repair cost by difficulty.
   - Destroys active drone.

#### When Player Repairs

Vanilla RoR2 repair logic takes over:

- Player interacts with broken drone.
- Pays scaled cost.
- Drone respawns.
- Mod attaches new timer (cycle repeats).

---

## Key Design Decisions

### Why Dynamic Duration?

The `duration` property is computed every time it's accessed:

```csharp
public float duration => DroneUptimeDuration.GetScaledDurationSeconds(baseDuration);
```

This means:

- If difficulty increases mid-run, the timer effectively speeds up.
- If difficulty decreases (via modifiers/artifacts), the timer slows down.
- The UI ring rate changes to match.

### Why Use Temp Item Indicator?

The temp item indicator prefab (`PickupDisplay.temporaryItemIndicator`) is:

- Already styled correctly (blue ring, no background)
- Tested and consistent with vanilla visuals
- Requires no custom art assets

### Why Spawn Broken Interactables Instead of Killing?

1. **Consistency with vanilla**: Drones already have broken states.
2. **Player agency**: Players can choose when/whether to repair.
3. **Cost scaling**: Repair cost scales with difficulty automatically.
4. **Recovery**: Drones aren't permanently lost; they're just temporarily unavailable.

---

## Troubleshooting Guide

### "Ring doesn't show at all"

**Possible causes**:

- Temp item indicator prefab failed to load.
- Fallback sprite also failed to load.
- Overlay component not attached to ally card.

**Check**:

- Look for errors in `BepInEx/LogOutput.log`.
- Verify `AllyCardController` has `DroneUptimeAllyCardOverlay` component in game.

### "Ring shows but doesn't drain"

**Possible causes**:

- Timer not attached to drone body.
- Drone not recognized in `DroneCatalog`.

**Check**:

- Does the drone have `DroneUptimeTimer` component?
- Is `remainingFraction` changing over time?

### "Drones still get attacked"

**Not all AI uses BullseyeSearch**. Some enemies have hardcoded targeting logic.

**Expected**: Drones receive _less_ aggro, not zero aggro.

### "Duration scaling feels wrong"

**Check config**:

- `DifficultyCoefficientExponent` set correctly?
- `DifficultyCoefficientMultiplier` at intended value?

**Remember**: Higher difficulty → **shorter** duration (inverse relationship).

---

## Extending the Mod

### Adding New Tier Categories

Currently supports White/Green/Red. To add more:

1. Add enum value to `DroneUptimeRarityTier`.
2. Add mapping in `ComputeBaseDurationSeconds` switch.
3. Add `BindDuration` calls in `BindConfig`.

### Custom Scaling Formula

To use a different scaling formula:

1. Modify `GetScaledDurationSeconds` method.
2. Update config description to reflect new formula.

### Alternative UI Placement

To move the ring:

1. Modify anchor values in `TryBuildUi`:
   ```csharp
   rt.anchorMin = new Vector2(1f, 0f); // bottom-right
   ```

### Different Expiry Behavior

Instead of spawning broken interactable, you could:

1. Teleport drone back to player.
2. Apply a debuff/cooldown.
3. Trigger a repair-bot summon.

Modify `BreakToRepair` and `SpawnBrokenAndRemove` accordingly.

---

## Performance Considerations

### Hook Overhead

The `BullseyeSearch.GetResults` hook runs frequently (every targeting query). The filter is optimized with early returns:

```csharp
if (self.viewer.teamComponent.teamIndex == TeamIndex.Player)
{
    return results; // No filtering needed for player viewers
}
```

### UI Updates

`LateUpdate` runs every frame but performs minimal work:

- One component lookup
- One property read (`remainingFraction`)
- One property write (`fillAmount`)

### Timer Calculations

Duration is recalculated every access, but the formula is simple (one power operation). No noticeable performance impact.

---

## Compatibility Notes

### Compatible With

- Most other drone mods (unless they also modify aggro)
- Difficulty-scaling mods (auto-adapts to coefficient changes)
- UI mods (uses vanilla ally card system)

### Potential Conflicts

- Mods that drastically change drone behavior
- Mods that heavily modify `BullseyeSearch`
- Mods that replace ally card UI entirely

---

## Credits and License

**Author**: AdoptedFatty  
**Version**: 1.0.0  
**Framework**: BepInEx  
**Game**: Risk of Rain 2

This document and code are provided as-is for educational and modding purposes.

---

**End of Documentation**
