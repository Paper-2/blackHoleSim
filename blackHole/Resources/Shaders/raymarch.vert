#version 450
#extension GL_ARB_shader_draw_parameters : enable

layout(location = 0) out vec2 fragUV;

// Ray marching doesn't need vertex attributes, so we generate a full-screen quad procedurally.
// Using gl_VertexID to create two triangles that cover the screen.
void main() {
    // Full-screen quad vertices

    vec2 positions[4] = vec2[](
        vec2(-1.0, -1.0),
        vec2(1.0, -1.0),
        vec2(-1.0,  1.0),
        vec2(1.0,  1.0));

    vec2 uvs[4] = vec2[](
        vec2(0.0, 0.0),
        vec2(1.0, 0.0),
        vec2(0.0, 1.0),
        vec2(1.0, 1.0));

    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
    fragUV = uvs[gl_VertexIndex];
}