using System.Buffers.Binary;

namespace ENBLightToLightPlacer;

/// <summary>
/// The second ENB Light convention: a dedicated NiParticleSystem that renders
/// nothing but emits light.
///
/// Detection follows the rules the ENB Light author documented for making ENB
/// treat a particle as a light, rather than matching node names -- names like
/// "pENBLight" are just this mod's habit, whereas these rules are what the
/// renderer actually keys on:
///
///   * NiParticleSystem (BSStripParticleSystem never emits light)
///   * NiAlphaProperty flags == 4109, i.e. additive blending -- a hard
///     requirement, stated first in the guide
///   * emissive multiple > 0 and emissive colour > 0
///   * a BSPSysSimpleColorModifier with non-zero colours
///   * NiPSysData carries no subtextures. This is what separates a
///     purpose-built light particle from a vanilla fire particle: fire is
///     animated by cycling subtexture frames, and ENB routes those through its
///     separate Complex Fire Lights path, which ENB Light explicitly turns off
///     because "fire lights rely on the vanilla fire particles so the results
///     are inconsistent".
///
///     Measured rather than assumed: at NiPSysData offset 46 the dark elf
///     lantern reads 0 with a subtexture count of 0, while campfire01burning
///     reads 1 with a count of 8.
///
/// Values follow the same documentation:
///
///   colour = emissive colour x peak modifier colour   (the texture is blank,
///            so unlike the quad convention it contributes nothing)
///   fade   = emissive multiple x peak modifier alpha
///            x particles alive at once x fade duty cycle
///
/// One particle's alpha badly understates the result, because ENB is lighting
/// from a cloud of overlapping particles. The guide gives the factor outright:
/// "Particle birth rate * particle lifespan = number of particles alive on the
/// screen at the same time" -- 12/s x 0.42s = 5.0 for the torch, 2/s x 3.0s =
/// 6.0 for the lantern. It also notes brightness "depends on the FadeIn/Out
/// values, meaning that the more fade there is, the shorter the particle stays
/// at its full brightness", which is the duty cycle.
///   radius = k x min(InitialRadius x max scale, saturation)
///
/// The saturation is the important part: "Values above about 100 or so for
/// Initial Size will make a bigger particle but will not increase light
/// coverage." Every fire mesh measured 128 and so clamps to the same coverage,
/// which is why the authored radii for them scatter from 71 to 512 with no
/// relation to the mesh -- those were eyeballed, not derived.
/// </summary>
public static class ParticleLights
{
    private const ushort AdditiveBlending = 4109;

    /// <summary>NiPSysModifier: name, order, then a target back-reference.</summary>
    private const int ModifierTargetOffset = 8;

    /// <summary>NiPSysModifier base is 13 bytes; NiPSysEmitter then has six floats and a Color4.</summary>
    private const int EmitterInitialRadiusOffset = 13 + 24 + 16;

    /// <summary>Same base, then fade-in/out and four colour percentages.</summary>
    private const int ColourModifierColoursOffset = 13 + 24;

    /// <summary>NiPSysEmitter: initial radius at 53, life span at 61.</summary>
    private const int EmitterLifeSpanOffset = 61;

    /// <summary>NiPSysEmitterCtlr: NiTimeController is 26 bytes, then the interpolator.</summary>
    private const int EmitterCtlrInterpolatorOffset = 26;

    /// <summary>NiPSysData: hasTextureIndices, then the subtexture count.</summary>
    private const int HasTextureIndicesOffset = 46;

    public static List<Marker> Find(Nif nif, float radiusPerUnit, float saturation,
                                    float fadeScale = 1f, Action<string>? log = null)
    {
        var transforms = Nif.WorldTransforms(nif);
        var found = new List<Marker>();
        var subtextured = SubtexturedSystems(nif);

        foreach (var (index, shaderRef, alphaRef) in ParticleSystems(nif))
        {
            if (!IsAdditive(nif, alphaRef)) { log?.Invoke($"[{index}] not additive"); continue; }
            if (shaderRef < 0 || shaderRef >= nif.BlockCount) { log?.Invoke($"[{index}] shader ref out of range"); continue; }
            if (nif.BlockType(shaderRef) != "BSEffectShaderProperty") { log?.Invoke($"[{index}] shader is {nif.BlockType(shaderRef)}"); continue; }

            var shader = nif.ReadEffectShader(shaderRef);
            if (shader.EmissiveMultiple <= 0) { log?.Invoke($"[{index}] emissive multiple 0"); continue; }
            if (shader.EmissiveColor.Take(3).Max() <= 0) { log?.Invoke($"[{index}] emissive colour 0"); continue; }

            // Subtextures mean this is animated fire, not a dedicated light.
            // Unknown counts as fire: a wrong light is worse than a missing one.
            if (!subtextured.TryGetValue(index, out bool isFire) || isFire)
            {
                log?.Invoke($"[{index}] " + (subtextured.ContainsKey(index)
                    ? "has subtextures, so it is fire"
                    : "could not pair with its NiPSysData"));
                continue;
            }

            var colour = ColourModifier(nif, index);
            if (colour is null) { log?.Invoke($"[{index}] no colour modifier"); continue; }
            var (peak, peakAlpha) = colour.Value;
            if (peak.Max() <= 0) continue;

            float accumulation = ParticlesAlive(nif, index);
            float duty = DutyCycle(nif, index);

            float size = EmitterSize(nif, index) * ScaleMultiplier(nif, index);
            if (size <= 0) { log?.Invoke($"[{index}] no emitter size"); continue; }
            float radius = radiusPerUnit * MathF.Min(size, saturation);

            float[] rgb = [shader.EmissiveColor[0] * peak[0],
                           shader.EmissiveColor[1] * peak[1],
                           shader.EmissiveColor[2] * peak[2]];
            float m = rgb.Max();
            if (m <= 0) continue;
            int[] tint = [(int)MathF.Round(rgb[0] / m * 255),
                          (int)MathF.Round(rgb[1] / m * 255),
                          (int)MathF.Round(rgb[2] / m * 255)];

            transforms.TryGetValue(index, out var placed);
            placed ??= new Nif.Placement([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, [0, 0, 0], []);

            found.Add(new Marker(
                NodeName: placed.Chain.Count > 1 ? placed.Chain[^2] : nif.SafeName(index),
                Texture: shader.SourceTexture.Split('\\').Last(),
                Color: tint,
                Fade: MathF.Round(shader.EmissiveMultiple * MathF.Max(peakAlpha, 0.05f)
                                  * accumulation * duty * fadeScale, 3),
                Point: [MathF.Round(placed.Translation[0], 2),
                        MathF.Round(placed.Translation[1], 2),
                        MathF.Round(placed.Translation[2], 2)],
                HalfSize: MathF.Round(size, 1),
                Note: string.Empty,
                EmissiveIsAnimated: false,
                FadeKeys: null,
                FadeInterpolation: "Linear",
                RadiusOverride: MathF.Round(radius, 1)));
        }
        return found;
    }

    /// <summary>
    /// Particle system block index -> does its data carry subtextures.
    ///
    /// Paired by order: a NIF writes blocks in tree order, so the k-th
    /// NiParticleSystem owns the k-th NiPSysData. Confirmed on both samples
    /// (lantern 36,57,78 -> 47,68,89). If the counts disagree the pairing is
    /// not trustworthy, so nothing is returned and every system is treated as
    /// fire.
    /// </summary>
    private static Dictionary<int, bool> SubtexturedSystems(Nif nif)
    {
        var systems = new List<int>();
        var data = new List<int>();
        for (int i = 0; i < nif.BlockCount; i++)
        {
            if (nif.BlockType(i) == "NiParticleSystem") systems.Add(i);
            else if (nif.BlockType(i) == "NiPSysData") data.Add(i);
        }
        var map = new Dictionary<int, bool>();
        if (systems.Count == 0 || systems.Count != data.Count) return map;
        for (int k = 0; k < systems.Count; k++)
        {
            var b = nif.Block(data[k]);
            bool fire = b.Length > HasTextureIndicesOffset && b[HasTextureIndicesOffset] != 0;
            map[systems[k]] = fire;
        }
        return map;
    }

    private static IEnumerable<(int Index, int ShaderRef, int AlphaRef)> ParticleSystems(Nif nif)
    {
        for (int i = 0; i < nif.BlockCount; i++)
        {
            // BSStripParticleSystem is excluded deliberately: it never emits.
            if (nif.BlockType(i) != "NiParticleSystem") continue;
            var (shader, alpha) = nif.ReadGeometryShaderRefs(i);
            yield return (i, shader, alpha);
        }
    }

    private static bool IsAdditive(Nif nif, int alphaRef)
    {
        if (alphaRef < 0 || alphaRef >= nif.BlockCount) return false;
        if (nif.BlockType(alphaRef) != "NiAlphaProperty") return false;
        var b = nif.Block(alphaRef);
        // NiObjectNET: name, numExtraData (0 here), controller -> flags at 12.
        if (b.Length < 14) return false;
        return BinaryPrimitives.ReadUInt16LittleEndian(b[12..]) == AdditiveBlending;
    }

    private static int FindModifier(Nif nif, int target, string blockType)
    {
        for (int j = 0; j < nif.BlockCount; j++)
        {
            if (nif.BlockType(j) != blockType) continue;
            var b = nif.Block(j);
            if (b.Length < ModifierTargetOffset + 4) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(b[ModifierTargetOffset..]) == target)
                return j;
        }
        return -1;
    }

    /// <summary>Peak colour of the particle's life, and its alpha.</summary>
    private static (float[] Peak, float Alpha)? ColourModifier(Nif nif, int psys)
    {
        int j = FindModifier(nif, psys, "BSPSysSimpleColorModifier");
        if (j < 0) return null;
        var b = nif.Block(j);
        if (b.Length < ColourModifierColoursOffset + 48) return null;

        // Three Color4 keys across the particle's life; the middle one is the
        // sustained colour, the outer two are the fade in and out.
        float[] best = [0, 0, 0];
        float bestAlpha = 0, bestMag = -1;
        for (int k = 0; k < 3; k++)
        {
            int off = ColourModifierColoursOffset + k * 16;
            float[] c = [BinaryPrimitives.ReadSingleLittleEndian(b[off..]),
                         BinaryPrimitives.ReadSingleLittleEndian(b[(off + 4)..]),
                         BinaryPrimitives.ReadSingleLittleEndian(b[(off + 8)..])];
            float a = BinaryPrimitives.ReadSingleLittleEndian(b[(off + 12)..]);
            float mag = c.Max() * a;
            if (mag > bestMag) { bestMag = mag; best = c; bestAlpha = a; }
        }
        return (best, bestAlpha);
    }

    private static float EmitterSize(Nif nif, int psys)
    {
        foreach (var type in new[] { "NiPSysSphereEmitter", "NiPSysCylinderEmitter",
                                     "NiPSysBoxEmitter", "NiPSysMeshEmitter" })
        {
            int j = FindModifier(nif, psys, type);
            if (j < 0) continue;
            var b = nif.Block(j);
            if (b.Length < EmitterInitialRadiusOffset + 4) continue;
            return BinaryPrimitives.ReadSingleLittleEndian(b[EmitterInitialRadiusOffset..]);
        }
        return 0;
    }


    /// <summary>
    /// Particles alive at once: birth rate x life span. The birth rate is the
    /// emitter controller's interpolator value; when that is animated the mean
    /// of its keys is the honest stand-in. Clamped, because a runaway count
    /// would swamp every other light in the scene.
    /// </summary>
    private static float ParticlesAlive(Nif nif, int psys)
    {
        float life = EmitterField(nif, psys, EmitterLifeSpanOffset);
        if (life <= 0) return 1f;

        int controller = nif.ControllerRef(psys);
        if (controller < 0 || controller >= nif.BlockCount) return 1f;
        if (!nif.BlockType(controller).EndsWith("EmitterCtlr", StringComparison.Ordinal)) return 1f;

        var cb = nif.Block(controller);
        if (cb.Length < EmitterCtlrInterpolatorOffset + 4) return 1f;
        int interp = BinaryPrimitives.ReadInt32LittleEndian(cb[EmitterCtlrInterpolatorOffset..]);
        float rate = nif.InterpolatorValue(interp);
        if (rate <= 0) return 1f;

        return Math.Clamp(rate * life, 1f, 30f);
    }

    /// <summary>
    /// How much of its life the particle spends at full brightness. Ramping in
    /// over fadeIn and out after fadeOut averages to half of each ramp.
    /// </summary>
    private static float DutyCycle(Nif nif, int psys)
    {
        int j = FindModifier(nif, psys, "BSPSysSimpleColorModifier");
        if (j < 0) return 1f;
        var b = nif.Block(j);
        if (b.Length < 21) return 1f;
        float fadeIn = BinaryPrimitives.ReadSingleLittleEndian(b[13..]);
        float fadeOut = BinaryPrimitives.ReadSingleLittleEndian(b[17..]);
        if (fadeIn < 0 || fadeOut > 1 || fadeOut < fadeIn) return 1f;
        return Math.Clamp(0.5f * (1f + fadeOut - fadeIn), 0.1f, 1f);
    }

    private static float EmitterField(Nif nif, int psys, int offset)
    {
        foreach (var type in new[] { "NiPSysSphereEmitter", "NiPSysCylinderEmitter",
                                     "NiPSysBoxEmitter", "NiPSysMeshEmitter" })
        {
            int j = FindModifier(nif, psys, type);
            if (j < 0) continue;
            var b = nif.Block(j);
            if (b.Length < offset + 4) continue;
            return BinaryPrimitives.ReadSingleLittleEndian(b[offset..]);
        }
        return 0;
    }

    private static float ScaleMultiplier(Nif nif, int psys)
    {
        int j = FindModifier(nif, psys, "BSPSysScaleModifier");
        if (j < 0) return 1f;
        var b = nif.Block(j);
        if (b.Length < 17) return 1f;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b[13..]);
        float max = 0;
        for (int k = 0; k < count && 17 + k * 4 + 4 <= b.Length; k++)
            max = MathF.Max(max, BinaryPrimitives.ReadSingleLittleEndian(b[(17 + k * 4)..]));
        return max > 0 ? max : 1f;
    }
}
