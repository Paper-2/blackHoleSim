#version 450
// objective is to make the dust look gassy 
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;
layout(location = 3) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform UniformBufferObject {
    mat4 model;
    mat4 view;
    mat4 proj;
} ubo;

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec2 fragTexCoord;

void main() {
    gl_Position = ubo.proj * ubo.view * ubo.model * vec4(inPosition, 1.0);
    
    // Calculate point size based on distance from camera
    float dist = length((ubo.view * ubo.model * vec4(inPosition, 1.0)).xyz);
    gl_PointSize = max(2.0, 500.0 / dist); // Scale point size with distance
    
    fragColor = inColor;
    fragTexCoord = inTexCoord;
}