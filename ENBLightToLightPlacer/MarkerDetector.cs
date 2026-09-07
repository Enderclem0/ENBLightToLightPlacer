using System.Text.RegularExpressions;

namespace ENBLightToLightPlacer;

/// <summary>One ENB particle-light marker, read off a mesh.</summary>
public sealed record Marker(
    string NodeName,
    string Texture,
    int[] Color,
    float Fade,
    float[] Point,
    float HalfSize,
    string Note,
    bool EmissiveIsAnimated,
    List<Nif.FloatKey>? FadeKeys,
    string FadeInterpolation);

/// <summary>
/// Finds ENB particle-light marker quads and converts what they encode into
/// Light Placer terms.
///
/// The marker is a small planar billboard quad carrying a
/// BSEffectShaderProperty on black.dds or fxglowENB.dds. Node names are NOT a
/// reliable signal -- Rudy's meshes use "EnbParticleLight01" but Alchemy
/// Ingredients left inherited junk like "DLC2DarkElfLantern01:40:48" -- so
/// detection is on geometry plus texture, with the name only as a fallback.
///
/// ENB Light 0.98 itself uses a second convention (a p*ENBLight* particle
/// system, colour in BSPSysSimpleColorModifier) that this does not read.
/// </summary>
public static partial class MarkerDetector
{
    private static readonly HashSet<string> MarkerTextures =
        new(StringComparer.OrdinalIgnoreCase) { "black.dds", "fxglowenb.dds" };

    [GeneratedRegex(@"enb.{0,3}(particle)?light", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerName();

    /// <summary>
    /// These mods pair the light quad with a *visible* glow sprite, named for
    /// it: firefly.nif carries EnbParticleLight01 (the emitter, half-size 126)
    /// beside EnbParticleLightGlow01 (the sprite, half-size 17, on
    /// FXGlowSpotLinearAlpha.dds). The sprite is not a light.
    ///
    /// The pattern has to be the whole companion name, not a bare "glow".
    /// Matching "glow" alone rejected every real marker in the actor eye
    /// meshes, because fxdraugrmaleeyes.nif keeps its two perfectly good
    /// EnbParticleLight quads under a node called FXDraugrFemaleEyeGlow.
    /// </summary>
    [GeneratedRegex(@"enb.{0,3}(particle)?light.*glow", RegexOptions.IgnoreCase)]
    private static partial Regex GlowCompanionName();

    public static List<Marker> Find(Nif nif)
    {
        var transforms = WorldTransforms(nif);
        var found = new List<Marker>();

        foreach (var shape in nif.ReadShapes())
        {
            if (shape.ShaderRef < 0 || shape.ShaderRef >= nif.BlockCount) continue;
            if (nif.BlockType(shape.ShaderRef) != "BSEffectShaderProperty") continue;
            if (shape.Verts.Count == 0 || shape.Verts.Count > 25) continue;

            var (min, max) = Bounds(shape.Verts);
            float[] ext = [max[0] - min[0], max[1] - min[1], max[2] - min[2]];
            float longest = Math.Max(ext[0], Math.Max(ext[1], ext[2]));
            float shortest = Math.Min(ext[0], Math.Min(ext[1], ext[2]));
            if (shortest > 0.05f * longest + 1e-6f) continue;            // must be planar
            if (Math.Min(ext[0], ext[1]) < 0.8f * Math.Max(ext[0], ext[1])) continue;  // and square

            var shader = nif.ReadEffectShader(shape.ShaderRef);
            transforms.TryGetValue(shape.Index, out var placed);
            placed ??= new Placement([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, [0, 0, 0], []);

            string basename = shader.SourceTexture.Split('\\').Last();
            string names = string.Join(' ', placed.Chain.TakeLast(2).Append(shape.Name));
            if (!MarkerTextures.Contains(basename) && !MarkerName().IsMatch(names)) continue;
            // A quad on the marker texture is a light whatever it is called;
            // only the name-matched ones can be sprites.
            if (!MarkerTextures.Contains(basename) && GlowCompanionName().IsMatch(names)) continue;

            byte[] vertex = [255, 255, 255, 255];
            if (shape.Colors.Count > 0)
            {
                var opaque = shape.Colors.Where(c => c[3] >= 250).ToList();
                var pool = opaque.Count > 0 ? opaque : shape.Colors;
                vertex = pool
                    .GroupBy(c => (c[0], c[1], c[2], c[3]))
                    .OrderByDescending(g => g.Count())
                    .First().First();
            }

            var (colour, note) = Colour(vertex, shader.EmissiveColor);
            var fadeKeys = nif.ReadEmissiveFadeKeys(shader.ControllerRef, out var fadeInterp);
            bool animated = fadeKeys == null
                            && shader.EmissiveMultiple <= 0
                            && nif.HasAnimatedEmissiveMultiple(shader.ControllerRef);
            float half = 0.5f * Math.Max(ext[0], ext[1]) * placed.Scale;

            found.Add(new Marker(
                NodeName: placed.Chain.Count > 1 ? placed.Chain[^2] : shape.Name,
                Texture: basename,
                Color: colour,
                Fade: MathF.Round(shader.EmissiveMultiple, 3),
                Point: [MathF.Round(placed.Translation[0], 2),
                        MathF.Round(placed.Translation[1], 2),
                        MathF.Round(placed.Translation[2], 2)],
                HalfSize: MathF.Round(half, 1),
                Note: note,
                EmissiveIsAnimated: animated,
                FadeKeys: fadeKeys,
                FadeInterpolation: fadeInterp));
        }
        return found;
    }

    /// <summary>
    /// ENB multiplies vertex colour by emissive colour, so we do too -- then
    /// renormalise, because Light Placer's colour is a hue and brightness is
    /// carried separately by fade. Without that, a mesh whose two colours are
    /// both dark yields a near-black tint: ElSopa's frostbite venom multiplies
    /// out to (0,24,1), a green light nobody would ever see.
    ///
    /// When the product collapses entirely -- the two sides tint different
    /// channels -- there is no hue left to rescue, so keep whichever side
    /// carries the colour and flag it for review.
    /// </summary>
    private static (int[], string) Colour(byte[] vertex, float[] emissive)
    {
        float[] v = [vertex[0] / 255f, vertex[1] / 255f, vertex[2] / 255f];
        float[] e = [Math.Min(1f, emissive[0]), Math.Min(1f, emissive[1]), Math.Min(1f, emissive[2])];
        float[] prod = [v[0] * e[0], v[1] * e[1], v[2] * e[2]];
        string note = string.Empty;

        if (prod.Max() < 0.004f)
        {
            prod = e.Max() > v.Max() ? e : v;
            note = "product-collapsed";
        }

        float peak = prod.Max();
        if (peak > 0) prod = [prod[0] / peak, prod[1] / peak, prod[2] / peak];
        return ([Round255(prod[0]), Round255(prod[1]), Round255(prod[2])], note);
    }

    private static int Round255(float v) => Math.Min(255, (int)MathF.Round(v * 255));

    private static (float[] Min, float[] Max) Bounds(List<float[]> verts)
    {
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue];
        float[] max = [float.MinValue, float.MinValue, float.MinValue];
        foreach (var v in verts)
            for (int k = 0; k < 3; k++)
            {
                min[k] = Math.Min(min[k], v[k]);
                max[k] = Math.Max(max[k], v[k]);
            }
        return (min, max);
    }

    private sealed record Placement(float[] Rotation, float Scale, float[] Translation, List<string> Chain);

    /// <summary>Accumulated transform per block, walked from the root node.</summary>
    private static Dictionary<int, Placement> WorldTransforms(Nif nif)
    {
        var result = new Dictionary<int, Placement>();
        var seen = new HashSet<int>();
        var stack = new Stack<(int Index, Placement Parent)>();
        stack.Push((0, new Placement([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, [0, 0, 0], [])));

        while (stack.Count > 0)
        {
            var (i, parent) = stack.Pop();
            if (i < 0 || i >= nif.BlockCount || !seen.Add(i)) continue;

            Nif.AvObject av;
            try { av = nif.ReadAvObject(i); }
            catch { continue; }

            var rotation = MatMul(parent.Rotation, av.Rotation);
            var translation = Apply(parent.Rotation, parent.Scale, parent.Translation, av.Translation);
            var chain = new List<string>(parent.Chain) { av.Name };
            var placed = new Placement(rotation, parent.Scale * av.Scale, translation, chain);
            result[i] = placed;

            foreach (var child in av.Children) stack.Push((child, placed));
        }
        return result;
    }

    private static float[] MatMul(float[] a, float[] b)
    {
        var m = new float[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    private static float[] Apply(float[] rot, float scale, float[] trans, float[] v)
    {
        var o = new float[3];
        for (int r = 0; r < 3; r++)
            o[r] = (rot[r * 3] * v[0] + rot[r * 3 + 1] * v[1] + rot[r * 3 + 2] * v[2]) * scale + trans[r];
        return o;
    }
}
