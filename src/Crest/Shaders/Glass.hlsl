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
float smin(float a, float b, float k)
{
    float h = saturate(0.5 + 0.5 * (b - a) / k);
    return lerp(b, a, h) - k * h * (1 - h);
}

// Depth inside a rounded box with no crease and no pinch: a smooth minimum of the distances to the
// nearest side and to the nearest top/bottom. The exact rounded box measures depth from the corner
// arc's centre near a corner, which pinches into a point and a diagonal line; this blends instead.
// With k = 1.17 r its zero line sits on the real corner curve (within a pixel).
float sdBoxSoft(float2 p, float2 c, float2 he, float r)
{
    float2 a = he - abs(p - c);
    return -smin(a.x, a.y, max(1.17 * r, 2));
}

// k = 0 gives the exact outline. k > 0 gives a rounder, softer version of it whose inside has no
// creases: an exact rounded box has a sharp crease running in from every corner (where "nearest
// edge" flips from the side to the top), and the lens followed that crease as a hard diagonal line.
float sdShapeK(float2 p, float2 c, float2 he, float r, float k)
{
    float d = k > 0 ? sdBoxSoft(p, c, he, r) : sdBox(p, c, he, r);
    float s = misc.z;
    if (s > 0.01)
    {
        float bottom = misc.w + s;
        float2 wc = float2(c.x, (bottom - 400) * 0.5);
        float2 whe = float2(he.x + s, (bottom + 400) * 0.5);
        float2 q = float2(abs(p.x - c.x), p.y);
        float dCircle = length(q - float2(he.x + s, bottom)) - s;
        if (k > 0)
        {
            d = smin(d, sdBox(p, wc, whe, 0), k);
            d = -smin(-d, dCircle, k);
        }
        else
        {
            d = min(d, sdBox(p, wc, whe, 0));
            d = max(d, -dCircle);
        }
    }
    return d;
}

float sdShape(float2 p, float2 c, float2 he, float r) { return sdShapeK(p, c, he, r, 0); }

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = sizeLens.xy;
    float2 px = uv * size;
    float2 c = (box.xy + box.zw) * 0.5;
    float2 he = (box.zw - box.xy) * 0.5;
    float r = look.x;

    // The exact outline places the rim; a softened copy steers the lens, so the bend and the frost
    // flow round the corners instead of meeting in a seam.
    float d = sdShape(px, c, he, r);
    float ks = sizeLens.z * 0.6;
    float ds = sdShapeK(px, c, he, r, ks);
    float e = 1.5;
    float2 n = float2(sdShapeK(px + float2(e, 0), c, he, r, ks) - sdShapeK(px - float2(e, 0), c, he, r, ks),
                      sdShapeK(px + float2(0, e), c, he, r, ks) - sdShapeK(px - float2(0, e), c, he, r, ks));
    n /= max(length(n), 0.0001);

    // 0 at the edge, 1 once past the lens band.
    float inside = max(-d, 0);
    float t = saturate(max(-ds, 0) / sizeLens.z);
    float k = 1 - t;
    float bend = sizeLens.w * k * k;
    float2 off = n * bend / size;

    // How far into the glass we are, over a wide band: drives a gradual blur, then the frost.
    float tf = saturate(max(-ds, 0) / (sizeLens.z * 2.4));
    float ease = tf * tf * (3 - 2 * tf);

    // The edge shows what lies just beyond it, a little split by colour like real glass.
    float f = misc.y * k;
    float2 uvc = uv + off;
    float3 col;
    col.r = tex2D(input, uv + off * (1 + f)).r;
    col.g = tex2D(input, uvc).g;
    col.b = tex2D(input, uv + off * (1 - f)).b;

    // The picture goes soft little by little: a ring of samples whose radius grows with depth,
    // so the clear edge melts into the frost instead of meeting it at a line.
    float rad = 9 * smoothstep(0, 0.7, tf);
    float2 st = rad / size;
    float3 ring = 0;
    ring += tex2D(input, uvc + st * float2( 1.000,  0.000)).rgb;
    ring += tex2D(input, uvc + st * float2( 0.707,  0.707)).rgb;
    ring += tex2D(input, uvc + st * float2( 0.000,  1.000)).rgb;
    ring += tex2D(input, uvc + st * float2(-0.707,  0.707)).rgb;
    ring += tex2D(input, uvc + st * float2(-1.000,  0.000)).rgb;
    ring += tex2D(input, uvc + st * float2(-0.707, -0.707)).rgb;
    ring += tex2D(input, uvc + st * float2( 0.000, -1.000)).rgb;
    ring += tex2D(input, uvc + st * float2( 0.707, -0.707)).rgb;
    ring += tex2D(input, uvc + st * float2( 0.383,  0.924) * 0.5).rgb;
    ring += tex2D(input, uvc + st * float2(-0.924,  0.383) * 0.5).rgb;
    ring += tex2D(input, uvc + st * float2(-0.383, -0.924) * 0.5).rgb;
    ring += tex2D(input, uvc + st * float2( 0.924, -0.383) * 0.5).rgb;
    col = lerp(col, (col + ring) / 13, smoothstep(0, 0.5, tf));

    // Then the frost takes over, with no step anywhere.
    float3 soft = tex2D(frost, uv + off * 0.5).rgb;
    col = lerp(col, soft, misc.x * lerp(0.1, 1, ease));

    // Richer colour, then darkened so white text stays readable; the lens edge stays a bit brighter.
    float l = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = lerp(l.xxx, col, look.z);
    col *= 1 - look.y * lerp(0.62, 1, ease);

    // Light catching the rim: brightest where the edge faces up-left or down-right.
    float rim = pow(saturate(1 - inside / 2.6), 2);
    float facing = 0.35 + 0.65 * abs(dot(n, float2(-0.55, 0.835)));
    col += look.w * (rim * facing + 0.05 * k * k);

    return float4(saturate(col), 1);
}
