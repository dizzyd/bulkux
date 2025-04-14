using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace bulkux;

[HarmonyPatch]
public class bulkuxModSystem : ModSystem
{
    Harmony harmony;
    private static ICoreAPI api;

    private static WorldInteraction[] _newCrateInteractions = new WorldInteraction[]
    {
        new WorldInteraction()
        {
            ActionLangCode = "bulkux:blockhelp-bulkux-get",
            MouseButton = EnumMouseButton.Right,
            HotKeyCodes = null
        },
        new WorldInteraction()
        {
            ActionLangCode = "bulkux:blockhelp-bulkux-put",
            MouseButton = EnumMouseButton.Right,
            HotKeyCode = "shift"
        }
    };
    
    private static List<WorldInteraction> _crateInteractionsBuffer = new List<WorldInteraction>();
    
    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        
        // Save the API pointer
        bulkuxModSystem.api = api;
        
        // If no patches have been applied, apply them. This check is necessary as the
        // code can get run twice in the same process space when running single player
        if (!Harmony.HasAnyPatches(Mod.Info.ModID))
        {
            harmony = new Harmony(Mod.Info.ModID);
            harmony.PatchAll();
        }
    }

    private static HashSet<ItemSlot> MoveAllMatching(IWorldAccessor world, ItemStack item, IInventory source, IInventory dest, int maxItems = -1)
    {
        HashSet<ItemSlot> dirtySlots = new HashSet<ItemSlot>();
        List<ItemSlot> skipSlots = new List<ItemSlot>();
        foreach (var sourceSlot in source)
        {
            // Skip the source slot item if it's not the right type
            if (!item.Equals(world, sourceSlot.Itemstack, GlobalConstants.IgnoredStackAttributes)) continue;
            
            // Loop over dest slots, trying to insert
            while (sourceSlot.StackSize > 0 && skipSlots.Count < dest.Count)
            {
                // Find the best slot for our current value; if no slots are available,
                // the dest is full and we can bail
                var wslot = dest.GetBestSuitedSlot(sourceSlot, null, skipSlots);
                if (wslot.slot == null)
                {
                    world.Api.Logger.Audit("no suitable slot found in destination");
                    return dirtySlots;
                }

                var moved = sourceSlot.TryPutInto(world, wslot.slot, sourceSlot.StackSize);
                if (moved > 0)
                {
                    dirtySlots.Add(wslot.slot);
                    dirtySlots.Add(sourceSlot);
                }
                
                skipSlots.Add(wslot.slot);
            }
        }

        return dirtySlots;
    }
    
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockEntityCrate), nameof(BlockEntityCrate.OnBlockInteractStart))]
    public static bool OnBlockInteractStart(IPlayer byPlayer, BlockSelection blockSel,
        BlockEntityCrate __instance,
        InventoryGeneric ___inventory,
        ref int ___labelColor,
        ref ItemStack ___labelStack,
        ref MeshData ___labelMesh,
        ref bool __result)
    {
        var world = __instance.Api.World;
        
        // Setup traverse instance for accessing a few private methods
        Traverse traverse = Traverse.Create(__instance);
        Traverse didMoveItemsMethod = traverse.Method("didMoveItems", new Type[] { typeof(ItemStack), typeof(IPlayer) });
        Traverse freeAtlasSpaceMethod = traverse.Method("FreeAtlasSpace");
        
        // All operations are bulk now - put will try to put all matching items in your inventory in the crate,
        // while take will attempt to get a full stack every time.
        bool put = byPlayer.Entity.Controls.ShiftKey;
        bool take = !put;
        
        ItemSlot ownSlot = ___inventory.FirstNonEmptySlot;
        var hotbarslot = byPlayer.InventoryManager.ActiveHotbarSlot;

        bool drawIconLabel = put && hotbarslot?.Itemstack?.ItemAttributes?["pigment"]?["color"].Exists == true &&
                             blockSel.SelectionBoxIndex == 1;

        if (drawIconLabel)
        {
            if (!___inventory.Empty)
            {
                JsonObject jobj = hotbarslot.Itemstack.ItemAttributes["pigment"]["color"];
                int r = jobj["red"].AsInt();
                int g = jobj["green"].AsInt();
                int b = jobj["blue"].AsInt();

                // Remove previous label from atlas
                freeAtlasSpaceMethod.GetValue();

                ___labelColor = ColorUtil.ToRgba(255, (int)GameMath.Clamp(r * 1.2f, 0, 255),
                    (int)GameMath.Clamp(g * 1.2f, 0, 255), (int)GameMath.Clamp(b * 1.2f, 0, 255));
                ___labelStack = ___inventory.FirstNonEmptySlot.Itemstack.Clone();
                ___labelMesh = null;

                byPlayer.Entity.World.PlaySoundAt(new AssetLocation("sounds/player/chalkdraw"),
                    blockSel.Position.X + blockSel.HitPosition.X, blockSel.Position.InternalY + blockSel.HitPosition.Y,
                    blockSel.Position.Z + blockSel.HitPosition.Z, byPlayer, true, 8);

                __instance.MarkDirty(true);
            }
            else
            {
                (api as ICoreClientAPI)?.TriggerIngameError(__instance, "empty",
                    Lang.Get("Can't draw item symbol on an empty crate. Put something inside the crate first"));
            }

            goto InteractDone;
        }

        if (take && ownSlot != null)
        {
            world.Logger.Audit("Attempting take");

            // Always try to take the whole slot out; this should always return either a full stack
            // or as much as is in the crate, since we're taking pains on insert to fill up every
            // slot as we go
            ItemStack stack = ownSlot.TakeOutWhole();
            var quantity = stack.StackSize;
            ownSlot.MarkDirty();

            // Try to give the player the stack
            if (!byPlayer.InventoryManager.TryGiveItemstack(stack, true))
            {
                // Failed to give it...drop it at player's position
                world.SpawnItemEntity(stack, byPlayer.Entity.Pos.AsBlockPos);
            }
            else
            {
                // Ok, we did it and need to play some animation and such
                didMoveItemsMethod.GetValue(stack, byPlayer);
            }

            world.Logger.Audit("{0} Took {1}x{2} from Crate at {3}.",
                byPlayer.PlayerName,
                quantity,
                stack?.Collectible.Code,
                __instance.Pos
            );

            if (___inventory.Empty)
            {
                freeAtlasSpaceMethod.GetValue();
                ___labelStack = null;
                ___labelMesh = null;
            }
        }

        if (put)
        {
            api.Logger.Audit("Attempting put");
            
            // Item type we'll attempt to store
            ItemStack sourceItem;

            // If this container doesn't have anything stored in it
            if (ownSlot == null)
            {
                // The crate is empty *and* our hand is empty, bail
                if (hotbarslot.Empty)
                {
                    (api as ICoreClientAPI)?.TriggerIngameError(__instance, "empty",
                        "This crate is empty and nothing is in your active hotbar slot");
                    goto InteractDone;
                }
                
                // Ok, we have something in our hand that should be stored; use it as a type 
                sourceItem = byPlayer.InventoryManager.ActiveHotbarSlot.Itemstack.Clone();
            }
            else
            {
                sourceItem = ownSlot.Itemstack.Clone();
            }
            
            // Search the hotbar for matching items
            var hotbar = byPlayer.InventoryManager.GetHotbarInventory();
            var dirtySlots = MoveAllMatching(world, sourceItem, hotbar, ___inventory);

            // Search backpacks for matching items
            var backpacks = byPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            dirtySlots.UnionWith(MoveAllMatching(world, sourceItem, backpacks, ___inventory));

            // Finally if _some_ items got moved, call didMoveItems (which is only animation and sound stuff)
            if (dirtySlots.Count > 0)
            {
                foreach (var s in dirtySlots)
                {
                    s.MarkDirty();
                }
                
                didMoveItemsMethod.GetValue(___inventory[0].Itemstack, byPlayer);
                world.Logger.Audit("{0} Put {1}x{2} into Crate at {3}.",
                    byPlayer.PlayerName,
                    dirtySlots.Count,
                    ___inventory[0].Itemstack?.Collectible.Code,
                    __instance.Pos
                );
            }
        }
        
        InteractDone:
            // Always mark our instance as dirty
            __instance.MarkDirty(true);

            // Return "true" for the OnBlockInteract result
            __result = true;
            
            // Return false to indicate harmony should NOT run the original method
            return false;
    }
    
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockCrate), nameof(BlockCrate.GetPlacedBlockInteractionHelp))]
    public static void GetPlacedBlockInteractionHelp(ref WorldInteraction[] __result)
    {
        // We want to filter out just the original world interactions for the crate; otherwise, 
        // mods like CarryOn won't be able to display their interaction help
        _crateInteractionsBuffer.Clear();
        foreach (var wi in __result)
        {
            if (wi.ActionLangCode == "blockhelp-crate-add" ||
                wi.ActionLangCode == "blockhelp-crate-addall" ||
                wi.ActionLangCode == "blockhelp-crate-remove" ||
                wi.ActionLangCode == "blockhelp-crate-removeall")
            {
                continue;
            }
            
            _crateInteractionsBuffer.Add(wi);
        }
        
        // Finally, add our new interaction items
        foreach (var wi in _newCrateInteractions)
        {
            _crateInteractionsBuffer.Add(wi);
        }

        __result = _crateInteractionsBuffer.ToArray();
    }
    
}

