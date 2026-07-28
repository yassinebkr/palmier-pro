#version 450

// Full-screen triangle: no vertex buffer. gl_VertexIndex ∈ {0,1,2} maps to a
// triangle that covers the screen (large triangle clipped to the viewport).
// UVs flip Y so the sampled image (top-left origin) lands right-side up.
vec2 positions[3] = vec2[](
    vec2(-1.0, -1.0),
    vec2( 3.0, -1.0),
    vec2(-1.0,  3.0)
);
vec2 uvs[3] = vec2[](
    vec2(0.0, 0.0),
    vec2(2.0, 0.0),
    vec2(0.0, 2.0)
);

layout(location = 0) out vec2 fragUV;

// Push constant: a normalized source-rect (sub-rectangle of the texture to show)
// and an output scale/offset in clip space. MVP keeps it minimal: full-frame.
void main() {
    fragUV = uvs[gl_VertexIndex];
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
}
