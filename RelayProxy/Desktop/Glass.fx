// Liquid glass refraction. Compile: scripts/compile-shader.ps1 (fxc /T ps_3_0).
// The input is the wallpaper drawn around the element with a margin, so the rim can bend light in from outside.
sampler2D input : register(s0);
float4 geo : register(c0);     // input width, input height, margin, corner radius (DIP)
float4 optics : register(c1);  // refraction (DIP), rim thickness (DIP), chromatic spread, magnification
float4 light : register(c2);   // pointer x, y (0..1), pointer glow, rim light
float4 tone : register(c3);    // vibrancy, brightness, frost radius (DIP), unused

float box(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

float3 at(float2 uv) { return tex2Dlod(input, float4(uv, 0, 0)).rgb; }

// Frost: an 8-tap golden-angle disk around the centre sample (the centre is returned through
// `centre` so the caller does not fetch it twice); radius 0 keeps the glass perfectly clear.
float3 frost(float2 uv, float radius, float2 inSize, float3 centre)
{
    float3 sum = centre;
    [unroll] for (int i = 0; i < 8; i++)
    {
        float a = i * 2.39996;
        float2 o = float2(cos(a), sin(a)) * sqrt((i + 0.5) / 8.0) * radius / inSize;
        sum += at(uv + o);
    }
    return sum / 9;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 inSize = geo.xy;
    float m = geo.z;
    float2 size = inSize - 2 * m;
    float2 b = size * 0.5;
    float r = min(geo.w, min(b.x, b.y));
    float2 c = uv * inSize - m - b;
    float d = box(c, b, r);
    float coverage = saturate(0.5 - d);
    if (coverage <= 0) return float4(0, 0, 0, 0);

    float e = 0.75;
    float2 n = float2(box(c + float2(e, 0), b, r) - box(c - float2(e, 0), b, r),
                      box(c + float2(0, e), b, r) - box(c - float2(0, e), b, r));
    n /= max(length(n), 0.0001);

    // Height of a rounded slab: flat in the middle, falling off steeply at the rim.
    float x = saturate(-d / max(optics.y, 1));
    float bend = pow(1 - x, 2.4);
    float2 shift = n * bend * optics.x;
    float2 q = c * (1 - optics.w) + shift;
    float2 p = (q + b + m) / inSize;
    float2 spread = n * bend * optics.x * optics.z / inSize;

    // Frost body, then split red and blue along the rim normal for a hint of dispersion.
    float3 sharp = at(p);
    float3 col = tone.z > 0.5 ? frost(p, tone.z, inSize, sharp) : sharp;
    col.r += at(p - spread).r - sharp.r;
    col.b += at(p + spread).b - sharp.b;

    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(luma.xxx, col, tone.x) * tone.y;

    // Light catching the curved rim: strong from the top left, a cooler bounce on the opposite side.
    float2 sun = normalize(float2(-0.55, -0.83));
    float rim = pow(1 - x, 3.5);
    col += rim * (saturate(dot(n, sun)) * 0.55 + saturate(dot(n, -sun)) * 0.22) * light.w;

    float2 pointer = (uv * inSize - m) / max(size, 1) - light.xy;
    pointer.x *= size.x / max(size.y, 1);
    col += light.z * 0.10 * exp(-dot(pointer, pointer) * 5);

    return float4(saturate(col), 1) * coverage;
}
