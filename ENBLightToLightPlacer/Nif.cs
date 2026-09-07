using System.Buffers.Binary;
using System.Text;

namespace ENBLightToLightPlacer;

/// <summary>
/// A minimal NIF reader -- only the blocks an ENB particle-light marker needs.
///
/// Deliberately hand-rolled rather than taking a dependency on a full NIF
/// library: we read four things (the node tree's transforms, BSTriShape vertex
/// colours and bounds, BSEffectShaderProperty fields, and the string table),
/// and a narrow reader that asserts its own offsets is easier to trust than a
/// broad API used at 5% depth.
///
/// Layouts follow nifly for File 20.2.0.7 / User 12 / Stream 100 (Skyrim SE),
/// and are a port of tools/nifedit.py + tools/nifmesh.py in this repo, which
/// were confirmed against real meshes rather than documentation.
/// </summary>
public sealed class Nif
{
    private readonly byte[] _data;
    private readonly int[] _blockOffsets;
    private readonly int[] _blockSizes;
    private readonly ushort[] _typeIndex;

    public uint Version { get; }
    public uint User { get; }
    public uint BsVersion { get; }
    public IReadOnlyList<string> Types { get; }
    public IReadOnlyList<string> Strings { get; }
    public int BlockCount => _blockOffsets.Length;

    public Nif(byte[] data)
    {
        _data = data;
        var c = new Cursor(data);

        // Header line is a version string terminated by \n.
        int nl = Array.IndexOf(data, (byte)'\n');
        if (nl < 0) throw new InvalidDataException("not a NIF: no header line");
        c.Position = nl + 1;

        Version = c.U32();
        c.Skip(1);                       // endian
        User = c.U32();
        int blocks = (int)c.U32();
        BsVersion = c.U32();

        for (int i = 0; i < 3; i++) c.Skip(1 + c.Peek());   // export info bzstrings
        if (BsVersion >= 130) c.Skip(1 + c.Peek());

        int typeCount = c.U16();
        var types = new string[typeCount];
        for (int i = 0; i < typeCount; i++) types[i] = c.SizedString();
        Types = types;

        _typeIndex = new ushort[blocks];
        for (int i = 0; i < blocks; i++) _typeIndex[i] = c.U16();

        _blockSizes = new int[blocks];
        for (int i = 0; i < blocks; i++) _blockSizes[i] = (int)c.U32();

        int stringCount = (int)c.U32();
        c.Skip(4);                       // max string length
        var strings = new string[stringCount];
        for (int i = 0; i < stringCount; i++) strings[i] = c.SizedString();
        Strings = strings;

        int groups = (int)c.U32();
        c.Skip(4 * groups);

        _blockOffsets = new int[blocks];
        for (int i = 0; i < blocks; i++)
        {
            _blockOffsets[i] = c.Position;
            c.Skip(_blockSizes[i]);
        }
    }

    public string BlockType(int i) => Types[_typeIndex[i]];

    public ReadOnlySpan<byte> Block(int i) => _data.AsSpan(_blockOffsets[i], _blockSizes[i]);

    public string StringAt(int i) => i >= 0 && i < Strings.Count ? Strings[i] : string.Empty;

    // ---------------------------------------------------------------- blocks

    public readonly record struct AvObject(
        string Name, float[] Rotation, float[] Translation, float Scale, int[] Children);

    /// <summary>NiAVObject prefix; children only exist on node types.</summary>
    public AvObject ReadAvObject(int i)
    {
        var c = new Cursor(Block(i));
        int nameId = c.I32();
        int extras = (int)c.U32();
        c.Skip(4 * extras);
        c.Skip(4);                                  // controller
        c.Skip(4);                                  // flags
        var translation = new[] { c.F32(), c.F32(), c.F32() };
        var rotation = new float[9];
        for (int k = 0; k < 9; k++) rotation[k] = c.F32();
        float scale = c.F32();
        c.Skip(4);                                  // collision object

        int[] children = [];
        if (BlockType(i).EndsWith("Node", StringComparison.Ordinal))
        {
            int n = (int)c.U32();
            children = new int[n];
            for (int k = 0; k < n; k++) children[k] = c.I32();
        }
        return new AvObject(StringAt(nameId), rotation, translation, scale, children);
    }

    public readonly record struct EffectShader(
        string SourceTexture, float[] EmissiveColor, float EmissiveMultiple, int ControllerRef);

    public EffectShader ReadEffectShader(int i)
    {
        var c = new Cursor(Block(i));
        c.Skip(4);                                  // name
        int extras = (int)c.U32();
        c.Skip(4 * extras);
        int controller = c.I32();
        c.Skip(4 + 4);                              // shader flags 1 and 2
        c.Skip(8 + 8);                              // uv offset, uv scale
        string source = c.SizedString();
        c.Skip(4);                                  // texture clamp mode
        c.Skip(16);                                 // falloff start/stop/opacities
        var colour = new[] { c.F32(), c.F32(), c.F32(), c.F32() };
        float multiple = c.F32();
        return new EffectShader(source, colour, multiple, controller);
    }

    /// <summary>
    /// Is the emissive multiple animated rather than static?
    ///
    /// The Dwemer control cubes ship emissiveMultiple = 0 with a
    /// BSEffectShaderPropertyFloatController driving it, so reading the static
    /// field alone yields a light that emits nothing. The curve itself is not
    /// recoverable here -- the controller points at a NiBlendFloatInterpolator
    /// whose value is the -3.4e38 "unset" sentinel, because the real keys live
    /// in one of the NiControllerSequence blocks the animation system picks at
    /// runtime -- so this only reports *that* it is animated. The caller
    /// substitutes a constant.
    ///
    /// Layout: NiTimeController is 26 bytes (next, flags, frequency, phase,
    /// start, stop, target), NiSingleInterpController adds the interpolator
    /// ref, and the controlled-variable enum follows at 30. Variable 0 is the
    /// emissive multiple; confirmed against the control cube, whose tail reads
    /// 00000000.
    /// </summary>
    public bool HasAnimatedEmissiveMultiple(int controllerRef)
    {
        int guard = 0;
        for (int c = controllerRef; c >= 0 && c < BlockCount && guard++ < 32;)
        {
            var b = Block(c);
            if (b.Length < 34) break;
            if (BlockType(c) == "BSEffectShaderPropertyFloatController"
                && BinaryPrimitives.ReadUInt32LittleEndian(b[30..]) == 0)
                return true;
            c = BinaryPrimitives.ReadInt32LittleEndian(b);   // nextController
        }
        return false;
    }

    /// <summary>One key of an emissive-multiple animation.</summary>
    public readonly record struct FloatKey(float Time, float Value, float Forward, float Backward);

    /// <summary>
    /// The keyframes driving the emissive multiple, if they are reachable.
    ///
    /// Reachable means the controller points at a NiFloatInterpolator with
    /// NiFloatData behind it -- the hagraven hanging balls and spriggan taproots
    /// pulse 0.8 -> 1.2 -> 0.8 over three seconds this way. It is NOT reachable
    /// when the interpolator is a NiBlendFloatInterpolator (the Dwemer control
    /// cubes): that is a runtime blend target holding an "unset" sentinel, and
    /// the real keys sit in whichever NiControllerSequence the animation system
    /// selects.
    ///
    /// NiFloatData: numKeys, then the key type -- 1 linear (time, value),
    /// 2 quadratic (plus forward and backward tangents), 3 TBC (plus tension,
    /// bias, continuity, which we read as linear since Light Placer has no
    /// equivalent).
    /// </summary>
    public List<FloatKey>? ReadEmissiveFadeKeys(int controllerRef, out string interpolation)
    {
        interpolation = "Linear";
        int guard = 0;
        for (int c = controllerRef; c >= 0 && c < BlockCount && guard++ < 32;)
        {
            var cb = Block(c);
            if (cb.Length < 34) return null;
            if (BlockType(c) != "BSEffectShaderPropertyFloatController"
                || BinaryPrimitives.ReadUInt32LittleEndian(cb[30..]) != 0)
            {
                c = BinaryPrimitives.ReadInt32LittleEndian(cb);
                continue;
            }

            int interp = BinaryPrimitives.ReadInt32LittleEndian(cb[26..]);
            if (interp < 0 || interp >= BlockCount) return null;
            if (BlockType(interp) != "NiFloatInterpolator") return null;

            int dataRef = BinaryPrimitives.ReadInt32LittleEndian(Block(interp)[4..]);
            if (dataRef < 0 || dataRef >= BlockCount || BlockType(dataRef) != "NiFloatData") return null;

            var db = Block(dataRef);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(db);
            uint keyType = BinaryPrimitives.ReadUInt32LittleEndian(db[4..]);
            int stride = keyType switch { 2 => 16, 3 => 20, _ => 8 };
            interpolation = keyType == 2 ? "Cubic" : "Linear";

            var keys = new List<FloatKey>();
            for (int k = 0; k < count; k++)
            {
                int off = 8 + k * stride;
                if (off + stride > db.Length) break;
                float time = BinaryPrimitives.ReadSingleLittleEndian(db[off..]);
                float value = BinaryPrimitives.ReadSingleLittleEndian(db[(off + 4)..]);
                float fwd = 0f, back = 0f;
                if (keyType == 2)
                {
                    fwd = BinaryPrimitives.ReadSingleLittleEndian(db[(off + 8)..]);
                    back = BinaryPrimitives.ReadSingleLittleEndian(db[(off + 12)..]);
                }
                keys.Add(new FloatKey(time, value, fwd, back));
            }
            return keys.Count > 0 ? keys : null;
        }
        return null;
    }

    /// <summary>Shader and alpha property refs of a geometry block.</summary>
    public (int Shader, int Alpha) ReadGeometryShaderRefs(int i)
    {
        var c = new Cursor(Block(i));
        c.Skip(4);
        int extras = (int)c.U32();
        c.Skip(4 * extras);
        c.Skip(4 + 4);                          // controller, flags
        c.Skip(12 + 36 + 4);                    // transform
        c.Skip(4);                              // collision
        c.Skip(16);                             // bounding sphere
        c.Skip(4);                              // skin
        int shader = c.I32();
        int alpha = c.I32();
        return (shader, alpha);
    }

    /// <summary>NiObjectNET's controller reference.</summary>
    public int ControllerRef(int i)
    {
        try
        {
            var c = new Cursor(Block(i));
            c.Skip(4);
            int extras = (int)c.U32();
            c.Skip(4 * extras);
            return c.I32();
        }
        catch { return -1; }
    }

    /// <summary>
    /// A float interpolator's value: the constant if it has one, otherwise the
    /// mean of its keys. A NiFloatInterpolator holding the -3.4e38 sentinel is
    /// animated, and its data block carries the real values.
    /// </summary>
    public float InterpolatorValue(int i)
    {
        if (i < 0 || i >= BlockCount) return 0;
        if (BlockType(i) != "NiFloatInterpolator") return 0;
        var b = Block(i);
        if (b.Length < 8) return 0;

        float value = BinaryPrimitives.ReadSingleLittleEndian(b);
        if (MathF.Abs(value) < 1e30f) return value;

        int dataRef = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
        if (dataRef < 0 || dataRef >= BlockCount || BlockType(dataRef) != "NiFloatData") return 0;
        var db = Block(dataRef);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(db);
        uint keyType = BinaryPrimitives.ReadUInt32LittleEndian(db[4..]);
        int stride = keyType switch { 2 => 16, 3 => 20, _ => 8 };
        float total = 0;
        int seen = 0;
        for (int k = 0; k < count; k++)
        {
            int off = 8 + k * stride;
            if (off + 8 > db.Length) break;
            total += BinaryPrimitives.ReadSingleLittleEndian(db[(off + 4)..]);
            seen++;
        }
        return seen > 0 ? total / seen : 0;
    }

    public string SafeName(int i)
    {
        try
        {
            var c = new Cursor(Block(i));
            return StringAt(c.I32());
        }
        catch { return string.Empty; }
    }

    public sealed record Placement(float[] Rotation, float Scale, float[] Translation, List<string> Chain);

    /// <summary>Accumulated transform per block, walked from the root node.</summary>
    public static Dictionary<int, Placement> WorldTransforms(Nif nif)
    {
        var result = new Dictionary<int, Placement>();
        var seen = new HashSet<int>();
        var stack = new Stack<(int Index, Placement Parent)>();
        stack.Push((0, new Placement([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, [0, 0, 0], [])));

        while (stack.Count > 0)
        {
            var (i, parent) = stack.Pop();
            if (i < 0 || i >= nif.BlockCount || !seen.Add(i)) continue;

            AvObject av;
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

    public sealed class Shape
    {
        public int Index;
        public string Name = string.Empty;
        public int ShaderRef = -1;
        public List<float[]> Verts = [];
        public List<byte[]> Colors = [];
    }

    private static readonly string[] ShapeTypes =
        ["BSTriShape", "BSSubIndexTriShape", "BSDynamicTriShape", "BSMeshLODTriShape"];

    private const int VfVertex = 0x0010, VfUv = 0x0020, VfNormal = 0x0080,
                      VfTangent = 0x0100, VfColors = 0x0200, VfSkinned = 0x0400;

    /// <summary>Vertex positions and colours for every shape carrying them.</summary>
    public List<Shape> ReadShapes()
    {
        var shapes = new List<Shape>();
        for (int i = 0; i < BlockCount; i++)
        {
            if (!ShapeTypes.Contains(BlockType(i))) continue;
            var block = Block(i);
            var c = new Cursor(block);

            int nameId = c.I32();
            int extras = (int)c.U32();
            c.Skip(4 * extras);
            c.Skip(4 + 4);                          // controller, flags
            c.Skip(12 + 36 + 4);                    // translation, rotation, scale
            c.Skip(4);                              // collision
            c.Skip(16);                             // bounding sphere
            c.Skip(4);                              // skin ref
            int shaderRef = c.I32();
            c.Skip(4);                              // alpha ref
            ulong desc = c.U64();
            if (BsVersion < 130) c.Skip(2); else c.Skip(4);   // triangle count
            int vertCount = c.U16();
            c.Skip(4);                              // data size

            int stride = (int)(desc & 0xF) * 4;
            int flags = (int)((desc >> 40) & 0xFFF);
            var shape = new Shape { Index = i, Name = StringAt(nameId), ShaderRef = shaderRef };

            if (stride > 0 && vertCount > 0 && c.Position + stride * vertCount <= block.Length)
                DecodeVertices(block, c.Position, vertCount, stride, flags, shape);

            shapes.Add(shape);
        }
        return shapes;
    }

    /// <summary>
    /// Unpack the interleaved vertex stream. Field order follows nifly's
    /// BSVertexData; absent fields occupy no bytes, which is what makes the
    /// stride a reliable check on the flag decode.
    /// </summary>
    private static void DecodeVertices(
        ReadOnlySpan<byte> buf, int offset, int count, int stride, int flags, Shape shape)
    {
        for (int i = 0; i < count; i++)
        {
            int p = offset + i * stride;
            if ((flags & VfVertex) != 0)
            {
                shape.Verts.Add([
                    BinaryPrimitives.ReadSingleLittleEndian(buf[p..]),
                    BinaryPrimitives.ReadSingleLittleEndian(buf[(p + 4)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(buf[(p + 8)..])
                ]);
                p += 16;                            // 3 floats plus bitangent X
            }
            if ((flags & VfUv) != 0) p += 4;
            if ((flags & VfNormal) != 0) p += 4;    // 3 bytes plus bitangent Y
            if ((flags & VfTangent) != 0) p += 4;
            if ((flags & VfColors) != 0)
            {
                shape.Colors.Add([buf[p], buf[p + 1], buf[p + 2], buf[p + 3]]);
                p += 4;
            }
            if ((flags & VfSkinned) != 0) p += 12;
        }
    }

    // --------------------------------------------------------------- cursor

    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        public int Position { get; set; }

        public byte Peek() => _d[Position];
        public void Skip(int n) => Position += n;

        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[Position..]); Position += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[Position..]); Position += 8; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d[Position..]); Position += 4; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[Position..]); Position += 2; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_d[Position..]); Position += 4; return v; }

        public string SizedString()
        {
            int n = (int)U32();
            var s = Encoding.Latin1.GetString(_d.Slice(Position, n));
            Position += n;
            return s;
        }
    }
}
