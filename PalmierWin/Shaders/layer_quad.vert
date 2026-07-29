#version 450

// Single push-constant block shared with the fragment stage. The CPU uploads
// one struct per draw: { a, b, c, d, tx, ty, opacity }. Layout matches
// PalmierCore.Mat3's effective affine `(x, y) -> (a*x + c*y + tx, b*x + d*y + ty)`
// so placement is byte-identical to the macOS CoreImage path.
layout(push_constant) uniform Layer {
    float a, b, c, d, tx, ty;
    float opacity;
} u;

// Unit quad corner in [0,1] x [0,1]. The CPU's placement matrix already maps
// this into normalized device coords [0,1] with top-left origin; this shader
// converts to Vulkan clip space [-1,1] with Y flipped.
vec2 positions[4] = vec2[](
    vec2(0.0, 0.0),
    vec2(1.0, 0.0),
    vec2(0.0, 1.0),
    vec2(1.0, 1.0)
);

layout(location = 0) out vec2 fragUV;

void main() {
    vec2 unit = positions[gl_VertexIndex];
    float px = u.a * unit.x + u.c * unit.y + u.tx;
    float py = u.b * unit.x + u.d * unit.y + u.ty;
    fragUV = unit;
    gl_Position = vec4(px * 2.0 - 1.0, 1.0 - py * 2.0, 0.0, 1.0);
}
