using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CommandQueueIconFix
{
    // For CommandQueueExtended 2.0.0 (com.kuberoot.commandqueuewithsave).
    // The queue contents already use RoR2.UI.ItemIcon. The grid used to ADD
    // items to the queue still assigns PickupDef.iconSprite to a plain Image.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.kuberoot.commandqueuewithsave", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class CommandQueueIconFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.commandqueue.iconfix";
        public const string PluginName = "CommandQueueExtended Icon Fix";
        public const string PluginVersion = "1.0.0";

        private const string OverlayName = "CommandQueueIconFix_ItemIcon";
        private const BindingFlags DeclaredMethods = BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        private static CommandQueueIconFixPlugin instance;
        private static GameObject vanillaItemIconPrefab;
        private static bool failedToFindPrefab;
        private static int overlaysCreated;
        private static int diagnosticCount;
        private static int exceptionCount;

        // Calls to the getter and setter are adjacent in the CommandQueue
        // button-creation callback. This is only used on Unity's main thread.
        [ThreadStatic]
        private static PickupDef currentPickup;

        private Harmony harmony;

        private void Awake()
        {
            instance = this;
            Type uiManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("CommandQueue.UIManager", false))
                .FirstOrDefault(type => type != null);

            if (uiManagerType == null)
            {
                Logger.LogError("Cannot find CommandQueue.UIManager. " +
                    "Check that CommandQueueExtended 2.0.0 is installed and enabled.");
                return;
            }

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

            Logger.LogInfo("v1.0.0 installed Harmony patches on " + callbacksFound +
                " CreateQueueContainer callback(s).");
            if (callbacksFound == 0)
                Logger.LogError("No matching callbacks found: this mod version may have changed.");
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

            if (created)
            {
                overlaysCreated++;
                bool inspect = overlaysCreated <= 8;
                if (inspect)
                    Logger.LogInfo("Attached vanilla ItemIcon for " + itemIndex +
                        " to " + legacyImage.gameObject.name);
                StartCoroutine(RebindAndInspect(icon, itemIndex, inspect));
            }
            return true;
        }

        private IEnumerator RebindAndInspect(ItemIcon icon, ItemIndex itemIndex, bool inspect)
        {
            // Rebind after Unity's Awake/OnEnable/Start of the cloned prefab.
            yield return null;
            if (icon == null)
                yield break;
            try
            {
                icon.SetItemIndex(itemIndex, 1);
            }
            catch (Exception ex)
            {
                Logger.LogError("ItemIcon.SetItemIndex failed for " + itemIndex + ": " + ex);
                yield break;
            }

            if (!inspect)
                yield break;

            // Diagnostic snapshot only; Addressables may still be loading.
            yield return new WaitForSecondsRealtime(0.5f);
            if (icon == null || diagnosticCount++ >= 8)
                yield break;
            Image[] images = icon.GetComponentsInChildren<Image>(true);
            RawImage[] textures = icon.GetComponentsInChildren<RawImage>(true);
            int validSprites = images.Count(image => image != null && image.sprite != null);
            int validTextures = textures.Count(image => image != null && image.texture != null);
            Logger.LogInfo("ItemIcon inspection " + itemIndex +
                ": Image components=" + images.Length +
                ", populated sprites=" + validSprites +
                ", RawImage textures=" + validTextures +
                ". This is diagnostic only; the game's icon pipeline may be asynchronous.");
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            instance = null;
            vanillaItemIconPrefab = null;
        }
    }
}
