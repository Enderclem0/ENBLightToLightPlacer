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
