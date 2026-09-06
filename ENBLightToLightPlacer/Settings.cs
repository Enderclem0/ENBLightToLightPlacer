namespace ENBLightToLightPlacer;

public class ModelOverride
{
    /// <summary>Substring of the model path this rule applies to. First match wins.</summary>
    public string PathContains = string.Empty;

    /// <summary>Radius to force, or 0 to keep the estimate.</summary>
    public float Radius = 0f;

    /// <summary>Fade to force, or 0 to keep the value read from the mesh.</summary>
    public float Fade = 0f;
}

public class ModelAlias
{
    /// <summary>Model path to emit an entry for.</summary>
    public string Target = string.Empty;

    /// <summary>Model path whose marker supplies the light.</summary>
    public string Source = string.Empty;
}

public class Settings
{
    /// <summary>
    /// LIGH record every generated light borrows. Light Placer overrides colour,
    /// radius and fade per entry, so one template covers almost everything --
    /// 406 of CS Light's 432 entries use this same one.
    /// </summary>
    public string LightTemplate = "MagicLightWhite01";

    public string LightFlags = "PortalStrict|Linear";

    /// <summary>
    /// Resolved against the Data folder, NOT the patch plugin's folder. Synthesis
    /// copies only the .esp out of its staging workspace, so a JSON written beside
    /// the plugin is silently stranded in a temp directory. Under MO2 the Data
    /// folder is virtual, so this lands in Overwrite -- move it into a mod of its
    /// own, or set an absolute path here and it is used verbatim.
    /// </summary>
    public string OutputJsonPath = @"LightPlacer\ENBLightPort\ENB Light Port.json";

    /// <summary>
    /// Radius is the one value a marker does not carry -- it was the mod
    /// author's judgement. These fit the radii CS Light's author chose for
    /// meshes whose markers we can also read: half 80 -> ~6, 102 -> ~22,
    /// 120 -> ~30, 160 -> ~68. Spread within a bucket is wide, so treat the
    /// result as a starting point rather than a derived truth.
    /// </summary>
    public float RadiusBase = 6f;
    public float RadiusReferenceHalfSize = 80f;
    public float RadiusExponent = 3.7f;

    /// <summary>
    /// Ceiling on the estimated radius. The power law was fitted over marker
    /// half-sizes of 80 to 160; past that it extrapolates steeply, and one
    /// oversized candle quad in Soljund's Sinkhole came out at 178 units.
    /// </summary>
    public float MaxEstimatedRadius = 120f;

    /// <summary>
    /// Floor on the estimated radius. The power law was fitted from half-size
    /// 80 upward; below that it collapses toward zero, and a sub-unit radius is
    /// a light slot spent on something nobody can see. Set 0 to extrapolate.
    /// </summary>
    public float MinEstimatedRadius = 6f;

    /// <summary>
    /// Model paths containing any of these are ignored. PGPatcher keeps working
    /// copies under _pgpatcher_dups\ that no record actually loads.
    /// </summary>
    public List<string> ExcludePathsContaining = [@"_pgpatcher_dups\"];

    /// <summary>
    /// Emit an entry for a mesh that has no marker of its own, using the light
    /// from one that does. Vanilla's filled petty and lesser soul gems use
    /// dedicated *_full meshes that the ENB mods never patched, so without this
    /// the empty gem glows and the filled one stays dark.
    /// </summary>
    public List<ModelAlias> Aliases =
    [
        new() { Target = @"clutter\soulgem\soulgempetty_full.nif",
                Source = @"clutter\soulgem\soulgempetty01.nif" },
        new() { Target = @"clutter\soulgem\soulgemlesser_full.nif",
                Source = @"clutter\soulgem\soulgemlesser01.nif" },
    ];

    /// <summary>
    /// Skip meshes under actors\. Their markers are eye glows, which would put
    /// a light on every draugr and dragon priest in the game.
    /// </summary>
    public bool SkipActorMeshes = true;

    /// <summary>
    /// Per-model overrides, applied after the estimate. Nothing in a mesh
    /// distinguishes a grand soul gem's light from a petty one -- every marker
    /// is the same quad at the same emissive multiple -- so any ramp between
    /// tiers is imposed here, not derived.
    /// </summary>
    public List<ModelOverride> Overrides =
    [
        new() { PathContains = @"soulgem\soulgempetty",   Radius = 12, Fade = 1.2f },
        new() { PathContains = @"soulgem\soulgemlesser",  Radius = 16, Fade = 1.4f },
        new() { PathContains = @"soulgem\soulgemcommon",  Radius = 20, Fade = 1.6f },
        new() { PathContains = @"soulgem\soulgemgreater", Radius = 24, Fade = 1.8f },
        new() { PathContains = @"soulgem\soulgemgrand",   Radius = 30, Fade = 2.2f },
        new() { PathContains = @"soulgem\soulgemblack",   Radius = 30, Fade = 2.2f },
    ];
}
