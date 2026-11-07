// Liquid glass for the open island.
// s0 is a picture of the screen behind the island, s1 the same picture softened.
// Near the edge the glass works like a thick lens: it pulls in what lies just outside
// and bends it, with a touch of colour fringing and a bright rim. Inside it is frosted.
// Compile with Shaders\compile.ps1 (writes Glass.ps next to this file).

sampler2D input : register(s0);
sampler2D frost : register(s1);

float4 sizeLens : register(c0); // x, y = element size (DIP); z = lens band width; w = how far the edge reaches out
float4 box      : register(c1); // island body: left, top, right, bottom (DIP)
float4 look     : register(c2); // x = corner radius; y = darken (0..1); z = saturation; w = rim brightness
float4 misc     : register(c3); // x = frost (0..1); y = colour fringing; z = shoulder size; w = top of the island (DIP)

float sdBox(float2 p, float2 c, float2 he, float r)
{
    float2 q = abs(p - c) - he + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

// The island's real outline, the same one the window draws: the rounded body plus, in notch
// style, the two concave shoulders that flare into the screen edge. Built as one solid piece:
// the body joined with a strip as wide as body + shoulders along the top, minus the two circles
// that carve the shoulder curves (BuildOutline's curve). So the body's straight side never
// counts as an edge up there: the curve is the only edge, and the lens and frost follow it.
float sdShape(float2 p, float2 c, float2 he, float r)
{
    float d = sdBox(p, c, he, r);
    float s = misc.z;
    if (s > 0.01)
    {
        float bottom = misc.w + s;
        float2 wc = float2(c.x, (bottom - 400) * 0.5);
        float2 whe = float2(he.x + s, (bottom + 400) * 0.5);
        d = min(d, sdBox(p, wc, whe, 0));
        float2 q = float2(abs(p.x - c.x), p.y);
        float dCircle = length(q - float2(he.x + s, bottom)) - s;
        d = max(d, -dCircle);
    }
    return d;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = sizeLens.xy;
    float2 px = uv * size;
    float2 c = (box.xy + box.zw) * 0.5;
    float2 he = (box.zw - box.xy) * 0.5;
    float r = look.x;

    float d = sdShape(px, c, he, r);
    float e = 0.75;
    float2 n = float2(sdShape(px + float2(e, 0), c, he, r) - sdShape(px - float2(e, 0), c, he, r),
                      sdShape(px + float2(0, e), c, he, r) - sdShape(px - float2(0, e), c, he, r));
    n /= max(length(n), 0.0001);

    // 0 at the edge, 1 once past the lens band.
    float inside = max(-d, 0);
    float t = saturate(inside / sizeLens.z);
    float k = 1 - t;
    float bend = sizeLens.w * k * k;
    float2 off = n * bend / size;

    // The edge shows what lies just beyond it, a little split by colour like real glass.
    float f = misc.y * k;
    float3 col;
    col.r = tex2D(input, uv + off * (1 + f)).r;
    col.g = tex2D(input, uv + off).g;
    col.b = tex2D(input, uv + off * (1 - f)).b;

    // Inside, the frost takes over.
    float3 soft = tex2D(frost, uv + off * 0.5).rgb;
    col = lerp(col, soft, misc.x * lerp(0.3, 1, smoothstep(0, 1, t)));

    // Richer colour, then darkened so white text stays readable; the lens edge stays a bit brighter.
    float l = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = lerp(l.xxx, col, look.z);
    col *= 1 - look.y * lerp(0.62, 1, t);

    // Light catching the rim: brightest where the edge faces up-left or down-right.
    float rim = pow(saturate(1 - inside / 2.6), 2);
    float facing = 0.35 + 0.65 * abs(dot(n, float2(-0.55, 0.835)));
    col += look.w * (rim * facing + 0.05 * k * k);

    return float4(saturate(col), 1);
}
