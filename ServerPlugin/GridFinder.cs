using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Groups;
using VRageMath;

namespace ServerPlugin;

public static class GridFinder
{
    public static bool TryFind(
        string gridNameOrEntityId,
        MyCharacter character,
        bool includeConnectedGrids,
        out List<MyCubeGrid> grids
    )
    {
        grids = includeConnectedGrids
            ? FindPhysical(gridNameOrEntityId, character)
            : FindMechanical(gridNameOrEntityId, character);

        return grids.Count > 0;
    }

    public static MyCubeGrid BiggestGrid(IEnumerable<MyCubeGrid> grids)
    {
        return grids.Aggregate(
            (left, right) => left.BlocksCount >= right.BlocksCount ? left : right
        );
    }

    private static List<MyCubeGrid> FindPhysical(string gridNameOrEntityId, MyCharacter character)
    {
        var group = string.IsNullOrWhiteSpace(gridNameOrEntityId)
            ? FindLookAtPhysicalGroup(character)
            : MyCubeGridGroups.Static.Physical.Groups.FirstOrDefault(g =>
                GroupMatches(g, gridNameOrEntityId)
            );

        return Extract(group);
    }

    private static List<MyCubeGrid> FindMechanical(string gridNameOrEntityId, MyCharacter character)
    {
        var group = string.IsNullOrWhiteSpace(gridNameOrEntityId)
            ? FindLookAtMechanicalGroup(character)
            : MyCubeGridGroups.Static.Mechanical.Groups.FirstOrDefault(g =>
                GroupMatches(g, gridNameOrEntityId)
            );

        return Extract(group);
    }

    private static bool GroupMatches<T>(
        MyGroups<MyCubeGrid, T>.Group group,
        string gridNameOrEntityId
    )
        where T : IGroupData<MyCubeGrid>, new()
    {
        foreach (var node in group.Nodes)
        {
            var grid = node.NodeData;
            if (
                IsValid(grid)
                && (
                    grid.DisplayName.Equals(gridNameOrEntityId, StringComparison.OrdinalIgnoreCase)
                    || grid.EntityId.ToString() == gridNameOrEntityId
                )
            )
                return true;
        }

        return false;
    }

    private static MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group FindLookAtPhysicalGroup(
        MyCharacter character
    )
    {
        if (character == null)
            return null;

        return PickLookAtGroup(MyCubeGridGroups.Static.Physical.Groups, character);
    }

    private static MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Group FindLookAtMechanicalGroup(
        MyCharacter character
    )
    {
        if (character == null)
            return null;

        return PickLookAtGroup(MyCubeGridGroups.Static.Mechanical.Groups, character);
    }

    private static MyGroups<MyCubeGrid, T>.Group PickLookAtGroup<T>(
        IEnumerable<MyGroups<MyCubeGrid, T>.Group> groups,
        MyCharacter character
    )
        where T : IGroupData<MyCubeGrid>, new()
    {
        const double range = 5000;
        var matrix = character.GetHeadMatrix(true);
        var start = matrix.Translation + matrix.Forward * 0.5;
        var end = matrix.Translation + matrix.Forward * range;
        var ray = new RayD(start, matrix.Forward);

        MyGroups<MyCubeGrid, T>.Group closest = null;
        var closestDistance = double.MaxValue;

        foreach (var group in groups)
        {
            foreach (var node in group.Nodes)
            {
                var grid = node.NodeData;
                if (!IsValid(grid) || !ray.Intersects(grid.PositionComp.WorldAABB).HasValue)
                    continue;

                var hit = grid.RayCastBlocks(start, end);
                if (!hit.HasValue)
                    continue;

                var distance = (start - grid.GridIntegerToWorld(hit.Value)).Length();
                if (distance >= closestDistance)
                    continue;

                closestDistance = distance;
                closest = group;
            }
        }

        return closest;
    }

    private static List<MyCubeGrid> Extract<T>(MyGroups<MyCubeGrid, T>.Group group)
        where T : IGroupData<MyCubeGrid>, new()
    {
        return group?.Nodes.Select(node => node.NodeData).Where(IsValid).ToList()
            ?? new List<MyCubeGrid>();
    }

    private static bool IsValid(MyCubeGrid grid)
    {
        return grid != null
            && grid.Physics != null
            && grid.InScene
            && !grid.IsPreview
            && !grid.MarkedForClose
            && !grid.MarkedAsTrash;
    }
}
