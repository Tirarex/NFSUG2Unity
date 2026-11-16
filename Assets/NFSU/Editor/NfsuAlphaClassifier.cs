// Decides how a texture is blended: opaque, alpha-tested (cutout), blended, or additive.
//
// There is no stored render state in NFSU bundles - every dword of the drawable header, the submesh
// record and the material-slot entries was compared between glow art and solid art and nothing
// separates them (AGENTS.md: "No blend/render-state flag exists in the drawable, the submesh record
// or the TPK tables"). What does carry the answer, in order of authority:
//
//   1. dword @40 of the 124-byte texture record (NfsuTextureRecord.GroupId) is a shared GROUP id,
//      not a name hash: only 8 distinct values over RA/RX/Global, and the special ones name whole
//      families unambiguously - the water animation frames, the viaduct water, the light-flare/glow
//      set, the road/sidewalk detail group whose DXT3 alpha is a decal mask, the shadow ramp.
//   2. DXT1 has no alpha channel, but its 3-colour mode index 3 is transparent black and its use is
//      bimodal per texture, so NfsuDdsWriter.Dxt1UsesKeyedAlpha settles cutout vs opaque there
//      (that is what makes foliage and chain fences work today).
//   3. For DXT3/DXT5/A8R8G8B8 the answer is in the pixels: an alpha-tested or blended object is
//      painted on a BLACK field, while the game's secondary masks sit on a full-colour field. The
//      colour is only read here to pick a SHADER - the .dds file is written as-is and Unity's own
//      DDS importer reads the alpha back (we never decode pixels for the imported texture).
//
// Validated against RA placement counts (nfs_probe/nfsu_alpha.py + blend_rules_check.py): the
// additive set is HEADLIGHTGLOW (1 232 placements), OBJ_TELEPOLE_LITE_BRIGH (921), the neon signs,
// ARC_SUBWAY_TRIM and the window-light art; the blend set is TRN_WATERFALL_A, RDD_VIADUCT* and
// OBJ_WIRES_HANGING; cutout picks up ARC_ST_BALC_BAR and OBJ_TREELIGHTS_STREET on top of the DXT1
// foliage/fences; all road / sidewalk / concrete / brick / pipe / barrier art stays opaque.
using System;
using System.Collections.Generic;

namespace NFSU
{
    /// <summary>Content signature of one texture, measured over sampled mip-0 texels.</summary>
    public struct NfsuAlphaFeatures
    {
        public int Sampled;
        public float MeanA;       // mean alpha, 0..1
        public float Frac0;       // fraction at the bottom of the format's alpha range
        public float Frac15;      // fraction at the top
        public float Corr;        // Pearson correlation of alpha and luminance
        public float LLow;        // mean luminance where alpha < 0.25, -1 when there is none
        public float LHigh;       // mean luminance where alpha > 0.75, -1 when there is none
        public float Black;       // fraction of texels darker than 0.06
        public float Spread;      // p95 - p5 of the alpha values: does the channel vary at all?
    }

    public enum NfsuAlphaClass
    {
        Opaque,
        Cutout,       // alpha-tested: the texels with no coverage are discarded
        Blend,        // SrcAlpha OneMinusSrcAlpha: translucent sheets (water, wires, glass)
        Additive,     // SrcAlpha One: light glows, neon, lamp glass
    }

    public static class NfsuAlphaClassifier
    {
        // Group ids seen in texture record dword @40, over the deduped RA + GLOBALA + GLOBALB union
        // (1504 records - counted by nfs_probe\group_members.py, class split verified by alpha_parity.py).
        public const uint GroupDefault = 0x001A93CF;   // 1413: ordinary art, says nothing by itself
        public const uint GroupTerrain = 0x1B81E7B0;   //   57: road, sidewalk, courtyard detail -> all opaque
        public const uint GroupWaterAnim = 0x03786B45; //   14: ANM_WATERA_ animation frames -> all blend
        public const uint GroupViaduct = 0x5032E8E4;   //    5: RDD_VIADUCT*, incl. its water -> all blend
        public const uint GroupLitA = 0x6B6D324D;      //    3: OBJ_FENCE_CHNLINK(+02), ARC_FACADE_19_LIT
                                                       //       - all use keyed alpha, i.e. fences
        public const uint GroupLitB = 0x6B6D3250;      //    4: ARC_BTM_BLD_102_LIT, ARC_ST_BUILD05_LIT
                                                       //       (no keyed alpha -> glows), ARC_WIND_STORELIT
                                                       //       (keyed alpha), ARC_HOTEL_ROT_DOOR01 (DXT3)
        public const uint GroupGlow = 0x6B6D3252;      //    7: HEADLIGHTGLOW + HEADLIGHTFLARE{INNER,OUTER}
                                                       //       (A8), ARC_LI_WDWN01/03_L, ARC_LI_STWN09_L,
                                                       //       SGN_LI_OPEN01 (DXT1 without keyed alpha)
        public const uint GroupShadow = 0x89BBF85A;    //    1: SHADOWRAMP (a ground decal, A8R8G8B8)

        /// <summary>
        /// Textures whose class the bundle cannot state. Alpha-tested art (a steel balcony railing,
        /// street-tree lights, metal grilles) and additive light art (the telepole light, neon tubes)
        /// are encoded IDENTICALLY: both are painted on a black field, both give
        /// corr(alpha, luminance) = +0.9..+1.0, and both have the same alpha histogram. Nothing in the
        /// pixels separates them, and no blend state is stored anywhere in the format, so these keys
        /// are named instead of guessed. Keep this list short - it is a statement about the game's
        /// data, not a tuning knob.
        /// </summary>
        static readonly Dictionary<uint, NfsuAlphaClass> Overrides = new Dictionary<uint, NfsuAlphaClass>
        {
            { 0x491FA5C0u, NfsuAlphaClass.Cutout },      // ARC_ST_BALC_BAR  - steel railing bars
            { 0x8EEB238Au, NfsuAlphaClass.Cutout },      // OBJ_TREELIGHTS_STREET - tiny light dots
            { 0x69C032BAu, NfsuAlphaClass.Cutout },      // GRILL_02         - metal grille
            { 0x69C032BBu, NfsuAlphaClass.Cutout },      // GRILL_03         - metal grille
        };

        /// <summary>
        /// Class of one texture record. Four facts, in this order, no tuned thresholds beyond those
        /// four numbers (0.30 correlation, 0.25 low-alpha luminance, 0.30 / 0.45 black-field share):
        ///
        ///   1. the TPK group id names whole families unambiguously (water, light glows, road art);
        ///   2. DXT1 has no alpha channel, so its keyed-alpha mode decides cutout vs opaque;
        ///   3. if the stored alpha does not vary, no shader can use it - opaque;
        ///   4. otherwise the RGB tells what the alpha means: art painted on a BLACK field with the
        ///      colour dark where alpha is low is light intensity (additive); painted on a black field
        ///      with colour unrelated to alpha is alpha-tested geometry (cutout); a full-colour field
        ///      with varying alpha is real coverage (blend); a full-colour field with a mask-like
        ///      alpha stays a surface (opaque).
        ///
        /// <paramref name="multiSlot"/> says the drawables that use this texture draw it over another
        /// texture (base + overlay), which is the game's own glow-pass pattern.
        /// </summary>
        public static NfsuAlphaClass Classify(NfsuTextureRecord rec, byte[] blob, bool multiSlot)
        {
            if (rec == null)
                return NfsuAlphaClass.Opaque;

            NfsuAlphaClass forced;
            if (Overrides.TryGetValue(rec.BinKey, out forced))
                return forced;

            // 1. named families: water is water, and the road / sidewalk / shadow groups are decals
            //    whose DXT3 alpha is the game's secondary mask, never coverage.
            if (rec.GroupId == GroupWaterAnim || rec.GroupId == GroupViaduct)
                return NfsuAlphaClass.Blend;
            if (rec.GroupId == GroupShadow || rec.GroupId == GroupTerrain)
                return NfsuAlphaClass.Opaque;

            bool lightFamily = rec.GroupId == GroupLitA || rec.GroupId == GroupLitB ||
                               rec.GroupId == GroupGlow;

            // 2. DXT1 has no alpha channel, it has KEYED alpha, and whether a record actually uses the
            //    transparent-black entry is a hard format fact: it says alpha-tested geometry. That
            //    outranks the group id - OBJ_FENCE_CHNLINK (563 placements) and ARC_FACADE_19_LIT sit
            //    in a "lit" group but do use keyed alpha, so they are fences, not glows; while the real
            //    light overlays (ARC_LI_WDWN01_L, ARC_LI_WDWN03_L, ARC_LI_STWN09_L, SGN_LI_OPEN01,
            //    ARC_BTM_BLD_102_LIT) never use it, which is what lets the group id call them glows.
            if (rec.Format == NfsuDdsWriter.FmtDXT1)
            {
                if (NfsuDdsWriter.Dxt1UsesKeyedAlpha(rec, blob))
                    return NfsuAlphaClass.Cutout;
                return lightFamily ? NfsuAlphaClass.Additive : NfsuAlphaClass.Opaque;
            }

            // A8/L8 is written with A = 255 (RGB = the channel), so it has no coverage at all here:
            // HEADLIGHTGLOW is additive by its group id, lettering and car bodies are solid art.
            if (rec.Format == NfsuDdsWriter.FmtA8)
                return lightFamily ? NfsuAlphaClass.Additive : NfsuAlphaClass.Opaque;

            // 3. does the file actually carry a varying alpha channel?
            NfsuAlphaFeatures f;
            if (!NfsuDdsWriter.MeasureAlpha(rec, blob, out f) || f.Frac0 >= 0.97f)
                return NfsuAlphaClass.Opaque;

            // 4. what the alpha means, from the colour underneath it
            bool onBlackField = f.Black >= 0.30f;
            if (f.Corr >= 0.30f && f.LLow >= 0.0f && f.LLow <= 0.25f && onBlackField
                    && (multiSlot || (f.Frac0 >= 0.30f && f.Black >= 0.45f)))
                return NfsuAlphaClass.Additive;   // OBJ_TELEPOLE_LITE_BRIGH, ARC_NEON*, ARC_SUBWAY_TRIM
            if (f.Frac0 >= 0.30f && f.Corr <= 0.35f && onBlackField)
                return NfsuAlphaClass.Cutout;     // ARC_ST_BALC_BAR, OBJ_TREELIGHTS_STREET
            if (f.Frac0 <= 0.25f && f.Frac15 <= 0.30f && f.Black < 0.15f && f.Corr <= 0.30f)
                return NfsuAlphaClass.Blend;      // TRN_WATERFALL_A, OBJ_WIRES_HANGING
            return NfsuAlphaClass.Opaque;         // concrete, brick, road, pipe, barrier art
        }

        /// <summary>Shader that draws this class (all four exist under Assets/NFSU/Shaders).</summary>
        public static string ShaderName(NfsuAlphaClass c)
        {
            switch (c)
            {
                case NfsuAlphaClass.Cutout: return "NFSU/Unlit VertexColor Cutout";
                case NfsuAlphaClass.Blend: return "NFSU/Unlit VertexColor Blend";
                case NfsuAlphaClass.Additive: return "NFSU/Unlit VertexColor Additive";
                default: return "NFSU/Unlit VertexColor";
            }
        }

        public static string ModeName(NfsuAlphaClass c)
        {
            switch (c)
            {
                case NfsuAlphaClass.Cutout: return "cutout";
                case NfsuAlphaClass.Blend: return "blend";
                case NfsuAlphaClass.Additive: return "additive";
                default: return "opaque";
            }
        }
    }
}
