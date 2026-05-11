using Robust.Shared.Serialization;

namespace Content.Shared._Pirate.ZLevels.Monitoring;

[Serializable, NetSerializable]
public sealed class CEZMonitoringConsoleLevelSelectedMessage : BoundUserInterfaceMessage
{
    public NetEntity? Grid;
    public int Depth;

    public CEZMonitoringConsoleLevelSelectedMessage(NetEntity? grid, int depth)
    {
        Grid = grid;
        Depth = depth;
    }
}
