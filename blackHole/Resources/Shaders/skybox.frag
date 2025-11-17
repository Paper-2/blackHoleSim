#version 450

layout(location = 0) in vec3 fragTexCoord;

layout(set = 0, binding = 1) uniform sampler2D texSampler;

layout(location = 0) out vec4 outColor;

void main() {
    // Simple starfield effect using position-based procedural generation
    vec3 dir = normalize(fragTexCoord);
    
    // Create stars using noise-like function
    float star = 0.0;
    for (int i = 0; i < 5; i++) {
        vec3 offset = vec3(float(i) * 123.456, float(i) * 789.012, float(i) * 345.678);
        vec3 p = dir * 1000.0 + offset;
        float noise = fract(sin(dot(p, vec3(12.9898, 78.233, 45.164))) * 43758.5453);
        if (noise > 0.998) {
            star = max(star, (noise - 0.998) * 500.0);
        }
    }
    
    // Dark space background with slight blue tint
    vec3 spaceColor = vec3(0.01, 0.01, 0.05);
    vec3 starColor = vec3(1.0, 1.0, 0.95);
    
    vec3 finalColor = mix(spaceColor, starColor, star);
    outColor = vec4(finalColor, 1.0);
}
