#version 450

// Effect dispatch fragment shader. One shader, one pipeline, one draw per
// effect pass. The effectType selects which kernel logic runs; params[]
// carries that kernel's parameters. Translated from the macOS Core Image
// Metal kernels (Metal/*.metal) — the coreimage::sampler/destination dialect
// maps directly to sampler2D + fragUV here.
//
// Effect type constants (must match EffectType in VulkanEffectPipeline.swift):
//   0 = none (passthrough)
//   1 = vignette
//   2 = wheels (lift/gamma/gain)
//   3 = levels (blacks/whites)
//   4 = grain
//   5 = chromaKey
//   6 = highlightsShadows
//   7 = edgeRounding
//   8 = clarity (src + aux=blurred)
//   9 = glowBright
//   10 = glowComposite (src + aux=blurred glow)
//   11 = gradeCurves (aux=per-channel LUT, aux2=master LUT)
//   12 = hueCurves (aux=hue LUT)
//   13 = lutTetra (aux=3D-LUT 2D strip)
//   14 = blur (separable gaussian, one direction per pass)
//   15 = invert
layout(push_constant) uniform EffectBlock {
    uint effectType;
    float params[30];
} u;

layout(set = 0, binding = 0) uniform sampler2D src;
layout(set = 0, binding = 1) uniform sampler2D aux;
layout(set = 0, binding = 2) uniform sampler2D aux2;

layout(location = 0) in vec2 fragUV;
layout(location = 0) out vec4 outColor;

// hash13 — used by grain (matches Metal/Grain.metal).
float hash13(vec3 p3) {
    p3 = fract(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return fract((p3.x + p3.y) * p3.z);
}

vec3 saturate3(vec3 c) { return clamp(c, 0.0, 1.0); }

// --- Kernels ---

vec4 effectVignette(vec4 s) {
    // params: [amount, midpoint, roundness, feather, aspect (w/h)]
    float amount = u.params[0];
    float midpoint = u.params[1];
    float roundness = u.params[2];
    float feather = u.params[3];
    vec2 d = (fragUV - 0.5) * 2.0;          // -1..1 across frame
    d.x *= u.params[4];                       // correct for aspect
    float p = mix(6.0, 2.0, (roundness + 1.0) * 0.5);
    float dist = pow(pow(abs(d.x), p) + pow(abs(d.y), p), 1.0 / p);
    float v = smoothstep(midpoint, midpoint + feather * 1.5 + 0.05, dist);
    return vec4(saturate3(s.rgb * (1.0 + amount * v)), s.a);
}

vec4 effectWheels(vec4 s) {
    // params: [lift.r, lift.g, lift.b, gain.r, gain.g, gain.b, invGamma.r, invGamma.g, invGamma.b]
    vec3 lift = vec3(u.params[0], u.params[1], u.params[2]);
    vec3 gain = vec3(u.params[3], u.params[4], u.params[5]);
    vec3 invGamma = vec3(u.params[6], u.params[7], u.params[8]);
    vec3 lit = max(s.rgb * (1.0 - lift) + lift, vec3(0.0)) * gain;
    return vec4(saturate3(pow(lit, invGamma)), s.a);
}

vec4 effectLevels(vec4 s) {
    // params: [blacks, whites]
    float blacks = u.params[0];
    float whites = u.params[1];
    float bp = -blacks * 0.4;
    float wp = 1.0 - whites * 0.4;
    return vec4(saturate3((s.rgb - bp) / max(0.05, wp - bp)), s.a);
}

vec4 effectGrain(vec4 s) {
    // params: [amount, size, frame]
    float amount = u.params[0];
    float size = u.params[1];
    float frame = u.params[2];
    vec2 co = fragUV * vec2(textureSize(src, 0)) / max(size, 0.5);
    float n = hash13(vec3(co, frame)) - 0.5;
    float y = dot(s.rgb, vec3(0.2126, 0.7152, 0.0722));
    float lumaMask = 4.0 * y * (1.0 - y);
    return vec4(saturate3(s.rgb + n * amount * 0.35 * lumaMask), s.a);
}

vec4 effectChromaKey(vec4 s) {
    // params: [keyColor.r, keyColor.g, keyColor.b, threshold, spillSuppression]
    vec3 keyColor = vec3(u.params[0], u.params[1], u.params[2]);
    float threshold = u.params[3];
    float spill = u.params[4];
    float dist = distance(s.rgb, keyColor);
    float mask = 1.0 - smoothstep(threshold * 0.5, threshold, dist);
    // Spill suppression: reduce the key-hue component in the remaining pixels.
    float maxC = max(s.r, max(s.g, s.b));
    float avgC = (s.r + s.g + s.b) / 3.0;
    float spillAmount = (maxC - avgC) * spill;
    vec3 desaturated = mix(s.rgb, vec3(avgC), spillAmount);
    return vec4(desaturated, s.a * (1.0 - mask));
}

vec4 effectHighlightsShadows(vec4 s) {
    // params: [highlights, shadows]
    float highlights = u.params[0];
    float shadows = u.params[1];
    float y = dot(s.rgb, vec3(0.2126, 0.7152, 0.0722));
    float hiMask = smoothstep(0.5, 1.0, y);
    float loMask = smoothstep(0.5, 0.0, y);
    vec3 adjusted = s.rgb + highlights * hiMask - shadows * loMask;
    return vec4(saturate3(adjusted), s.a);
}

vec4 effectEdgeRounding(vec4 s) {
    // params: [radius (0..1 fraction of half-min-dim), softness]
    float radius = u.params[0];
    float softness = u.params[1];
    vec2 px = fragUV;
    vec2 d = abs(px - 0.5) - (0.5 - radius);
    float dist = length(max(d, vec2(0.0))) + min(max(d.x, d.y), 0.0);
    float alpha = 1.0 - smoothstep(0.0, softness, dist);
    return vec4(s.rgb, s.a * alpha);
}

// Metal/Clarity.metal — unsharp vs a blurred copy + dark-channel-prior dehaze.
vec4 effectClarity(vec4 s) {
    // params: [clarity, dehaze]; aux = blurred src
    float clarity = u.params[0];
    float dehaze = u.params[1];
    vec3 b = texture(aux, fragUV).rgb;
    vec3 rgb = s.rgb + (s.rgb - b) * clarity;
    if (dehaze != 0.0) {
        float dark = min(s.rgb.r, min(s.rgb.g, s.rgb.b));            // high = hazy
        float w = dehaze * (0.5 + 0.5 * smoothstep(0.05, 0.5, dark));
        rgb += (s.rgb - b) * (w * 0.6);                              // local contrast
        rgb = mix(vec3(0.45), rgb, 1.0 + w * 0.45);                  // crush the veil
        float yy = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
        rgb = mix(vec3(yy), rgb, 1.0 + w * 0.5);                     // re-saturate
    }
    return vec4(saturate3(rgb), s.a);
}

// Metal/Glow.metal — isolate + warm-tint highlights (host blurs afterwards).
vec4 effectGlowBright(vec4 s) {
    // params: [threshold, warmth]
    float threshold = u.params[0];
    float warmth = u.params[1];
    float y = dot(s.rgb, vec3(0.2126, 0.7152, 0.0722));
    vec3 hi = s.rgb * smoothstep(threshold, 1.0, y);
    vec3 warm = hi * vec3(1.0, 0.7, 0.45);          // halation's red-orange cast
    return vec4(mix(hi, warm, warmth), s.a);
}

// Metal/Glow.metal — screen-blend the blurred highlights over the source.
vec4 effectGlowComposite(vec4 s) {
    // params: [intensity]; aux = blurred glow
    float intensity = u.params[0];
    vec3 g = saturate3(texture(aux, fragUV).rgb * intensity);
    return vec4(1.0 - (1.0 - s.rgb) * (1.0 - g), s.a);  // screen blend
}

// Metal/GradeCurves.metal — per-channel LUT (aux) + luma LUT (aux2).
vec4 effectGradeCurves(vec4 s) {
    vec3 rgb = saturate3(s.rgb);
    float y = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
    float yp = texture(aux2, vec2(y, 0.5)).r;
    // Luma-preserving rescale, gain capped so shadow-lift curves can't blow up
    // dark saturated pixels (and their compression noise).
    rgb = (y > 1e-4) ? rgb * min(yp / y, 8.0) : vec3(yp);
    float r = texture(aux, vec2(rgb.r, 0.5)).r;
    float g = texture(aux, vec2(rgb.g, 0.5)).g;
    float b = texture(aux, vec2(rgb.b, 0.5)).b;
    return vec4(r, g, b, s.a);
}

// Metal/HueCurves.metal helpers.
vec3 rgb2hsv(vec3 c) {
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
}

vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, saturate3(p - K.xxx), c.y);
}

// Metal/HueCurves.metal — aux = 256-wide LUT (R=Δhue, G=satScale, B=Δlum),
// each encoded 0..1 over its ±max range (BGRA8 has no signed channels).
vec4 effectHueCurves(vec4 s) {
    const float maxHueShift = 1.0 / 12.0;  // ±30° at a full push
    const float maxLumShift = 0.5;
    vec3 hsv = rgb2hsv(saturate3(s.rgb));
    vec4 L = texture(aux, vec2(hsv.x, 0.5));
    float dHue = (L.r - 0.5) * 2.0 * maxHueShift;
    float satScale = (L.g - 0.5) * 2.0;
    float dLum = (L.b - 0.5) * 2.0 * maxLumShift;
    float gate = smoothstep(0.04, 0.18, hsv.y);
    float h2 = fract(hsv.x + dHue * gate);
    float s2 = clamp(hsv.y * (1.0 + satScale * gate), 0.0, 1.0);
    float v2 = clamp(hsv.z + dLum * gate, 0.0, 1.0);
    return vec4(hsv2rgb(vec3(h2, s2, v2)), s.a);
}

// Metal/LUTTetra.metal — tetrahedral 3D-LUT. aux = 2D strip (width n, height
// n²; node (r,g,b) at pixel (r, b·n+g), row 0 = top). No row flip: our upload
// is top-row-first and v=0 samples the top row (unlike CoreImage's y-up).
vec3 lutFetch(float n, vec3 idx) {
    float row = idx.z * n + idx.y;
    return texture(aux, vec2((idx.x + 0.5) / n, (row + 0.5) / (n * n))).rgb;
}

vec4 effectLutTetra(vec4 s) {
    // params: [n, intensity]
    float n = u.params[0];
    float intensity = u.params[1];
    vec3 rgb = saturate3(s.rgb);
    vec3 p = rgb * (n - 1.0);
    vec3 b0 = clamp(floor(p), 0.0, n - 2.0);
    vec3 f = p - b0;

    vec3 c000 = lutFetch(n, b0);
    vec3 c111 = lutFetch(n, b0 + 1.0);
    vec3 o;
    if (f.r >= f.g) {
        if (f.g >= f.b) {
            o = (1.0 - f.r) * c000 + (f.r - f.g) * lutFetch(n, b0 + vec3(1, 0, 0))
                + (f.g - f.b) * lutFetch(n, b0 + vec3(1, 1, 0)) + f.b * c111;
        } else if (f.r >= f.b) {
            o = (1.0 - f.r) * c000 + (f.r - f.b) * lutFetch(n, b0 + vec3(1, 0, 0))
                + (f.b - f.g) * lutFetch(n, b0 + vec3(1, 0, 1)) + f.g * c111;
        } else {
            o = (1.0 - f.b) * c000 + (f.b - f.r) * lutFetch(n, b0 + vec3(0, 0, 1))
                + (f.r - f.g) * lutFetch(n, b0 + vec3(1, 0, 1)) + f.g * c111;
        }
    } else {
        if (f.b >= f.g) {
            o = (1.0 - f.b) * c000 + (f.b - f.g) * lutFetch(n, b0 + vec3(0, 0, 1))
                + (f.g - f.r) * lutFetch(n, b0 + vec3(0, 1, 1)) + f.r * c111;
        } else if (f.b >= f.r) {
            o = (1.0 - f.g) * c000 + (f.g - f.b) * lutFetch(n, b0 + vec3(0, 1, 0))
                + (f.b - f.r) * lutFetch(n, b0 + vec3(0, 1, 1)) + f.r * c111;
        } else {
            o = (1.0 - f.g) * c000 + (f.g - f.r) * lutFetch(n, b0 + vec3(0, 1, 0))
                + (f.r - f.b) * lutFetch(n, b0 + vec3(1, 1, 0)) + f.b * c111;
        }
    }
    return vec4(mix(s.rgb, o, intensity), s.a);
}

// macOS "stylize.invert" — CIColorMatrix negating RGB (bias 1), alpha kept.
vec4 effectInvert(vec4 s) {
    return vec4(1.0 - s.rgb, s.a);
}

// Separable gaussian, one direction per pass. 9 taps, binomial weights.
vec4 effectBlur(vec4 s) {
    // params: [dir.x, dir.y, radius (px), texelW, texelH]
    vec2 dir = vec2(u.params[0], u.params[1]);
    float radius = max(u.params[2], 0.0);
    vec2 texel = vec2(u.params[3], u.params[4]);
    float w[9] = float[9](1.0, 8.0, 28.0, 56.0, 70.0, 56.0, 28.0, 8.0, 1.0);
    vec3 acc = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 9; i++) {
        vec2 off = dir * (float(i) - 4.0) * (radius / 4.0) * texel;
        acc += texture(src, fragUV + off).rgb * w[i];
        wsum += w[i];
    }
    return vec4(acc / wsum, s.a);
}

void main() {
    vec4 s = texture(src, fragUV);
    switch (u.effectType) {
        case 1: outColor = effectVignette(s); break;
        case 2: outColor = effectWheels(s); break;
        case 3: outColor = effectLevels(s); break;
        case 4: outColor = effectGrain(s); break;
        case 5: outColor = effectChromaKey(s); break;
        case 6: outColor = effectHighlightsShadows(s); break;
        case 7: outColor = effectEdgeRounding(s); break;
        case 8: outColor = effectClarity(s); break;
        case 9: outColor = effectGlowBright(s); break;
        case 10: outColor = effectGlowComposite(s); break;
        case 11: outColor = effectGradeCurves(s); break;
        case 12: outColor = effectHueCurves(s); break;
        case 13: outColor = effectLutTetra(s); break;
        case 14: outColor = effectBlur(s); break;
        case 15: outColor = effectInvert(s); break;
        default: outColor = s;  // passthrough
    }
}
