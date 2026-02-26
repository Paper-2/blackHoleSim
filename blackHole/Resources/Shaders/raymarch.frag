#version 450

layout(location = 0) in vec2 fragUV;
layout(location = 0) out vec4 outColor;

layout(binding = 0) uniform sampler2D uSkybox;
layout(binding = 1) uniform sampler2D uNoise;

const uint DISTANCE = 1000;

layout(std140, binding = 2) uniform RayMarchUBO {
    mat4 uInvViewMatrix;
    vec3 uCameraPos;
    float _pad0;
    vec4 uFrustumCorners[4];
    vec4 uAccretionDiskColor;
    vec4 uBlackHoleColor;
    vec2 uResolution;
    vec2 _pad1;
    float uTime;
    float uSchwarzschildRadius;
    float uSpaceDistortion;
    float uAccretionDiskThickness;
    vec4 uOrbitSpherePosRadius;
    vec4 uOrbitSphereColor;
} ubo;

// functions here were taken from shadertoys, and other tutorials. Or something.

float GetSpaceDistortionLerpValue(
    float schwarzschildRadius,
    float distanceToSingularity,
    float spaceDistortion) {
    return pow(schwarzschildRadius, spaceDistortion)
    / pow(distanceToSingularity, spaceDistortion);
}

vec3 getSkyColor(vec3 rd) {
    vec3 dir = normalize(rd);
    vec2 uv = vec2(atan(dir.z, dir.x), asin(dir.y));
    uv *= vec2(0.1591, 0.3183);
    uv += 0.5;
    uv.y = 1.0 - uv.y;
    return texture(uSkybox, uv).rgb;
}

vec3 shadeDisk(vec3 pos) {
    const float PI = 3.14159265359;

    // yeah...
    float radial = length(pos.xz);
    float inner = ubo.uSchwarzschildRadius * 1.8;
    float outer = ubo.uSchwarzschildRadius * 9.0;
    float t = clamp((radial - inner) / max(outer - inner, 0.001), 0.0, 1.0);

    vec3 innerColor = vec3(1.00, 0.95, 0.85);
    vec3 outerColor = vec3(1.0, 0.45, 0.05);
    vec3 baseColor = mix(innerColor, outerColor, t);

    float angle = atan(pos.z, pos.x);
    float normalizedAngle = angle * (0.5 / PI) + 0.5; 
    vec2 baseUv = pos.xz * 0.015;
    float timeOffset = ubo.uTime * 0.08;
    vec2 swirlDir = radial > 0.0001 ? normalize(vec2(-pos.z, pos.x)) : vec2(1.0, 0.0);
    vec2 swirlUv = baseUv + swirlDir * radial * 0.002;

    float n1 = texture(uNoise, swirlUv + vec2(timeOffset, -timeOffset)).r;
    float n2 = texture(uNoise, baseUv * 2.1 - swirlDir * 0.4 + vec2(-timeOffset * 1.4, timeOffset * 0.9)).g;
    float n3 = texture(uNoise, vec2(normalizedAngle * 3.0, radial * 0.006) + vec2(timeOffset * 0.6, -timeOffset * 0.3)).b;

    float density = clamp(0.55 * n1 + 0.35 * n2 + 0.45 * n3, 0.0, 1.0);
    density = smoothstep(0.25, 0.8, density);

    float swirlBand = sin(angle * 10.0 - timeOffset * 20.0) * 0.5 + 0.5;
    float verticalGlow = exp(-abs(pos.y) / (ubo.uAccretionDiskThickness * 0.75 + 0.0005));
    float cloudMix = clamp(density * 0.7 + swirlBand * 0.2 + verticalGlow * 0.2, 0.0, 1.0);

    vec3 highlightColor = mix(vec3(1.15, 1.05, 0.95), vec3(1.05, 0.55, 0.25), t);
    vec3 shadowColor = baseColor * 0.35;

    return clamp(mix(shadowColor, highlightColor * (0.8 + verticalGlow * 0.4), cloudMix), 0.0, 1.5);
}

float computeDiskOpacity(float radial, float innerRadius, float outerRadius) {
    float extent = max(outerRadius - innerRadius, 0.0001);
    float normalized = clamp((radial - innerRadius) / extent, 0.0, 1.0);
    float edgeBlend = smoothstep(0.5, 1.0, normalized);
    edgeBlend = pow(edgeBlend, 0.35);
    float noiseSample = texture(uNoise, vec2(radial * 0.01, normalized * 8.0 + ubo.uTime * 0.1)).r;
    float noiseMod = mix(0.6, 1.4, noiseSample);
    float fade = mix(1.0, 0.0, edgeBlend);
    float innerRamp = smoothstep(0.0, 0.18, normalized);
    float baseOpacity = mix(0.0, fade, innerRamp);
    return clamp(baseOpacity * noiseMod, 0.0, 1.0);
}

// Distort the ray direction based on gravitational influence.
vec3 DistortRay(vec3 pos, vec3 prevDir, float stepSize) {
    vec3 blackHolePosition = vec3(0.0);

    float d = distance(blackHolePosition, pos);
    float l = GetSpaceDistortionLerpValue(ubo.uSchwarzschildRadius, d, ubo.uSpaceDistortion);
    vec3 straight = normalize(prevDir);
    vec3 inward = normalize(blackHolePosition - pos);
    vec3 blended = mix(straight, inward, l);
    return normalize(blended) * stepSize;
}

// Reduce cloud banding by shrinking the step size close to strong curvature or dense disk regions.
float ComputeAdaptiveStep(
    vec3 pos,
    vec3 dir,
    float baseStep,
    float schwarzschildRadius,
    float halfThickness) {
    float distanceToSingularity = max(length(pos), 0.0001);
    float gravityStrength = clamp((schwarzschildRadius * 2.5) / distanceToSingularity, 0.0, 1.0);

    float radial = length(pos.xz);
    float innerRadius = schwarzschildRadius * 3.0;
    float outerRadius = schwarzschildRadius * 9.0;
    float radialFadeIn = smoothstep(innerRadius - schwarzschildRadius * 0.5, innerRadius + schwarzschildRadius * 0.5, radial);
    float radialFadeOut = smoothstep(outerRadius - schwarzschildRadius * 0.5, outerRadius + schwarzschildRadius * 0.5, radial);
    float radialInfluence = clamp(radialFadeIn - radialFadeOut, 0.0, 1.0);

    float verticalInfluence = 1.0 - clamp(abs(pos.y) / (halfThickness * 2.5 + 0.0001), 0.0, 1.0);
    float diskInfluence = clamp(radialInfluence * verticalInfluence, 0.0, 1.0);

    float tangentFactor = 1.0 - abs(dot(normalize(dir), vec3(0.0, 1.0, 0.0)));
    float grazingInfluence = smoothstep(0.2, 0.9, tangentFactor);

    float gravityScale = mix(2.0, 0.4, gravityStrength);
    float diskScale = mix(1.5, 0.35, diskInfluence);
    float grazingScale = mix(1.0, 0.6, grazingInfluence * diskInfluence);
    float stepScale = min(gravityScale, min(diskScale, grazingScale));

    float stepMin = baseStep * 0.2;
    float stepMax = baseStep * 3.0;
    return clamp(baseStep * stepScale, stepMin, stepMax);
}

vec4 raymarch(vec3 ro, vec3 rd) {
    const int maxstep = 1000;
    vec3 previousPos = ro;
    float r = ubo.uSchwarzschildRadius;
    float baseStep = DISTANCE / float(maxstep);
    float traveled = 0.0;

    vec3 previousRayDir = rd;

    float distanceToSingularity = 99999999.0;
    float blackHoleInfluence = 0.0;
    float diskMask = 0.0;
    vec3 diskColor = vec3(0.0);
    float diskHitDistance = -1.0;
    float orbitSphereMask = 0.0;
    vec3 orbitSphereColor = vec3(0.0);
    float orbitSphereHitDistance = -1.0;
    float halfThickness = max(ubo.uAccretionDiskThickness, 0.001);

    bool hasBacktrace = false;
    float nextStepOverride = -1.0;
    float innerRadius = ubo.uSchwarzschildRadius * 3.0;
    float outerRadius = ubo.uSchwarzschildRadius * 9.0;

    for (int i = 0; i < maxstep; ++i) {
        distanceToSingularity = length(previousPos);
        if (distanceToSingularity < ubo.uSchwarzschildRadius) {
            blackHoleInfluence = 1.0;
            break;
        }

        if (traveled > DISTANCE) {
            break;
        }

        vec3 startPos = previousPos;
        float stepStartDistance = traveled;
        float adaptiveStep = nextStepOverride > 0.0
        ? nextStepOverride
        : ComputeAdaptiveStep(startPos, previousRayDir, baseStep, r, halfThickness);
        nextStepOverride = -1.0;

        vec3 stepVec = DistortRay(startPos, previousRayDir, adaptiveStep);
        vec3 stepDir = length(stepVec) > 0.0 ? normalize(stepVec) : previousRayDir;
        vec3 newPos = startPos + stepVec;

        vec3 orbitToPoint = newPos - ubo.uOrbitSpherePosRadius.xyz;
        float orbitSphereRadius = ubo.uOrbitSpherePosRadius.w;
        float orbitDistance = length(orbitToPoint);
        if (orbitDistance <= orbitSphereRadius + adaptiveStep * 0.5) {
            vec3 normal = orbitDistance > 1e-5 ? normalize(orbitToPoint) : vec3(0.0, 1.0, 0.0);
            vec3 lightDir = normalize(vec3(0.25, 0.9, 0.35));
            float diffuse = max(dot(normal, lightDir), 0.18);
            float rim = pow(1.0 - max(dot(normal, -stepDir), 0.0), 3.0);
            orbitSphereColor = ubo.uOrbitSphereColor.rgb * (diffuse + rim * 0.35);
            orbitSphereMask = 1.0;
            orbitSphereHitDistance = stepStartDistance + adaptiveStep;
            previousPos = newPos;
            previousRayDir = stepDir;
            break;
        }

        float postDistance = length(newPos);
        if (postDistance < ubo.uSchwarzschildRadius) {
            blackHoleInfluence = 1.0;
            previousPos = newPos;
            previousRayDir = stepDir;
            break;
        }

        float radial = length(newPos.xz);
        bool insideDisk =
        abs(newPos.y) <= halfThickness
        && radial > innerRadius
        && radial < outerRadius;

        if (insideDisk && !hasBacktrace) {
            hasBacktrace = true;
            float refinedStep = adaptiveStep * 0.25;
            float backtrack = refinedStep * 3.0;
            float availableBackward = max(traveled, 0.0);
            float actualBacktrack = clamp(backtrack, 0.0, availableBackward);
            previousPos = startPos - stepDir * actualBacktrack;
            traveled = max(traveled - actualBacktrack, 0.0);
            nextStepOverride = refinedStep;
            previousRayDir = stepDir;
            continue;
        }

        traveled += adaptiveStep;

        if (insideDisk) {
            float opacity = computeDiskOpacity(radial, innerRadius, outerRadius);
            float hitDistance = stepStartDistance + adaptiveStep;
            if (diskHitDistance < 0.0 || hitDistance < diskHitDistance) {
                diskColor = shadeDisk(newPos);
                diskMask = opacity;
                diskHitDistance = hitDistance;
            }
        } else if (diskMask < 1.0) {
            float prevHeight = startPos.y;
            float newHeight = newPos.y;
            float heightDelta = newHeight - prevHeight;
            if (
                (abs(prevHeight) <= halfThickness + adaptiveStep
                    || abs(newHeight) <= halfThickness + adaptiveStep)
                && abs(heightDelta) > 1e-5) {
                float t = clamp(-prevHeight / heightDelta, 0.0, 1.0);
                vec3 segment = newPos - startPos;
                vec3 interceptPos = startPos + segment * t;
                float interceptRadial = length(interceptPos.xz);
                bool hitsDisk =
                abs(interceptPos.y) <= halfThickness
                && interceptRadial > innerRadius
                && interceptRadial < outerRadius;
                if (hitsDisk) {
                    float opacity = computeDiskOpacity(interceptRadial, innerRadius, outerRadius);
                    float hitDistance = stepStartDistance + adaptiveStep * t;
                    if (diskHitDistance < 0.0 || hitDistance < diskHitDistance) {
                        diskColor = shadeDisk(interceptPos);
                        diskMask = opacity;
                        diskHitDistance = hitDistance;
                    }
                }
            }
        }

        previousPos = newPos;
        previousRayDir = stepDir;
    }

    vec3 skyColor = getSkyColor(previousRayDir);
    vec3 bhColor = ubo.uBlackHoleColor.rgb;
    vec3 background = mix(skyColor, bhColor, blackHoleInfluence);

    vec3 finalColor = background;
    if (diskHitDistance >= 0.0) {
        finalColor = mix(finalColor, diskColor, diskMask);
    }
    if (orbitSphereMask > 0.5 && (diskHitDistance < 0.0 || orbitSphereHitDistance < diskHitDistance)) {
        finalColor = orbitSphereColor;
    }
    finalColor = clamp(finalColor, 0.0, 1.0);

    return vec4(finalColor, 1.0);
}

void main() {
    vec3 color = vec3(0.0);
    float samples = 1.0;

    vec2 offsets[16] = vec2[](
        vec2(-0.25, -0.25),
        vec2(0.25, -0.25),
        vec2(-0.25, 0.25),
        vec2(0.25, 0.25),
        vec2(-0.75, -0.75),
        vec2(-0.25, -0.75),
        vec2(0.25, -0.75),
        vec2(0.75, -0.75),
        vec2(-0.75, -0.25),
        vec2(0.75, -0.25),
        vec2(-0.75, 0.25),
        vec2(0.75, 0.25),
        vec2(-0.75, 0.75),
        vec2(-0.25, 0.75),
        vec2(0.25, 0.75),
        vec2(0.75, 0.75));

    for (int i = 0; i < samples; ++i) {
        vec2 uv = fragUV + offsets[i] / ubo.uResolution;

        vec3 top = mix(ubo.uFrustumCorners[0].xyz, ubo.uFrustumCorners[1].xyz, uv.x);
        vec3 bottom = mix(ubo.uFrustumCorners[3].xyz, ubo.uFrustumCorners[2].xyz, uv.x);
        vec3 viewRay = mix(top, bottom, uv.y);

        vec3 worldRay = (ubo.uInvViewMatrix * vec4(viewRay, 0.0)).xyz;
        worldRay = normalize(worldRay);

        color += raymarch(ubo.uCameraPos, worldRay).rgb;
    }

    outColor = vec4(color / float(samples), 1.0);
}