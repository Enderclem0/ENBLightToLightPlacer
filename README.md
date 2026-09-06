# ENB Light → Light Placer

A [Synthesis](https://github.com/Mutagen-Modding/Synthesis) patcher that ports
ENB particle-light meshes to [Light Placer](https://www.nexusmods.com/skyrimspecialedition/mods/125865)
JSON, so they keep working on Community Shaders.

## Why

Community Shaders has no particle-light reader — Light Limit Fix's old
per-texture ini feature is gone from CS 26.x — so every *ENB Light* /
*Particle Lights for ENB* mesh in a load order is inert. The meshes still
encode everything a light needs, and it maps cleanly onto Light Placer's
schema:

| Light Placer field | Read from the mesh |
|---|---|
| `points` | the marker quad's world position |
| `color` | vertex colour × emissive RGB, renormalised |
| `fade` | `emissiveMultiple` |
| `radius` | *estimated* from quad half-size — see below |

## What it detects

The marker is a small planar billboard quad carrying a
`BSEffectShaderProperty` on `black.dds` or `fxglowENB.dds`. Node names are
**not** a reliable signal — Rudy HQ's meshes use `EnbParticleLight01`, but
Alchemy Ingredients left inherited junk like `DLC2DarkElfLantern01:40:48` —
so detection is on geometry plus texture, with the name only as a fallback.

ENB Light 0.98 itself uses a second convention (a `p*ENBLight*`
NiParticleSystem with its colour in `BSPSysSimpleColorModifier`) which this
does not yet read. Those meshes are counted and skipped.

## Radius is a guess, and says so

Radius is the one value a marker does not carry — it was the mod author's
judgement. The default power law is fitted against the radii CS Light's author
chose for meshes whose markers can also be read: half-size 80 → ~6, 102 → ~22,
120 → ~30, 160 → ~68. Spread inside a bucket is wide (15–71 at half 120), so
treat the output as a starting point and tune it in the settings.

Nothing in a mesh distinguishes a grand soul gem's light from a petty one —
every marker is the same quad at the same emissive multiple, differing only in
colour — so any ramp between tiers is imposed through `Overrides`, not derived.

## Settings

Exposed through Synthesis's generated settings UI: the light template record,
light flags, the radius power law, whether to skip `actors\` meshes (their
markers are eye glows, which would put a light on every draugr in the game),
and per-model radius/fade overrides.

## Output

A JSON file under the patcher's output folder, not plugin records — Light
Placer's data cannot be expressed as records, so the patch plugin itself stays
empty. Nothing else in the patch chain depends on it.

## Running it with Mod Organizer 2

Synthesis refuses to invoke the .NET SDK inside MO2's virtual file system
(`Mo2BuildBlockedException`), so use the two-phase flow:

1. Run Synthesis **outside** MO2 once after any change — this compiles and
   caches the build.
2. Run Synthesis **through** MO2 to actually patch. It short-circuits the
   compile because the cached build matches, and sees the merged Data folder,
   which is what makes "the winning mesh for a path" simply "the file at that
   path".

## Development

```
dotnet build
dotnet run --project ENBLightToLightPlacer -- --selftest <mesh.nif> [...]
dotnet run --project ENBLightToLightPlacer -- --selftest-assets <DataFolder> <relative\path.nif> [...]
```

`--selftest` runs the detector with no Mutagen or load order, which is the
cheap way to confirm a NIF-reading change did not silently move a colour or a
point. `--selftest-assets` exercises loose-then-archive resolution against a
real Data folder.
