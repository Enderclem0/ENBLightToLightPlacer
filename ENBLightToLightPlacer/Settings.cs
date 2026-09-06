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

public class Settings
{
    /// <summary>
    /// LIGH record every generated light borrows. Light Placer overrides colour,
    /// radius and fade per entry, so one template covers almost everything --
    /// 406 of CS Light's 432 entries use this same one.
    /// </summary>
    public string LightTemplate = "MagicLightWhite01";

    public string LightFlags = "PortalStrict|Linear";

    /// <summary>Written under the patcher's output folder, so MO2 deploys it to Data.</summary>
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
