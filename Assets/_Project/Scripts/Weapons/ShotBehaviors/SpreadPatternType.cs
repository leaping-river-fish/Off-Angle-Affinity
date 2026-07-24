namespace OffAngle.Weapons
{
    public enum SpreadPatternType
    {
        /// <summary>Each pellet gets a random offset within HorizontalSpread/VerticalSpread. Different every shot.</summary>
        RandomCone = 0,

        /// <summary>Each pellet uses a fixed offset from FixedPattern (wrapping if PelletCount exceeds the array length). Identical every shot - arena-shooter style.</summary>
        FixedPattern
    }
}
