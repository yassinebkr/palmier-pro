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
layout(push_constant) uniform EffectBlock {
    uint effectType;
    float params[30];
} u;

layout(set = 0, binding = 0) uniform sampler2D src;

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
    return vec4(desaturated, s.a * mask);
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
        default: outColor = s;  // passthrough
    }
}
