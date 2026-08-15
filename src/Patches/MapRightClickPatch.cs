using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ModMenu.Patches
{
    /// <summary>
    /// Turns a right-click on the world map into a small chooser instead of jumping straight
    /// into the waypoint dialog, and adds "Teleport here" to it.
    ///
    /// These patches target the world map GUI, which lives in VSEssentials rather than the
    /// core API, so they are applied by hand inside a try/catch instead of through PatchAll.
    /// If that mod ever moves or renames these methods only the map feature is lost, and the
    /// rest of the menu keeps working.
    /// </summary>
    public static class MapRightClickPatch
    {
        private static ICoreClientAPI capi;
        private static MapContextMenu menu;
        private static Action<double, double, double> teleport;

        public static void Apply(Harmony harmony, ICoreClientAPI api, MapContextMenu contextMenu,
            Action<double, double, double> teleportTo, Vintagestory.API.Common.ILogger logger)
        {
            capi = api;
            menu = contextMenu;
            teleport = teleportTo;

            try
            {
                harmony.Patch(
                    AccessTools.Method(typeof(GuiDialogWorldMap), "OnMouseUp"),
                    prefix: new HarmonyMethod(typeof(MapRightClickPatch), nameof(WorldMapPrefix)));

                harmony.Patch(
                    AccessTools.Method(typeof(WaypointMapComponent), "OnMouseUpOnElement"),
                    prefix: new HarmonyMethod(typeof(MapRightClickPatch), nameof(WaypointPrefix)));
            }
            catch (Exception e)
            {
                logger.Warning("Could not hook the world map right-click menu, map teleporting "
                             + "will be unavailable: {0}", e.Message);
            }
        }

        // ---- empty map right-click -------------------------------------------------

        public static bool WorldMapPrefix(GuiDialogWorldMap __instance, MouseEvent args, EnumDialogType ___dialogType)
        {
            if (capi == null || menu == null) return true;
            if (args.Button != EnumMouseButton.Right || args.Handled) return true;
            if (__instance.SingleComposer == null) return true;
            if (!__instance.SingleComposer.Bounds.PointInside(args.X, args.Y)) return true;

            var mapElem = __instance.SingleComposer.GetElement("mapElem") as GuiElementMap;
            if (mapElem == null) return true;

            // Let the layers hit-test first, exactly as the original does. If the cursor is
            // over a waypoint, WaypointPrefix below fires from inside this loop and marks the
            // event handled, so we must not also show the "add" menu.
            foreach (MapLayer layer in mapElem.mapLayers)
            {
                layer.OnMouseUpClient(args, mapElem);
                if (args.Handled) return false;
            }

            Vec3d worldPos = LoadWorldPos(__instance, mapElem, ___dialogType, args.X, args.Y);

            menu.Show(args.X, args.Y, new List<(string, Action)>
            {
                ("Add waypoint", () => OpenAddWaypoint(__instance, worldPos)),
                ("Teleport here", () => teleport(worldPos.X, worldPos.Y, worldPos.Z))
            });

            args.Handled = true;
            return false;
        }

        /// <summary>
        /// Mirrors GuiDialogWorldMap.loadWorldPos, which is private. The Y it produces comes
        /// from the rain map, i.e. the terrain surface at that spot, which is exactly the
        /// height worth teleporting to.
        /// </summary>
        private static Vec3d LoadWorldPos(GuiDialogWorldMap dlg, GuiElementMap mapElem,
            EnumDialogType dialogType, double mouseX, double mouseY)
        {
            double viewX = mouseX - dlg.SingleComposer.Bounds.absX;
            double viewY = mouseY - dlg.SingleComposer.Bounds.absY
                         - (dialogType == EnumDialogType.Dialog ? GuiElement.scaled(30.0) : 0.0);

            var worldPos = new Vec3d();
            mapElem.TranslateViewPosToWorldPos(new Vec2f((float)viewX, (float)viewY), ref worldPos);
            worldPos.Y++;
            return worldPos;
        }

        private static void OpenAddWaypoint(GuiDialogWorldMap dlg, Vec3d worldPos)
        {
            var layer = dlg.MapLayers.FirstOrDefault(l => l is WaypointMapLayer) as WaypointMapLayer;
            var addDlg = new GuiDialogAddWayPoint(capi, layer) { WorldPos = worldPos };
            addDlg.TryOpen();
            addDlg.OnClosed += () => capi.Gui.RequestFocus(dlg);
        }

        // ---- right-click on an existing waypoint -----------------------------------

        public static bool WaypointPrefix(MouseEvent args, GuiElementMap mapElem,
            Waypoint ___waypoint, int ___waypointIndex, WaypointMapLayer ___wpLayer)
        {
            if (capi == null || menu == null) return true;
            if (args.Button != EnumMouseButton.Right) return true;
            if (!IsCursorOnWaypoint(___waypoint, mapElem, args)) return true;

            // Copy out of the injected fields so the closures do not capture by reference.
            Waypoint wp = ___waypoint;
            int index = ___waypointIndex;
            WaypointMapLayer layer = ___wpLayer;

            menu.Show(args.X, args.Y, new List<(string, Action)>
            {
                ("Modify waypoint", () => OpenEditWaypoint(layer, wp, index)),
                ("Teleport here", () => teleport(wp.Position.X, wp.Position.Y, wp.Position.Z))
            });

            args.Handled = true;
            return false;
        }

        /// <summary>
        /// Same hit test the game uses in WaypointMapComponent.OnMouseUpOnElement, so the
        /// menu appears for exactly the clicks that would have opened the edit dialog.
        /// </summary>
        private static bool IsCursorOnWaypoint(Waypoint wp, GuiElementMap mapElem, MouseEvent args)
        {
            if (wp?.Position == null || mapElem == null) return false;

            var view = new Vec2f();
            mapElem.TranslateWorldPosToViewPos(wp.Position, ref view);
            double px = view.X + mapElem.Bounds.renderX;
            double py = view.Y + mapElem.Bounds.renderY;

            if (wp.Pinned)
            {
                mapElem.ClampButPreserveAngle(ref view, 2);
                px = GameMath.Clamp(view.X + mapElem.Bounds.renderX,
                    mapElem.Bounds.renderX + 2.0,
                    mapElem.Bounds.renderX + mapElem.Bounds.InnerWidth - 2.0);
                py = GameMath.Clamp(view.Y + mapElem.Bounds.renderY,
                    mapElem.Bounds.renderY + 2.0,
                    mapElem.Bounds.renderY + mapElem.Bounds.InnerHeight - 2.0);
            }

            float radius = RuntimeEnv.GUIScale * 8f;
            return Math.Abs(args.X - px) < radius && Math.Abs(args.Y - py) < radius;
        }

        private static void OpenEditWaypoint(WaypointMapLayer layer, Waypoint wp, int index)
        {
            GuiDialogWorldMap mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>().worldMapDlg;
            var editDlg = new GuiDialogEditWayPoint(capi, layer, wp, index);
            editDlg.TryOpen();
            editDlg.OnClosed += () => capi.Gui.RequestFocus(mapDlg);
        }
    }
}
