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
    /// Transcribe a mesh's emissive keyframes into Light Placer's fadeController
    /// so the light pulses the way the glow does, instead of sitting at a
    /// constant. Light Placer's format is a direct match for NiFloatData.
    /// </summary>
    public bool EmitFadeControllers = true;

    /// <summary>
    /// Fade to use when a marker's emissive multiple is 0 but a controller
    /// animates it -- the Dwemer control cubes, whose glow is driven by an
    /// animation sequence. The curve is not readable from the mesh (the
    /// interpolator holds an "unset" sentinel and the keys live in a
    /// NiControllerSequence chosen at runtime), so this is a stand-in for a
    /// value we cannot measure. Set 0 to skip those markers instead.
    /// </summary>
    public float AnimatedEmissiveFade = 1.0f;

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
    /// Read meshes under actors\ too. Their markers are eye glows and wisp
    /// bodies, so this lights every draugr, dragon priest and witchlight --
    /// two lights on the eye meshes, one per eye.
    ///
    /// Deliberately renamed from SkipActorMeshes rather than flipped in place.
    /// Synthesis persists settings per patcher, and a saved value wins over a
    /// changed default forever: the old file kept SkipActorMeshes=true from the
    /// first run, so flipping the default silently did nothing. A new key has
    /// no saved entry, so the default here is what actually applies.
    /// </summary>
    public bool IncludeActorMeshes = true;

    /// <summary>
    /// Skip models that another LightPlacer JSON already covers, so an object
    /// does not get two lights stacked on it. Everything under
    /// [Data]\LightPlacer\**\*.json is read except this patcher's own output --
    /// excluding that would make every run after the first emit nothing.
    /// </summary>
    public bool SkipModelsCoveredElsewhere = true;

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
