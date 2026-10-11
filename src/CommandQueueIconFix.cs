using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using RoR2;
using RoR2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CommandQueueIconFix
{
    // For CommandQueueExtended 2.0.0 (com.kuberoot.commandqueuewithsave).
    // The queue contents already use RoR2.UI.ItemIcon. The grid used to ADD
    // items to the queue still assigns PickupDef.iconSprite to a plain Image.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.kuberoot.commandqueuewithsave", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class CommandQueueIconFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.commandqueue.iconfix";
        public const string PluginName = "CommandQueueExtended Icon Fix";
        public const string PluginVersion = "1.2.0";

        private const string OverlayName = "CommandQueueIconFix_ItemIcon";
        private const string CountName = "CommandQueueIconFix_OwnedCount";
        private const string HitAreaName = "CommandQueueIconFix_TooltipHitArea";
        private const string IconResourceName = "CommandQueueIconFix.icon.png";
        private const float RefreshInterval = 0.2f;
        private const float LookingGlassLookupInterval = 5f;

        public enum TooltipPosition
        {
            BottomCenter,
            Cursor
        }

        private const BindingFlags DeclaredMethods = BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        private static CommandQueueIconFixPlugin instance;
        private static GameObject vanillaItemIconPrefab;
        private static bool failedToFindPrefab;
        private static int exceptionCount;
        private static int tooltipExceptionCount;
        private static int hoverExceptionCount;
        private static int tooltipRecoveryLogCount;
        private static readonly Color UnownedCountColor = new Color(0.50f, 0.50f, 0.50f, 1f);

        // Calls to the getter and setter are adjacent in the CommandQueue
        // button-creation callback. This is only used on Unity's main thread.
        [ThreadStatic]
        private static PickupDef currentPickup;

        private static readonly FieldInfo TooltipOwnerField =
            AccessTools.Field(typeof(TooltipController), "owner");
        private readonly List<QueueChoiceTag> trackedChoices = new List<QueueChoiceTag>();
        private ConfigEntry<TooltipPosition> tooltipPosition;
        private ConfigEntry<bool> ignoreEnableInspect;
        private Sprite riskOfOptionsIcon;
        private Texture2D riskOfOptionsIconTexture;
        private Harmony harmony;
        private MethodInfo lookingGlassDescription;
        private float nextRefresh;
        private float nextLookingGlassLookup;
        private bool loggedLookingGlassException;
        private bool loggedLookingGlassMismatch;
        private bool loggedCounterException;

        private void Awake()
        {
            instance = this;
            tooltipPosition = Config.Bind("Tooltips", "Position", TooltipPosition.BottomCenter,
                "Position of item tooltips in the Command Queue item-selection grid. " +
                "BottomCenter shows them at the bottom center of the screen; " +
                "Cursor uses the game's normal tooltip position. " +
                "Inspect tooltips are shown when Enable Inspect is enabled, or when " +
                "IgnoreEnableInspect is enabled in this plugin.");
            ignoreEnableInspect = Config.Bind("Tooltips", "IgnoreEnableInspect", false,
                "Show Command Queue item tooltips even when the game's Enable Inspect " +
                "option is disabled. Does not change the game's own setting. " +
                "Changes apply immediately; owned-item counters are unaffected.");
            tooltipPosition.SettingChanged += OnTooltipPositionChanged;
            ignoreEnableInspect.SettingChanged += OnIgnoreInspectChanged;
            Logger.LogInfo("Tooltip position=" + tooltipPosition.Value +
                "; ignore Enable Inspect=" + ignoreEnableInspect.Value +
                "; native inspect is still read from the local Risk of Rain 2 profile.");
            RegisterRiskOfOptionsIfAvailable();

            // This affects only tooltips whose TooltipProvider is tagged as one
            // of our grid choices. All other game and mod tooltips are unchanged.
            try
            {
                if (TooltipOwnerField == null)
                    throw new MissingFieldException("RoR2.UI.TooltipController", "owner");

                harmony = new Harmony(PluginGuid);
                harmony.Patch(AccessTools.Method(typeof(TooltipController), "LateUpdate"),
                    postfix: new HarmonyMethod(typeof(CommandQueueIconFixPlugin),
                        nameof(RepositionQueueTooltip)));
                Logger.LogInfo("Command Queue tooltip positioning patch installed.");

                // Some UI controls deselect their button while the mouse remains
                // over it. Only protect *our* active hover tooltips against
                // premature removal; all other game tooltips are unaffected.
                MethodInfo removeTooltip = AccessTools.Method(
                    typeof(TooltipController), "RemoveTooltip",
                    new[] { typeof(MPEventSystem), typeof(TooltipProvider) });
                if (removeTooltip != null)
                {
                    harmony.Patch(removeTooltip,
                        prefix: new HarmonyMethod(typeof(CommandQueueIconFixPlugin),
                            nameof(KeepHoveredQueueTooltip)));
                    Logger.LogInfo("Command Queue hovered-tooltip lifetime guard installed.");
                }
                else
                {
                    Logger.LogWarning("Cannot patch native tooltip removal. Hover recovery remains available.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Cannot install optional tooltip patches: " + ex);
            }

            Type uiManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("CommandQueue.UIManager", false))
                .FirstOrDefault(type => type != null);

            if (uiManagerType == null)
            {
                Logger.LogError("Cannot find CommandQueue.UIManager. " +
                    "Check that CommandQueueExtended 2.0.0 is installed and enabled.");
                return;
            }

            if (harmony == null)
                harmony = new Harmony(PluginGuid);
            int callbacksFound = 0;
            foreach (Type nested in FindNestedTypes(uiManagerType))
            {
                foreach (MethodInfo method in nested.GetMethods(DeclaredMethods))
                {
                    if (!method.Name.Contains("<CreateQueueContainer>") ||
                        method.GetMethodBody() == null)
                        continue;

                    try
                    {
                        harmony.Patch(method,
                            transpiler: new HarmonyMethod(
                                typeof(CommandQueueIconFixPlugin),
                                nameof(ReplaceLegacyGridSprites)));
                        callbacksFound++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Cannot patch " + method.Name + ": " + ex);
                    }
                }
            }

            Logger.LogInfo("v" + PluginVersion + " installed Harmony patches on " + callbacksFound +
                " CreateQueueContainer callback(s).");
            if (callbacksFound == 0)
                Logger.LogError("No matching callbacks found: this mod version may have changed.");
        }

        private void OnTooltipPositionChanged(object sender, EventArgs args)
        {
            // RepositionQueueTooltip reads this same ConfigEntry every frame;
            // no patch reinstall, queue rebuild, or game restart is needed.
            Logger.LogInfo("Tooltip position changed to " + tooltipPosition.Value +
                " (effective immediately).");
        }

        private void OnIgnoreInspectChanged(object sender, EventArgs args)
        {
            // Refresh TooltipProvider.enabled promptly; tooltip lifecycle and
            // the separate game setting are otherwise unchanged.
            nextRefresh = 0f;
            Logger.LogInfo("Ignore Enable Inspect changed to " + ignoreEnableInspect.Value +
                " (effective immediately; game setting unchanged).");
        }

        private static string GetLocalizedInspectOptionName()
        {
            // Best effort: these option tokens may differ across game builds.
            // Never display an untranslated token identifier in the menu.
            string[] candidates = {
                "OPTION_ENABLE_INSPECT_NAME",
                "OPTION_GAMEPLAY_ENABLE_INSPECT_NAME",
                "OPTION_ENABLEINSPECT_NAME",
                "OPTION_INSPECT_NAME",
                "OPTION_GAMEPLAY_INSPECT_NAME"
            };
            try
            {
                foreach (string token in candidates)
                {
                    string translated = Language.GetString(token);
                    if (!string.IsNullOrWhiteSpace(translated) &&
                        !string.Equals(translated, token, StringComparison.Ordinal))
                        return translated;
                }
            }
            catch
            {
                // Language resources may not be initialized yet in Awake.
                // Localization must never prevent the setting from registering.
            }
            return "Enable Inspect";
        }

        private void RegisterRiskOfOptionsIfAvailable()
        {
            // RiskOfOptions is completely optional. Reflection lets the same
            // DLL build and run without RiskOfOptions.dll being installed.
            // The soft BepInDependency above ensures RiskOfOptions initializes
            // first when it is present.
            Assembly optionsAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    assembly.GetType("RiskOfOptions.ModSettingsManager", false) != null);
            if (optionsAssembly == null)
            {
                Logger.LogInfo("RiskOfOptions not installed; tooltip position and inspect " +
                    "override remain configurable through BepInEx.");
                return;
            }

            try
            {
                Type managerType = optionsAssembly.GetType("RiskOfOptions.ModSettingsManager", true);
                Type baseOptionType = optionsAssembly.GetType("RiskOfOptions.Options.BaseOption", true);
                Type choiceOptionType = optionsAssembly.GetType("RiskOfOptions.Options.ChoiceOption", true);
                Type choiceConfigType = optionsAssembly.GetType("RiskOfOptions.OptionConfigs.ChoiceConfig", true);

                object choiceConfig = Activator.CreateInstance(choiceConfigType);
                choiceConfigType.GetField("name").SetValue(choiceConfig, "Tooltip Position");
                choiceConfigType.GetField("category").SetValue(choiceConfig, "Tooltips");
                choiceConfigType.GetField("description").SetValue(choiceConfig,
                    "Position of the item description when hovering over an available " +
                    "Command Queue item. Follows the game's Enable Inspect option " +
                    "unless Ignore Enable Inspect is enabled. Changes apply immediately.");
                choiceConfigType.GetField("restartRequired").SetValue(choiceConfig, false);

                // RiskOfOptions 2.8.x exposes ChoiceOption(ConfigEntryBase,
                // ChoiceConfig) and AddOption(BaseOption, modGuid, modName).
                // Passing the original ConfigEntry keeps the .cfg and dropdown
                // synchronized; no second configuration value is created.
                object choiceOption = Activator.CreateInstance(choiceOptionType,
                    new object[] { tooltipPosition, choiceConfig });
                MethodInfo addOption = managerType.GetMethod("AddOption",
                    new[] { baseOptionType, typeof(string), typeof(string) });
                if (addOption == null)
                    throw new MissingMethodException("RiskOfOptions.ModSettingsManager", "AddOption");
                addOption.Invoke(null, new object[] { choiceOption, PluginGuid, PluginName });
                Logger.LogInfo("RiskOfOptions registered Tooltip Position dropdown " +
                    "(BottomCenter / Cursor); no restart required.");

                // Register the independent live boolean option. Keep the
                // original enum dropdown even if this optional registration
                // fails on a different RiskOfOptions release.
                try
                {
                    Type checkBoxType = optionsAssembly.GetType(
                        "RiskOfOptions.Options.CheckBoxOption", true);
                    Type checkBoxConfigType = optionsAssembly.GetType(
                        "RiskOfOptions.OptionConfigs.CheckBoxConfig", true);
                    object checkBoxConfig = Activator.CreateInstance(checkBoxConfigType);
                    checkBoxConfigType.GetField("name").SetValue(checkBoxConfig,
                        "Ignore " + GetLocalizedInspectOptionName());
                    checkBoxConfigType.GetField("category").SetValue(checkBoxConfig, "Tooltips");
                    checkBoxConfigType.GetField("description").SetValue(checkBoxConfig,
                        "When enabled, Command Queue item tooltips appear even if the " +
                        "game's Enable Inspect setting is off. The normal game " +
                        "setting is not modified. Changes apply immediately.");
                    checkBoxConfigType.GetField("restartRequired").SetValue(checkBoxConfig, false);
                    object checkBox = Activator.CreateInstance(checkBoxType,
                        new object[] { ignoreEnableInspect, checkBoxConfig });
                    addOption.Invoke(null, new object[] { checkBox, PluginGuid, PluginName });
                    Logger.LogInfo("RiskOfOptions registered Ignore Enable Inspect checkbox; " +
                        "no restart required.");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Could not register optional RiskOfOptions " +
                        "Ignore Enable Inspect checkbox. The BepInEx setting still works: " + ex);
                }

                // Thunderstore's icon.png is package metadata, not an icon
                // RiskOfOptions automatically imports. Supply a Sprite via
                // SetModIcon when a bundled or adjacent PNG is available.
                try
                {
                    RegisterRiskOfOptionsIcon(managerType);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Could not register optional RiskOfOptions icon: " + ex);
                }

                // Optional description for the entry in Mod Options.
                try
                {
                    MethodInfo setDescription = managerType.GetMethod("SetModDescription",
                        new[] { typeof(string), typeof(string), typeof(string) });
                    if (setDescription != null)
                        setDescription.Invoke(null, new object[] {
                            "Restores CommandQueueExtended item icons and adds " +
                            "owned-item counters and inspect tooltips.",
                            PluginGuid, PluginName
                        });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Could not set optional RiskOfOptions mod description: " + ex);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("RiskOfOptions detected, but Tooltip Position " +
                    "could not be registered. The .cfg setting still works: " + ex);
            }
        }

        private void RegisterRiskOfOptionsIcon(Type managerType)
        {
            byte[] png = null;
            string source = null;
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream resource = assembly.GetManifestResourceStream(IconResourceName))
            {
                if (resource != null)
                {
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        resource.CopyTo(buffer);
                        png = buffer.ToArray();
                    }
                    source = "embedded resource";
                }
            }

            if (png == null)
            {
                string pluginDirectory = System.IO.Path.GetDirectoryName(assembly.Location);
                if (!string.IsNullOrEmpty(pluginDirectory))
                {
                    string iconPath = System.IO.Path.Combine(pluginDirectory, "icon.png");
                    if (File.Exists(iconPath))
                    {
                        png = File.ReadAllBytes(iconPath);
                        source = iconPath;
                    }
                }
            }

            if (png == null)
            {
                Logger.LogInfo("RiskOfOptions icon not found. Put icon.png beside " +
                    "CommandQueueIconFix.dll, or put it in the project directory " +
                    "before building to embed it.");
                return;
            }
            if (png.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("RiskOfOptions icon PNG is larger than 4 MiB.");

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, png, false))
                    throw new InvalidDataException("RiskOfOptions icon.png is not a valid PNG.");
                Sprite icon = Sprite.Create(texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                if (icon == null)
                    throw new InvalidOperationException("Could not create icon Sprite.");

                MethodInfo setIcon = managerType.GetMethod("SetModIcon",
                    new[] { typeof(Sprite), typeof(string), typeof(string) });
                if (setIcon == null)
                {
                    UnityEngine.Object.Destroy(icon);
                    throw new MissingMethodException("RiskOfOptions.ModSettingsManager",
                        "SetModIcon(Sprite, string, string)");
                }
                try
                {
                    setIcon.Invoke(null, new object[] { icon, PluginGuid, PluginName });
                }
                catch
                {
                    UnityEngine.Object.Destroy(icon);
                    throw;
                }
                riskOfOptionsIconTexture = texture;
                riskOfOptionsIcon = icon;
                Logger.LogInfo("RiskOfOptions mod icon registered from " + source + ".");
            }
            catch
            {
                if (riskOfOptionsIconTexture != texture)
                    UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        private static IEnumerable<Type> FindNestedTypes(Type type)
        {
            yield return type;
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (Type child in FindNestedTypes(nested))
                    yield return child;
            }
        }

        // Original IL in the available-items grid:
        //   ldfld PickupDef.iconSprite
        //   callvirt UnityEngine.UI.Image.set_sprite
        // The helpers keep EXACTLY the same argument and return types and
        // use the captured PickupDef to configure the cloned ItemIcon.
        public static IEnumerable<CodeInstruction> ReplaceLegacyGridSprites(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo capture = AccessTools.Method(
                typeof(CommandQueueIconFixPlugin), nameof(CapturePickup));
            MethodInfo assign = AccessTools.Method(
                typeof(CommandQueueIconFixPlugin), nameof(AssignItemIcon));
            int captures = 0;
            int assignments = 0;

            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo field &&
                    field.DeclaringType == typeof(PickupDef) && field.Name == "iconSprite")
                {
                    code.opcode = OpCodes.Call;
                    code.operand = capture;
                    captures++;
                }
                else if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
                    code.operand is MethodInfo setter &&
                    setter.DeclaringType == typeof(Image) && setter.Name == "set_sprite")
                {
                    code.opcode = OpCodes.Call;
                    code.operand = assign;
                    assignments++;
                }
                yield return code;
            }

            if (captures > 0 || assignments > 0)
            {
                instance?.Logger.LogInfo("Replaced icon calls in " +
                    __originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name +
                    ": " + captures + " pickup capture(s), " + assignments +
                    " Image.sprite setter(s).");
                if (captures != 1 || assignments < 1)
                    instance?.Logger.LogWarning("Unexpected icon IL pattern. " +
                        "This patch is intended for CommandQueueExtended 2.0.0.");
            }
        }

        public static Sprite CapturePickup(PickupDef definition)
        {
            currentPickup = definition;
            return definition != null ? definition.iconSprite : null;
        }

        public static void AssignItemIcon(Image target, Sprite originalSprite)
        {
            PickupDef pickup = currentPickup;
            currentPickup = null;
            if (target == null)
                return;

            // Preserve the original locked/unavailable-item appearance and
            // sprites in any non-item paths. Do not change click listeners.
            if (pickup == null || pickup.itemIndex == ItemIndex.None || instance == null)
            {
                target.sprite = originalSprite;
                return;
            }

            try
            {
                if (!instance.TryInstallItemIcon(target, pickup.itemIndex))
                    target.sprite = originalSprite;
            }
            catch (Exception ex)
            {
                // A failed cosmetic change must not break the queue UI.
                target.sprite = originalSprite;
                if (exceptionCount++ < 5)
                    instance.Logger.LogError("Failed ItemIcon for " + pickup.itemIndex + ": " + ex);
            }
        }

        private static GameObject FindItemIconPrefab()
        {
            if (vanillaItemIconPrefab != null)
                return vanillaItemIconPrefab;
            if (failedToFindPrefab)
                return null;

            try
            {
                // This is the same prefab source that CommandQueueExtended's
                // UIManager.Awake() uses for its queued-item display.
                GameObject hudPrefab = RoR2.LegacyResourcesAPI.Load<GameObject>("Prefabs/HUDSimple");
                HUD hud = hudPrefab != null ? hudPrefab.GetComponent<HUD>() : null;
                ItemInventoryDisplay inventory = hud != null ? hud.itemInventoryDisplay : null;
                vanillaItemIconPrefab = inventory != null ? inventory.itemIconPrefab : null;

                if (vanillaItemIconPrefab == null ||
                    vanillaItemIconPrefab.GetComponent<ItemIcon>() == null)
                {
                    instance?.Logger.LogError("HUDSimple itemIconPrefab is missing or " +
                        "does not have RoR2.UI.ItemIcon.");
                    failedToFindPrefab = true;
                    return null;
                }
                instance?.Logger.LogInfo("Reusing HUDSimple ItemIcon prefab: " +
                    vanillaItemIconPrefab.name);
                return vanillaItemIconPrefab;
            }
            catch (Exception ex)
            {
                failedToFindPrefab = true;
                instance?.Logger.LogError("Cannot load HUDSimple ItemIcon prefab: " + ex);
                return null;
            }
        }

        private bool TryInstallItemIcon(Image legacyImage, ItemIndex itemIndex)
        {
            GameObject prefab = FindItemIconPrefab();
            if (prefab == null)
                return false;

            Transform existing = legacyImage.transform.Find(OverlayName);
            GameObject container;
            ItemIcon icon;
            bool created = false;
            if (existing != null)
            {
                container = existing.gameObject;
                icon = container.GetComponent<ItemIcon>();
                if (icon == null)
                {
                    Logger.LogWarning("Existing icon overlay has no ItemIcon: " + itemIndex);
                    return false;
                }
            }
            else
            {
                container = UnityEngine.Object.Instantiate(prefab, legacyImage.transform, false);
                container.name = OverlayName;
                icon = container.GetComponent<ItemIcon>();
                RectTransform rect = container.GetComponent<RectTransform>();
                if (icon == null || rect == null)
                {
                    UnityEngine.Object.Destroy(container);
                    Logger.LogWarning("Cannot create an ItemIcon and RectTransform for " + itemIndex);
                    return false;
                }

                // Stretch the VANILLA prefab into the existing Command choice
                // image, leaving its parent MPButton and click events unchanged.
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
                container.transform.SetAsLastSibling();

                // Child graphics must not intercept pointer clicks on the
                // CommandQueueExtended MPButton underneath.
                foreach (Graphic graphic in container.GetComponentsInChildren<Graphic>(true))
                    graphic.raycastTarget = false;
                CanvasGroup group = container.GetComponent<CanvasGroup>();
                if (group == null)
                    group = container.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                group.interactable = false;
                created = true;
            }

            // The game uses this component for queued icons already. Let it
            // resolve item art by its current (v1.5.x) implementation.
            icon.SetItemIndex(itemIndex, 1);
            legacyImage.sprite = null;
            legacyImage.overrideSprite = null;
            legacyImage.enabled = false;

            // The icon itself is independent from the optional tooltip and
            // count features. Never allow either one to break the icon fix.
            try
            {
                RegisterQueueChoice(legacyImage, icon, itemIndex);
            }
            catch (Exception ex)
            {
                if (!loggedCounterException)
                {
                    loggedCounterException = true;
                    Logger.LogError("Could not attach Command Queue count/tooltip: " + ex);
                }
            }

            if (created)
                StartCoroutine(RebindItemIcon(icon, itemIndex));
            return true;
        }

        private IEnumerator RebindItemIcon(ItemIcon icon, ItemIndex itemIndex)
        {
            // Rebind after Unity's Awake/OnEnable/Start of the cloned prefab.
            // Keep this second assignment: it prevents the original icon fix
            // from depending on prefab initialization order.
            yield return null;
            if (icon == null)
                yield break;
            try
            {
                icon.SetItemIndex(itemIndex, 1);
            }
            catch (Exception ex)
            {
                if (exceptionCount++ < 5)
                    Logger.LogError("ItemIcon.SetItemIndex failed for " + itemIndex + ": " + ex);
            }
        }

        private void RegisterQueueChoice(Image legacyImage, ItemIcon icon, ItemIndex itemIndex)
        {
            MPButton button = legacyImage.GetComponentInParent<MPButton>();
            if (button == null)
            {
                Logger.LogWarning("Cannot find the Command Queue choice button for " + itemIndex);
                return;
            }

            QueueChoiceTag tag = button.GetComponent<QueueChoiceTag>();
            if (tag == null)
            {
                tag = button.gameObject.AddComponent<QueueChoiceTag>();
                tag.Hud = button.GetComponentInParent<HUD>();
            }
            if (!trackedChoices.Contains(tag))
                trackedChoices.Add(tag);

            tag.ItemIndex = itemIndex;
            tag.Icon = icon;

            // Reuse the button's own tooltip provider when present. The native
            // picker prefab may already contain one, but CommandQueueExtended
            // doesn't initialize it for its manually constructed choices.
            tag.Tooltip = button.GetComponent<TooltipProvider>();
            if (tag.Tooltip == null)
                tag.Tooltip = button.gameObject.AddComponent<TooltipProvider>();
            // The original picker buttons can change selection while the
            // mouse is still hovering. With removeTooltipOnDeselect=true,
            // TooltipProvider closes the popup even though no pointer exit
            // occurred (consistent with the v1.1.2 runtime logs).
            // These descriptions are mouse-hover only, not navigation-select.
            tag.Tooltip.AllowTooltipOnNavigationSelect = false;
            tag.Tooltip.removeTooltipOnDeselect = false;

            // CommandQueueExtended reuses picker buttons but does not initialize
            // their tooltip/raycast path.  Add an invisible target on the same
            // button so both TooltipProvider and MPButton receive pointer events.
            // It is transparent, does not change the icon, and clicks still
            // propagate to the original MPButton.
            EnsureTooltipHitArea(button);

            Transform existingCount = legacyImage.transform.Find(CountName);
            if (existingCount != null)
                tag.Counter = existingCount.GetComponent<TextMeshProUGUI>();

            if (tag.Counter == null)
            {
                GameObject counterObject = new GameObject(CountName,
                    typeof(RectTransform), typeof(TextMeshProUGUI));
                counterObject.layer = legacyImage.gameObject.layer;
                counterObject.transform.SetParent(legacyImage.transform, false);
                RectTransform rect = counterObject.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.one;
                rect.anchorMax = Vector2.one;
                rect.pivot = Vector2.one;
                rect.anchoredPosition = new Vector2(-2f, -1f);
                rect.sizeDelta = new Vector2(33f, 16f);

                TextMeshProUGUI counter = counterObject.GetComponent<TextMeshProUGUI>();
                if (icon.stackText != null && icon.stackText.font != null)
                    counter.font = icon.stackText.font;
                counter.fontSize = 13f;
                counter.enableAutoSizing = true;
                counter.fontSizeMin = 10f;
                counter.fontSizeMax = 13f;
                counter.fontStyle = FontStyles.Bold;
                counter.alignment = TextAlignmentOptions.TopRight;
                counter.color = Color.white;
                counter.outlineColor = Color.black;
                counter.outlineWidth = 0.18f;
                counter.raycastTarget = false;
                counter.text = "x0";
                tag.Counter = counter;
            }
            // The count must be in front of the cloned icon graphics.
            tag.Counter.transform.SetAsLastSibling();
            tag.LastCount = int.MinValue;
            RefreshChoice(tag);
        }

        private static void EnsureTooltipHitArea(MPButton button)
        {
            Transform existing = button.transform.Find(HitAreaName);
            Image target = existing != null ? existing.GetComponent<Image>() : null;
            if (target == null)
            {
                GameObject hitObject = new GameObject(HitAreaName,
                    typeof(RectTransform), typeof(Image));
                hitObject.layer = button.gameObject.layer;
                hitObject.transform.SetParent(button.transform, false);
                RectTransform rect = hitObject.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                target = hitObject.GetComponent<Image>();
            }
            target.color = new Color(1f, 1f, 1f, 0f);
            target.raycastTarget = true;
            target.transform.SetAsLastSibling();
        }

        // Unity's PointerEventData has no eventSystem member. Use the
        // MPEventSystem belonging to the local player who owns the queue,
        // falling back to Unity's current event system when necessary.
        // This does not instantiate or alter a tooltip prefab ourselves.
        private static MPEventSystem ResolveChoiceEventSystem(LocalUser user)
        {
            MPEventSystem localSystem = user != null ? user.eventSystem : null;
            return localSystem ?? (EventSystem.current as MPEventSystem);
        }

        internal static void HandleChoicePointerEnter(QueueChoiceTag tag, PointerEventData eventData)
        {
            if (instance == null || tag == null)
                return;

            tag.Hovered = true;
            tag.PointerPosition = eventData != null ? eventData.position : Vector2.zero;
            tag.RecoveryAttempts = 0;
            tag.NextRecoveryTime = 0f;
            try
            {
                LocalUser user = GetChoiceUser(tag);
                MPEventSystem eventSystem = ResolveChoiceEventSystem(user);
                tag.LastEventSystem = eventSystem;
                if (!IsInspectEnabled(user) || tag.Tooltip == null ||
                    !tag.Tooltip.isActiveAndEnabled || eventSystem == null ||
                    (string.IsNullOrEmpty(tag.Tooltip.titleText) &&
                     string.IsNullOrEmpty(tag.Tooltip.bodyText)))
                    return;

                // The game's TooltipProvider may have handled pointer-enter
                // already. Only request a new native tooltip when necessary.
                if (eventSystem.currentTooltipProvider == tag.Tooltip &&
                    eventSystem.currentTooltip != null)
                    return;

                // RoR2 SetTooltip ignores repeated requests for the same
                // provider. Clear a stale provider without a controller first.
                if (eventSystem.currentTooltipProvider == tag.Tooltip &&
                    eventSystem.currentTooltip == null)
                    TooltipController.RemoveTooltip(eventSystem, tag.Tooltip);

                TooltipController.SetTooltip(eventSystem, tag.Tooltip,
                    tag.PointerPosition, null);
            }
            catch (Exception ex)
            {
                if (hoverExceptionCount++ < 5)
                    instance.Logger.LogError("Command Queue tooltip enter failed for " +
                        tag.ItemIndex + ": " + ex);
            }
        }

        internal static void HandleChoicePointerExit(QueueChoiceTag tag, PointerEventData eventData)
        {
            if (instance == null || tag == null)
                return;
            tag.Hovered = false;
            tag.RecoveryAttempts = 0;
            try
            {
                MPEventSystem eventSystem = tag.LastEventSystem ??
                    ResolveChoiceEventSystem(GetChoiceUser(tag));
                // RoR2's TooltipProvider also removes its own tooltip, but
                // its handler relies on EventSystem.current, which might have
                // changed by the time it receives the exit event.
                if (eventSystem != null && tag.Tooltip != null)
                    TooltipController.RemoveTooltip(eventSystem, tag.Tooltip);
            }
            catch (Exception ex)
            {
                if (hoverExceptionCount++ < 5)
                    instance.Logger.LogWarning("Command Queue tooltip exit failed: " + ex);
            }
            tag.LastEventSystem = null;
        }

        // Harmony prefix for RoR2.UI.TooltipController.RemoveTooltip.
        // Never intercept removal for another provider, a different UI screen,
        // a disabled inspect option, or after the pointer has actually left.
        public static bool KeepHoveredQueueTooltip(MPEventSystem __0, TooltipProvider __1)
        {
            if (instance == null || __0 == null || __1 == null)
                return true;

            QueueChoiceTag tag = __1.GetComponent<QueueChoiceTag>();
            if (tag == null || tag.Tooltip != __1 || !tag.Hovered ||
                !tag.isActiveAndEnabled || !__1.isActiveAndEnabled ||
                !tag.gameObject.activeInHierarchy ||
                !IsInspectEnabled(GetChoiceUser(tag)))
                return true;

            // Only prevent clearing the currently displayed native tooltip.
            // Allow the provider reset path if the controller is already gone.
            if (__0.currentTooltipProvider != __1 || __0.currentTooltip == null)
                return true;

            return false;
        }

        // A last-resort, bounded recovery if the event system clears its state
        // without going through the native RemoveTooltip method. This runs only
        // for our hovered choices and never creates a custom tooltip window.
        private void LateUpdate()
        {
            foreach (QueueChoiceTag tag in trackedChoices)
            {
                if (tag == null || !tag.Hovered || tag.Tooltip == null ||
                    !tag.Tooltip.isActiveAndEnabled || !tag.gameObject.activeInHierarchy ||
                    !IsInspectEnabled(GetChoiceUser(tag)))
                    continue;

                MPEventSystem eventSystem = tag.LastEventSystem ??
                    ResolveChoiceEventSystem(GetChoiceUser(tag));
                if (eventSystem == null)
                    continue;

                if (eventSystem.currentTooltipProvider == tag.Tooltip &&
                    eventSystem.currentTooltip != null)
                    continue;

                // Never replace an unrelated native tooltip with a recovery
                // request just because a Command Queue button remains hovered.
                if (eventSystem.currentTooltipProvider != tag.Tooltip &&
                    eventSystem.currentTooltip != null)
                    continue;

                if (Time.unscaledTime < tag.NextRecoveryTime || tag.RecoveryAttempts >= 4)
                    continue;

                tag.NextRecoveryTime = Time.unscaledTime + 0.2f;
                tag.RecoveryAttempts++;
                try
                {
                    if (eventSystem.currentTooltipProvider == tag.Tooltip &&
                        eventSystem.currentTooltip == null)
                        TooltipController.RemoveTooltip(eventSystem, tag.Tooltip);

                    TooltipController.SetTooltip(eventSystem, tag.Tooltip,
                        tag.PointerPosition, null);
                    // Successful recovery is normal fallback behavior, not
                    // a user-facing warning. Keep a few debug-level entries
                    // in case detailed logging is enabled for support.
                    if (tooltipRecoveryLogCount++ < 3)
                        Logger.LogDebug("Recovered Command Queue tooltip for item=" +
                            tag.ItemIndex + ", attempt=" + tag.RecoveryAttempts + ".");
                }
                catch (Exception ex)
                {
                    if (tooltipExceptionCount++ < 3)
                        Logger.LogWarning("Could not recover hovered Command Queue tooltip: " + ex);
                }
            }
        }

        private static LocalUser GetChoiceUser(QueueChoiceTag tag)
        {
            if (tag.Hud != null && tag.Hud.localUserViewer != null)
                return tag.Hud.localUserViewer;
            return LocalUserManager.GetFirstLocalUser();
        }

        private static bool IsInspectEnabled(LocalUser user)
        {
            // Only affects this plugin's lower-grid TooltipProviders.
            // Never alter the player's actual useInspectFeature setting.
            return (instance != null && instance.ignoreEnableInspect != null &&
                    instance.ignoreEnableInspect.Value) ||
                   (user != null && user.userProfile != null &&
                    user.userProfile.useInspectFeature);
        }

        private void Update()
        {
            if (Time.unscaledTime < nextRefresh)
                return;
            nextRefresh = Time.unscaledTime + RefreshInterval;

            TryFindLookingGlass();
            for (int i = trackedChoices.Count - 1; i >= 0; --i)
            {
                QueueChoiceTag tag = trackedChoices[i];
                if (tag == null || tag.Tooltip == null || tag.Counter == null)
                {
                    trackedChoices.RemoveAt(i);
                    continue;
                }
                try
                {
                    RefreshChoice(tag);
                }
                catch (Exception ex)
                {
                    if (!loggedCounterException)
                    {
                        loggedCounterException = true;
                        Logger.LogError("Error updating Command Queue items: " + ex);
                    }
                }
            }
        }

        private void RefreshChoice(QueueChoiceTag tag)
        {
            LocalUser user = GetChoiceUser(tag);
            CharacterMaster master = user != null ? user.cachedMaster : null;
            Inventory inventory = master != null ? master.inventory : null;
            int count = inventory != null ? inventory.GetItemCountEffective(tag.ItemIndex) : 0;

            // This is the survivor's owned count, NOT the amount queued.
            if (tag.LastCount != count || tag.LastMaster != master ||
                tag.UsingLookingGlass != (lookingGlassDescription != null))
            {
                tag.Counter.text = "x" + count;
                tag.Counter.color = count == 0 ? UnownedCountColor : Color.white;
                tag.LastCount = count;
                tag.LastMaster = master;
                tag.UsingLookingGlass = lookingGlassDescription != null;

                ItemDef definition = ItemCatalog.GetItemDef(tag.ItemIndex);
                if (definition != null)
                {
                    // Setting individual fields does not reliably initialize
                    // TooltipProvider's native content/availability state.
                    // Use the same SetContent API as RoR2's picker tooltips.
                    tag.Tooltip.SetContent(new TooltipContent
                    {
                        titleToken = definition.nameToken,
                        titleColor = ColorCatalog.GetColor(definition.darkColorIndex),
                        bodyToken = definition.descriptionToken,
                        bodyColor = Color.white,
                        overrideTitleText = null,
                        overrideBodyText = GetDescription(definition, count, master),
                        disableTitleRichText = false,
                        disableBodyRichText = false
                    });
                }
            }

            bool enabled = IsInspectEnabled(user);
            if (tag.Tooltip.enabled != enabled)
                tag.Tooltip.enabled = enabled; // OnDisable also removes an open tooltip.
        }

        private void TryFindLookingGlass()
        {
            if (lookingGlassDescription != null || Time.unscaledTime < nextLookingGlassLookup)
                return;
            nextLookingGlassLookup = Time.unscaledTime + LookingGlassLookupInterval;
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(
                    "LookingGlass.ItemStatsNameSpace.ItemStats", false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null)
                return;

            // No LookingGlass DLL reference or hard dependency is required.
            lookingGlassDescription = type.GetMethod("GetItemDescription",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(ItemDef), typeof(int), typeof(CharacterMaster),
                    typeof(bool), typeof(bool) }, null);
            if (lookingGlassDescription != null)
                Logger.LogInfo("LookingGlass item descriptions are available.");
            else if (!loggedLookingGlassMismatch)
            {
                loggedLookingGlassMismatch = true;
                Logger.LogWarning("LookingGlass is loaded but its description API differs; " +
                    "using normal game descriptions.");
            }
        }

        private string GetDescription(ItemDef definition, int count, CharacterMaster master)
        {
            if (lookingGlassDescription != null && master != null)
            {
                try
                {
                    // withOneMore=true: describes taking this item, including
                    // the current stack count and LookingGlass calculations.
                    string enhanced = lookingGlassDescription.Invoke(null,
                        new object[] { definition, count, master, true, false }) as string;
                    if (!string.IsNullOrEmpty(enhanced))
                        return enhanced;
                }
                catch (Exception ex)
                {
                    if (!loggedLookingGlassException)
                    {
                        loggedLookingGlassException = true;
                        Logger.LogWarning("LookingGlass description failed, using vanilla text: " + ex);
                    }
                }
            }

            string description = Language.GetString(definition.descriptionToken);
            if (string.IsNullOrEmpty(description))
                description = Language.GetString(definition.pickupToken);
            return description;
        }

        // Run only after the game's native tooltip has followed the cursor.
        // The effect is restricted to the tagged lower-grid item buttons.
        public static void RepositionQueueTooltip(TooltipController __instance)
        {
            try
            {
                if (instance == null || __instance == null || TooltipOwnerField == null)
                    return;

                MPEventSystem owner = TooltipOwnerField.GetValue(__instance) as MPEventSystem;
                if (owner == null || owner.currentTooltip != __instance)
                    return;
                TooltipProvider provider = owner.currentTooltipProvider;
                if (provider == null || provider.GetComponent<QueueChoiceTag>() == null)
                    return;

                RectTransform flip = __instance.tooltipFlipTransform;
                RectTransform center = __instance.tooltipCenterTransform;
                if (flip == null || center == null)
                    return;

                QueueTooltipLayoutState state = __instance.GetComponent<QueueTooltipLayoutState>();
                if (instance.tooltipPosition.Value == TooltipPosition.Cursor)
                {
                    // Restore the original corner handling if configuration is
                    // changed while this tooltip is already open.
                    if (state != null && state.Saved)
                    {
                        flip.anchorMin = state.OriginalMin;
                        flip.anchorMax = state.OriginalMax;
                        flip.pivot = state.OriginalPivot;
                        flip.anchoredPosition = state.OriginalPosition;
                        state.Saved = false;
                    }
                    return;
                }

                if (state == null)
                    state = __instance.gameObject.AddComponent<QueueTooltipLayoutState>();
                if (!state.Saved)
                {
                    state.OriginalMin = flip.anchorMin;
                    state.OriginalMax = flip.anchorMax;
                    state.OriginalPivot = flip.pivot;
                    state.OriginalPosition = flip.anchoredPosition;
                    state.Saved = true;
                }

                Vector2 bottomCenter = new Vector2(0.5f, 0f);
                flip.anchorMin = bottomCenter;
                flip.anchorMax = bottomCenter;
                flip.pivot = bottomCenter;
                flip.anchoredPosition = Vector2.zero;
                center.position = new Vector3(Screen.width * 0.5f,
                    Mathf.Max(16f, Screen.height * 0.02f), 0f);
        
            }
            catch (Exception ex)
            {
                // UI positioning is cosmetic; never break the game's tooltips.
                if (tooltipExceptionCount++ < 3)
                    instance?.Logger.LogError("Command Queue tooltip positioning failed: " + ex);
            }
        }

        private void OnDestroy()
        {
            if (tooltipPosition != null)
                tooltipPosition.SettingChanged -= OnTooltipPositionChanged;
            if (ignoreEnableInspect != null)
                ignoreEnableInspect.SettingChanged -= OnIgnoreInspectChanged;
            harmony?.UnpatchSelf();
            instance = null;
            vanillaItemIconPrefab = null;
            trackedChoices.Clear();
        }
    }

    // Attached only to CommandQueueExtended's available-item buttons.
    public sealed class QueueChoiceTag : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public void OnPointerEnter(PointerEventData eventData)
        {
            CommandQueueIconFixPlugin.HandleChoicePointerEnter(this, eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            CommandQueueIconFixPlugin.HandleChoicePointerExit(this, eventData);
        }

        public ItemIndex ItemIndex;
        public ItemIcon Icon;
        public TooltipProvider Tooltip;
        public TextMeshProUGUI Counter;
        public HUD Hud;
        public int LastCount = int.MinValue;
        public CharacterMaster LastMaster;
        public bool UsingLookingGlass;
        public bool Hovered;
        public MPEventSystem LastEventSystem;
        public Vector2 PointerPosition;
        public int RecoveryAttempts;
        public float NextRecoveryTime;
    }

    // Stores the vanilla tooltip's pivot for live config switching.
    public sealed class QueueTooltipLayoutState : MonoBehaviour
    {
        public bool Saved;
        public Vector2 OriginalMin;
        public Vector2 OriginalMax;
        public Vector2 OriginalPivot;
        public Vector2 OriginalPosition;
    }
}
