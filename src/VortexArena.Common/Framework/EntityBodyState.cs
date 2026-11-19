using VortexArena.Common.Gameplay;

namespace VortexArena.Common.Framework;

public partial class Entity
{
    internal int BodyCloneOrdinal;

    /// <summary>Model species carried by players and their copied bodies for spawned effects.</summary>
    public ModelSpecies Species;

    /// <summary>Lower body animation selected at clone creation.</summary>
    public int BodyCloneLocomotion;
}
