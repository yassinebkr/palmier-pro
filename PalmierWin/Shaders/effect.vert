#version 450

// Full-screen triangle for effect passes. Same geometry as textured_quad.vert:
// a single triangle covering the screen, UVs [0,1].
vec2 positions[3] = vec2[](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
vec2 uvs[3] = vec2[](vec2(0.0, 0.0), vec2(2.0, 0.0), vec2(0.0, 2.0));

layout(location = 0) out vec2 fragUV;

void main() {
    fragUV = uvs[gl_VertexIndex];
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
}
