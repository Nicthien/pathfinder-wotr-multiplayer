using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Assets.Code.UI._ConsoleUI.Overtips;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Globalmap.State;
using Kingmaker.PubSubSystem;
using Kingmaker.TurnBasedMode;
using Kingmaker.UI._ConsoleUI.Overtips;
using Kingmaker.UI.Common;
using Kingmaker.UI.Selection;
using Kingmaker.View.MapObjects;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.MapObjects;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.HarmonyPatches.MapObjects
{
    /// despite Kingmaker.UI._ConsoleUI.Overtips namespace name, it's still being used on PC
    [HarmonyPatch]
    public class OvertipsPatches
    {
        [HarmonyPatch(typeof(OvertipViewPartName), nameof(OvertipViewPartName.SetName))]
        [HarmonyPostfix]
        public static void OvertipViewPartName_SetName_Postfix(OvertipViewPartName __instance)
        {
            if (!Main.Multiplayer.IsActive)
            {
                return;
            }

            if (__instance.GetViewModel() is not EntityOvertipVM viewModel || viewModel.Unit == null)
            {
                return;
            }

            var text = string.IsNullOrEmpty(viewModel.CustomName.Value) ? viewModel.Name.Value : viewModel.CustomName.Value;

            var mpOwnerName = Main.Multiplayer.GetCharacterOwnerName(viewModel.Unit.UniqueId);
            if (!string.IsNullOrEmpty(mpOwnerName))
            {
                text += $" ({mpOwnerName})";
            }

            if (Main.ModManagerSettings.AddUnitIdToOvertip)
            {
                text += $" [{viewModel.Unit.UniqueId}]";
            }

            var formattedText = UIUtility.GetSaberBookFormat(text, __instance.m_SaberColor, 140, null, 0f);
            __instance.m_CharacterName.text = formattedText;
        }

        [HarmonyPatch(typeof(ObjectInteractionOvertipView), nameof(ObjectInteractionOvertipView.OnClick))]
        [HarmonyPrefix]
        public static void ObjectInteractionOvertipView_OnClick_Prefix(ObjectInteractionOvertipView __instance)
        {
            if (!Main.Multiplayer.IsActive)
            {
                return;
            }

            var networkOvertip = new NetworkOvertip
            {
                MapObject = Main.Mapper.Map<NetworkMapObject>(__instance.MapObjectView.Data),
                Units = [.. Game.Instance.SelectionCharacter.SelectedUnits.Select(x => x.UniqueId)]
            };

            if (Main.Multiplayer.IsInCombat)
            {
                var activeUnit = Game.Instance.TurnBasedCombatController.CurrentTurn?.SelectedUnit;
                if (activeUnit != null)
                {
                    var path = PathVisualizer.Instance?.CurrentPathForUnit(activeUnit.View);
                    networkOvertip.VectorPath = path?.vectorPath?.Select(x => x.ToNetworkVector3()).ToList() ?? [];
                }
            }

            Main.Multiplayer.OnInteractWithMapObjectOvertip(networkOvertip);
        }

        [HarmonyPatch(typeof(EntityOvertipVM), nameof(EntityOvertipVM.StartAreaTransition))]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> EntityOvertipVM_StartAreaTransition_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = PatchesUtils.GetTranspilerTarget(MethodBase.GetCurrentMethod());
            var replaceWith = AccessTools.Method(typeof(OvertipsPatches), nameof(OvertipsPatches.SelectAllCharactersControlledByLocalPlayer));
            var lookFor = AccessTools.Method(typeof(SelectionManagerBase), nameof(SelectionManagerBase.SelectAll));
            var matcher = new CodeMatcher(instructions);
            var match = matcher.SearchForward(x => x.Calls(lookFor));

            if (match.IsInvalid)
            {
                Main.GetLogger<OvertipsPatches>().LogError("Invalid transpiler position. Target={Target}, Position={Position}", target, match.Pos);
                return instructions;
            }

            match = match.Advance(-4).RemoveInstructions(5);
            var newInstructions = new List<CodeInstruction>()
            {
                new(OpCodes.Call, replaceWith),
            };
            match.Insert(newInstructions);

            Main.GetLogger<OvertipsPatches>().LogDebug("Transpiler has been applied. Target={Target}", target);
            return matcher.Instructions();
        }

        [HarmonyPatch(typeof(AreaTransitionOvertipView), nameof(AreaTransitionOvertipView.OnClick))]
        [HarmonyPrefix]
        public static bool AreaTransitionOvertipView_OnClick_Prefix(AreaTransitionOvertipView __instance)
        {
            if (!Main.Multiplayer.IsActive)
            {
                return true;
            }

            var transition = __instance.MapObjectView?.Data?.Get<AreaTransitionPart>();
            if (transition == null) return true;

            if (!Main.Multiplayer.CanInitiateAreaTransitions())
            {
                // A client click is a request to the authoritative host. Reuse the
                // map-object interaction message, but do not start an independent
                // local walk/transition that can strand only part of the party.
                if (Main.Multiplayer.RemoteContext?.Overtip == null)
                {
                    var units = Game.Instance.Player.PartyAndPets
                        .Where(u => Main.Multiplayer.IsControlledByLocalPlayer(u.UniqueId))
                        .Select(c => c.View).ToList();
                    Main.Multiplayer.OnInteractWithMapObjectOvertip(new NetworkOvertip
                    {
                        MapObject = Main.Mapper.Map<NetworkMapObject>(__instance.MapObjectView.Data),
                        Units = [.. units.Select(x => x.Data.UniqueId)]
                    });
                }
                return false;
            }

            StartAuthoritativePartyTransition(transition);
            return false;
        }

        private static void StartAuthoritativePartyTransition(AreaTransitionPart transition)
        {
            if (Kingmaker.UI.MapObjectOvertip.AreaTransitionController.CanNotMove(transition, false)) return;
            var restrictionUnit = Game.Instance.Player.PartyAndPets.FirstOrDefault(unit => unit.IsDirectlyControllable)
                ?? Game.Instance.Player.MainCharacter.Value;
            if (!transition.CheckRestrictions(restrictionUnit)) return;

            var action = transition.Blueprint?.Actions?.FirstOrDefault(entry =>
                entry.Condition == null || entry.Condition.Check(null));
            if (action != null)
            {
                Main.Multiplayer.OnAreaTransition(new NetworkAreaTransition
                {
                    AreaExitId = transition.View.UniqueId,
                    IsActionsTransition = true,
                    From = Main.Mapper.Map<NetworkArea>(Game.Instance.CurrentlyLoadedArea),
                    To = Main.Mapper.Map<NetworkArea>(transition.AreaEnterPoint)
                });
                action.Actions.Run();
                return;
            }

            var currentArea = Game.Instance.CurrentlyLoadedArea;
            var target = transition.AreaEnterPoint;
            if (Game.Instance.State.LoadedAreaState.Encounter == null && target.Area.IsGlobalMap)
            {
                var globalMap = Kingmaker.Blueprints.Root.BlueprintRoot.Instance.GlobalMap.GetGlobalMap(target);
                if (globalMap != null) Game.Instance.Player.GetGlobalMap(globalMap).Player.AreaReturnPoint =
                    transition.GetEnterPointToReturnTo();
            }
            EventBus.RaiseEvent<IPartyLeaveAreaHandler>(handler =>
                handler.HandlePartyLeaveArea(currentArea, target, transition));
            Game.Instance.LoadArea(target, transition.Settings.AutoSaveMode, null);
        }

        private static void SelectAllCharactersControlledByLocalPlayer()
        {
            if (!Main.Multiplayer.IsActive)
            {
                Game.Instance.UI.SelectionManager.SelectAll();
                return;
            }

            if (Main.Multiplayer.RemoteContext?.SelectedUnits != null)
            {
                return;
            }

            List<UnitEntityData> units = [.. Game.Instance.Player.PartyAndPets.Where(u => Main.Multiplayer.IsControlledByLocalPlayer(u.UniqueId))];
            var views = units.Select(c => c.View).ToList();
            (Game.Instance.UI.SelectionManager as SelectionManagerPC).MultiSelect(views, true);
        }
    }
}
