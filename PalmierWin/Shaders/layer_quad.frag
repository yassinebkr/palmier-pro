#version 450

layout(location = 0) in vec2 fragUV;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D src;

// Same push-constant block as the vertex stage (single shared declaration).
layout(push_constant) uniform Layer {
    float a, b, c, d, tx, ty;
    float opacity;
} u;

void main() {
    vec4 c = texture(src, fragUV);
    float a = c.a * u.opacity;
    outColor = vec4(c.rgb * a, a);
}
