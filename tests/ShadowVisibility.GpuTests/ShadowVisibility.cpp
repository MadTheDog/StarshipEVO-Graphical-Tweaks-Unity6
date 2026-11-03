#include "../GlassSurface.GpuTests/FixtureResources.h"
#include <algorithm>
#include <functional>
#include <limits>

struct Case
{
    std::string name;
    std::array<F4, 25> metadata{};
    std::array<float, 16> atlas{};
    std::vector<float> largeAtlas;
    F4 position{}, camera{}, frame{54321, 123, 0, 0};
    float token = 17, ready = 1, distance = 0, bias = 0, expected = 0;
    U expectedStatus = 3;
    U atlasWidth = 4, atlasHeight = 4;
};

static float Depth(bool reversed, float physical) { return reversed ? 1.f - physical : physical; }
static void Matrix(Case& test, U cascade, float u, float v, float physical, bool reversed)
{
    test.metadata[cascade * 4] = F4{0, 0, 0, u};
    test.metadata[cascade * 4 + 1] = F4{0, 0, 0, v};
    test.metadata[cascade * 4 + 2] = F4{0, 0, 0, Depth(reversed, physical)};
    test.metadata[cascade * 4 + 3] = F4{0, 0, 0, 1};
}
static Case Base(bool reversed)
{
    Case test;
    for (U i = 0; i < 4; ++i) Matrix(test, i, .5f, .5f, .6f, reversed);
    test.metadata[20] = F4{1, 1, 1, 1};
    test.metadata[21] = F4{4, 4, 1, 1};
    test.metadata[23] = F4{54321, 123, 17, 1234};
    test.metadata[24] = F4{54321, 123, 17, 4321};
    test.atlas.fill(Depth(reversed, .4f));
    return test;
}
static void Layout(Case& test, U cascades, bool reversed)
{
    test.atlasHeight = cascades == 2 ? 2 : 4;
    test.metadata[21] = F4{4, float(test.atlasHeight), float(cascades), 1};
    for (U i = 0; i < cascades; ++i)
        Matrix(test, i, .25f + .5f * float(i & 1), cascades == 2 ? .5f : .25f + .5f * float(i >> 1), .6f, reversed);
}
static void LitTile(Case& test, U tile, bool reversed)
{
    U x = 2 * (tile & 1), y = test.atlasHeight == 2 ? 0 : 2 * (tile >> 1);
    for (U dy = 0; dy < 2; ++dy) for (U dx = 0; dx < 2; ++dx)
        test.atlas[(y + dy) * 4 + x + dx] = Depth(reversed, .8f);
}
static Resource Image(ID3D11Device* device, U width, U height, DXGI_FORMAT format, U stride, const void* values)
{
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = width; desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1; desc.Format = format; desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = values; initial.SysMemPitch = width * stride;
    ComPtr<ID3D11Texture2D> texture; Check(device->CreateTexture2D(&desc, &initial, &texture));
    Resource result; Check(texture.As(&result.resource));
    Check(device->CreateShaderResourceView(texture.Get(), nullptr, &result.view)); return result;
}
static std::vector<Case> Cases(bool reversed)
{
    std::vector<Case> tests;
    auto add = [&](const char* name, float expected, const std::function<void(Case&)>& change)
    {
        Case test = Base(reversed); test.name = name; test.expected = expected; change(test); tests.push_back(test);
    };
    add("receiver behind blocker", 0, [](Case&) {});
    add("receiver before blocker", 1, [&](Case& t) { t.atlas.fill(Depth(reversed, .8f)); });
    add("equal physical depth is lit", 1, [&](Case& t) { t.atlas.fill(Depth(reversed, .6f)); });
    add("small depth gap blocks without bias", 0, [&](Case& t) { t.atlas.fill(Depth(reversed, .5999f)); });
    add("positive receiver bias resolves small gap", 1, [&](Case& t) { t.atlas.fill(Depth(reversed, .5999f)); t.bias = .0002f; });
    add("negative bias moves away from light", 0, [&](Case& t) { t.atlas.fill(Depth(reversed, .6001f)); t.bias = -.0002f; });
    add("quarter shadow strength leaves three quarters lit", .75f, [](Case& t) { t.metadata[21][3] = .25f; });
    add("zero shadow strength", 1, [](Case& t) { t.metadata[21][3] = 0; });
    add("strength upper clamp", 0, [](Case& t) { t.metadata[21][3] = 2; });
    add("strength lower clamp", 1, [](Case& t) { t.metadata[21][3] = -1; });
    add("four tap PCF one visible", .25f, [&](Case& t) { t.atlas[5] = Depth(reversed, .8f); });
    add("four tap PCF two visible", .5f, [&](Case& t) { t.atlas[5] = t.atlas[6] = Depth(reversed, .8f); });
    add("four tap PCF all visible", 1, [&](Case& t) { t.atlas[5] = t.atlas[6] = t.atlas[9] = t.atlas[10] = Depth(reversed, .8f); });
    add("PCF unequal weights", .1875f, [&](Case& t) { Matrix(t, 0, .4375f, .5625f, .6f, reversed); t.atlas[5] = Depth(reversed, .8f); });
    add("atlas outer corner taps clamp", 1, [&](Case& t) { Matrix(t, 0, 0, 0, .6f, reversed); t.atlas[0] = Depth(reversed, .8f); });
    add("disabled capture is lit", 1, [](Case& t) { t.ready = 0; });
    add("below ready threshold is lit", 1, [](Case& t) { t.ready = .49f; });
    add("ready threshold allows shadow", 0, [](Case& t) { t.ready = .5f; });
    add("export low frame mismatch", 1, [](Case& t) { t.metadata[23][0] -= 1; });
    add("export high frame mismatch", 1, [](Case& t) { t.metadata[23][1] += 1; });
    add("export camera mismatch", 1, [](Case& t) { t.metadata[23][2] += 1; });
    add("export marker mismatch", 1, [](Case& t) { t.metadata[23][3] = 0; });
    add("export stamp missing", 1, [](Case& t) { t.metadata[23] = F4{}; });
    add("atlas low frame mismatch", 1, [](Case& t) { t.metadata[24][0] -= 1; });
    add("atlas high frame mismatch", 1, [](Case& t) { t.metadata[24][1] += 1; });
    add("atlas camera mismatch", 1, [](Case& t) { t.metadata[24][2] += 1; });
    add("atlas marker mismatch", 1, [](Case& t) { t.metadata[24][3] = 0; });
    add("atlas stamp missing", 1, [](Case& t) { t.metadata[24] = F4{}; });
    add("expected frame mismatch", 1, [](Case& t) { t.frame[0] += 1; });
    add("expected camera mismatch", 1, [](Case& t) { t.token += 1; });
    add("zero atlas width", 1, [](Case& t) { t.metadata[21][0] = 0; });
    add("zero atlas height", 1, [](Case& t) { t.metadata[21][1] = 0; });
    add("negative U outside atlas", 1, [&](Case& t) { Matrix(t, 0, -.01f, .5f, .6f, reversed); });
    add("U above atlas", 1, [&](Case& t) { Matrix(t, 0, 1.01f, .5f, .6f, reversed); });
    add("negative V outside atlas", 1, [&](Case& t) { Matrix(t, 0, .5f, -.01f, .6f, reversed); });
    add("V above atlas", 1, [&](Case& t) { Matrix(t, 0, .5f, 1.01f, .6f, reversed); });
    add("negative depth outside atlas", 1, [](Case& t) { t.metadata[2][3] = -.01f; });
    add("depth above atlas", 1, [](Case& t) { t.metadata[2][3] = 1.01f; });
    add("zero homogeneous divisor", 1, [](Case& t) { t.metadata[3] = F4{}; });
    add("matrix rows multiply nontrivial world position", 1, [&](Case& t)
    {
        t.position = F4{.1f, .2f, .3f, 0};
        // These rows place this point at texel center (1,2). A column/row
        // transpose instead samples a different blocked texel or exits the atlas.
        t.metadata[0] = F4{.25f, .5f, .75f, .025f}; // u=.375
        t.metadata[1] = F4{.6f, -.2f, .4f, .485f}; // v=.625
        t.metadata[2] = F4{0, 0, 0, Depth(reversed, .6f)};
        t.metadata[3] = F4{.1f, .2f, .3f, .86f}; // w=1
        t.atlas[9] = Depth(reversed, .8f);
    });
    add("two cascades select second sphere", 1, [&](Case& t)
    {
        Layout(t, 2, reversed); t.position = F4{4, 0, 0, 0};
        t.metadata[17] = F4{4, 0, 0, 0}; t.metadata[20] = F4{1, 4, 0, 0};
        LitTile(t, 1, reversed);
    });
    add("nested spheres prefer first eligible cascade", 1, [&](Case& t)
    {
        Layout(t, 2, reversed); t.metadata[20] = F4{1, 4, 0, 0};
        LitTile(t, 0, reversed);
    });
    add("outside all cascade spheres", 1, [&](Case& t) { Layout(t, 4, reversed); t.position = F4{10, 0, 0, 0}; });
    for (U selected = 0; selected < 4; ++selected)
    {
        Case t = Base(reversed); t.name = "four cascades select index " + std::to_string(selected); t.expected = 1;
        Layout(t, 4, reversed); t.position = F4{float(selected * 3), 0, 0, 0};
        for (U i = 0; i < 4; ++i)
        {
            t.metadata[16 + i] = F4{float(i * 3), 0, 0, 0};
        }
        LitTile(t, selected, reversed); tests.push_back(t);
    }
    add("distance before fade interval stays shadowed", 0, [](Case& t) { t.distance = 10; t.position[0] = 8; });
    add("distance fade starts at nine tenths", 0, [](Case& t) { t.distance = 10; t.position[0] = 9; });
    add("distance half fade", .5f, [](Case& t) { t.distance = 10; t.position[0] = 9.5f; });
    add("distance fade reaches fully lit", 1, [](Case& t) { t.distance = 10; t.position[0] = 10; });
    add("distance after fade interval is lit", 1, [](Case& t) { t.distance = 10; t.position[0] = 12; });
    add("camera-relative distance rather than world origin", .5f, [](Case& t) { t.distance = 10; t.position[0] = 109.5f; t.camera[0] = 100; });
    add("distance fade disabled", 0, [](Case& t) { t.distance = 0; t.position[0] = 100; });
    add("strength followed by half distance fade", .875f, [](Case& t) { t.metadata[21][3] = .25f; t.distance = 10; t.position[0] = 9.5f; });
    return tests;
}

static std::vector<Case> StatusCases(bool reversed)
{
    std::vector<Case> tests;
    auto add = [&](const char* name, float expected, U status, const std::function<void(Case&)>& change)
    {
        Case test = Base(reversed); test.name = name; test.expected = expected; test.expectedStatus = status;
        change(test); tests.push_back(test);
    };
    add("status invalid unarmed", 1, 0, [](Case& t) { t.ready = 0; });
    add("status invalid stale export frame", 1, 0, [](Case& t) { t.metadata[23][0] -= 1; });
    add("status invalid export camera", 1, 0, [](Case& t) { t.metadata[23][2] += 1; });
    add("status invalid missing export marker", 1, 0, [](Case& t) { t.metadata[23][3] = 0; });
    add("status invalid missing atlas marker", 1, 0, [](Case& t) { t.metadata[24] = F4{}; });
    add("status invalid stale atlas frame", 1, 0, [](Case& t) { t.metadata[24][1] += 1; });
    add("status invalid zero metadata dimensions", 1, 0, [](Case& t) { t.metadata[21][0] = 0; });
    add("status outside cascade spheres", 1, 1, [&](Case& t) { Layout(t, 4, reversed); t.position[0] = 10; });
    add("status zero homogeneous W", 1, 2, [](Case& t) { t.metadata[3] = F4{}; });
    add("status outside UV", 1, 2, [&](Case& t) { Matrix(t, 0, 1.1f, .5f, .6f, reversed); });
    add("status outside projected depth", 1, 2, [](Case& t) { t.metadata[2][3] = -1; });
    add("status sampled fully blocked", 0, 3, [](Case&) {});
    add("status sampled physically lit", 1, 3, [&](Case& t) { t.atlas.fill(Depth(reversed, .8f)); });
    add("status sampled partial PCF", .25f, 3, [&](Case& t) { t.atlas[5] = Depth(reversed, .8f); });
    add("status sampled partial distance fade", .5f, 3, [](Case& t) { t.distance = 10; t.position[0] = 9.5f; });
    add("status full distance fade at limit", 1, 4, [](Case& t) { t.distance = 10; t.position[0] = 10; });
    add("status full distance fade beyond limit", 1, 4, [](Case& t) { t.distance = 10; t.position[0] = 20; });
    add("status invalid metadata before outside cascade", 1, 0, [](Case& t) { t.metadata[23][3] = 0; t.metadata[21][2] = 4; t.position[0] = 20; });
    add("status outside cascade before distance fade", 1, 1, [&](Case& t) { Layout(t, 4, reversed); t.position[0] = 20; t.distance = 10; });
    return tests;
}

static void ReceiverUV(Case& test, U selected, float u, float v)
{
    for (U i = 0; i < U(test.metadata[21][2]); ++i) test.metadata[16 + i] = F4{float(i * 4), 0, 0, 0};
    float x = float(selected * 4); test.position = F4{x + .5f, 0, 0, 0};
    float centerU = test.metadata[selected * 4][3], centerV = test.metadata[selected * 4 + 1][3];
    float sx = 2 * (u - centerU), sy = 2 * (v - centerV);
    test.metadata[selected * 4] = F4{sx, 0, 0, centerU - sx * x};
    test.metadata[selected * 4 + 1] = F4{sy, 0, 0, centerV - sy * x};
}
static Case ExactNative(bool reversed)
{
    Case t = Base(reversed); const float inactive = -std::numeric_limits<float>::infinity();
    t.metadata = {{
        F4{-.00167015393f,-2.75828561e-11f,.000549664721f,.37890625f},
        F4{.000330248033f,.00335413055f,.00100345723f,.518066406f},
        F4{.000900055515f,-.000906795729f,.00273481454f,.756478548f}, F4{0,0,0,1},
        F4{-.000238278619f,-3.93520893e-12f,7.84199292e-05f,.878662109f},
        F4{4.71160456e-05f,.000478529284f,.000143161902f,.520019531f},
        F4{.000135418682f,-.000136432776f,.000411469053f,.798014164f}, F4{0,0,0,1},
        F4{0,0,0,0}, F4{0,0,0,0}, F4{0,0,0,1}, F4{0,0,0,1},
        F4{0,0,0,0}, F4{0,0,0,0}, F4{0,0,0,1}, F4{0,0,0,1},
        F4{40.9288788f,23.5295925f,-110.488571f,20020.4355f},
        F4{285.398376f,161.404892f,-775.716064f,983595.188f},
        F4{0,0,0,inactive}, F4{0,0,0,inactive}, F4{20020.4355f,983595.188f,inactive,inactive},
        F4{4096,2048,2,1}, F4{.246664345f,.585740387f,.21204932f,0},
        F4{28812,0,1,1234}, F4{28812,0,1,4321}
    }};
    if (!reversed)
        for (U cascade = 0; cascade < 2; ++cascade)
        {
            F4& z = t.metadata[cascade * 4 + 2];
            z = F4{-z[0],-z[1],-z[2],1-z[3]};
        }
    t.atlasWidth = 4096; t.atlasHeight = 2048;
    t.largeAtlas.assign(size_t(t.atlasWidth) * t.atlasHeight, reversed ? 1.f : 0.f);
    t.frame = F4{28812,0,0,0}; t.token = 1; t.camera = t.metadata[22];
    t.position = F4{2.16f,2.16f,-4.26f,0}; t.expected = 0; t.expectedStatus = 3;
    t.name = "exact native25 metadata with inactive negative-infinity spheres";
    return t;
}
static std::vector<Case> TileCases(bool reversed)
{
    std::vector<Case> tests;
    auto add = [&](const char* name, U cascades, float expected, U status, const std::function<void(Case&)>& change)
    {
        Case t = Base(reversed); Layout(t, cascades, reversed); t.name = name; t.expected = expected; t.expectedStatus = status;
        change(t); tests.push_back(t);
    };
    add("two tiles left edge does not read lit right tile", 2, 0, 3, [&](Case& t) { LitTile(t, 1, reversed); ReceiverUV(t, 0, .5f, .5f); });
    add("two tiles right edge does not read lit left tile", 2, 0, 3, [&](Case& t) { LitTile(t, 0, reversed); ReceiverUV(t, 1, .5f, .5f); });
    add("two tiles lit left edge does not read dark right tile", 2, 1, 3, [&](Case& t) { LitTile(t, 0, reversed); ReceiverUV(t, 0, .5f, .5f); });
    add("two tiles UV outside selected left tile is rejected", 2, 1, 2, [](Case& t) { ReceiverUV(t, 0, .51f, .5f); });
    add("two tiles UV outside selected right tile is rejected", 2, 1, 2, [](Case& t) { ReceiverUV(t, 1, .49f, .5f); });
    add("two tiles permuted cascade order locates right tile", 2, 1, 3, [&](Case& t)
    {
        Matrix(t, 0, .75f, .5f, .6f, reversed); Matrix(t, 1, .25f, .5f, .6f, reversed);
        LitTile(t, 1, reversed); ReceiverUV(t, 0, .5f, .5f);
    });
    add("four tiles corner does not read three lit neighbors", 4, 0, 3, [&](Case& t)
    {
        LitTile(t, 1, reversed); LitTile(t, 2, reversed); LitTile(t, 3, reversed); ReceiverUV(t, 0, .5f, .5f);
    });
    add("four tiles lit corner does not read dark neighbors", 4, 1, 3, [&](Case& t) { LitTile(t, 0, reversed); ReceiverUV(t, 0, .5f, .5f); });
    add("four tiles last tile minimum corner stays lit", 4, 1, 3, [&](Case& t) { LitTile(t, 3, reversed); ReceiverUV(t, 3, .5f, .5f); });
    add("four tiles UV inside atlas but outside selected V is rejected", 4, 1, 2, [](Case& t) { ReceiverUV(t, 0, .25f, .51f); });
    add("four tiles permuted order locates last tile", 4, 1, 3, [&](Case& t)
    {
        for (U i = 0; i < 4; ++i) Matrix(t, i, .25f + .5f * float((3 - i) & 1), .25f + .5f * float((3 - i) >> 1), .6f, reversed);
        LitTile(t, 3, reversed); ReceiverUV(t, 0, .5f, .5f);
    });
    add("unsupported two cascade square atlas fails open", 2, 1, 0, [](Case& t) { t.atlasHeight = 4; t.metadata[21][1] = 4; });
    add("unsupported two cascade vertical atlas fails open", 2, 1, 0, [](Case& t) { t.atlasWidth = 2; t.atlasHeight = 4; t.metadata[21][0] = 2; t.metadata[21][1] = 4; });
    add("unsupported four cascade rectangle fails open", 4, 1, 0, [](Case& t) { t.atlasHeight = 2; t.metadata[21][1] = 2; });
    add("duplicate two cascade tile centers fail open", 2, 1, 0, [&](Case& t) { Matrix(t, 1, .25f, .5f, .6f, reversed); });
    add("duplicate four cascade tile centers fail open", 4, 1, 0, [&](Case& t) { Matrix(t, 3, .25f, .25f, .6f, reversed); });
    add("tiny tile center outside quarter tile guard fails open", 2, 1, 0, [](Case& t) { t.metadata[0][3] += .14f; });
    add("tiny tile center inside quarter tile guard remains valid", 2, 0, 3, [](Case& t) { t.metadata[0][3] += .05f; });
    add("tiny tile center at quarter tile guard remains valid", 2, 0, 3, [](Case& t) { t.metadata[0][3] += .125f; });
    add("nonfinite center transform fails open", 2, 1, 0, [](Case& t) { t.metadata[0][3] = std::numeric_limits<float>::quiet_NaN(); });
    add("zero center homogeneous W fails open", 2, 1, 0, [](Case& t) { t.metadata[3] = F4{}; });
    add("nonpositive sphere radius fails open", 2, 1, 0, [](Case& t) { t.metadata[20][0] = 0; });
    add("nonfinite sphere radius fails open", 2, 1, 0, [](Case& t) { t.metadata[20][0] = std::numeric_limits<float>::quiet_NaN(); });
    add("bound atlas dimensions must match metadata", 2, 1, 0, [](Case& t) { t.metadata[21][0] = 8; t.metadata[21][1] = 4; });
    add("fractional atlas dimensions fail open", 2, 1, 0, [](Case& t) { t.metadata[21][0] = 3.5f; });
    add("unsupported three cascade count fails open", 4, 1, 0, [](Case& t) { t.metadata[21][2] = 3; });
    add("zero cascade count fails open", 4, 1, 0, [](Case& t) { t.metadata[21][2] = 0; });
    add("inactive negative infinity radii do not poison active selection", 2, 0, 3, [](Case& t)
    { t.metadata[20][2] = t.metadata[20][3] = -std::numeric_limits<float>::infinity(); });
    add("inactive positive infinity radii do not poison active selection", 2, 0, 3, [](Case& t)
    { t.metadata[20][2] = t.metadata[20][3] = std::numeric_limits<float>::infinity(); });
    add("inactive NaN radii do not poison active selection", 2, 0, 3, [](Case& t)
    { t.metadata[20][2] = t.metadata[20][3] = std::numeric_limits<float>::quiet_NaN(); });
    add("active second radius infinity still fails open", 2, 1, 0, [](Case& t)
    { t.metadata[20][1] = std::numeric_limits<float>::infinity(); });
    add("active third radius infinity in four cascades still fails open", 4, 1, 0, [](Case& t)
    { t.metadata[20][2] = std::numeric_limits<float>::infinity(); });
    add("active fourth radius NaN in four cascades still fails open", 4, 1, 0, [](Case& t)
    { t.metadata[20][3] = std::numeric_limits<float>::quiet_NaN(); });
    add("native stabilized sphere center remains a valid tile", 2, 0, 3, [&](Case& t)
    {
        t.atlasWidth = 4096; t.atlasHeight = 2048; t.metadata[21][0] = 4096; t.metadata[21][1] = 2048;
        t.metadata[16] = F4{16.1999092f, 15.8699417f, -117.992599f, 0};
        t.metadata[0] = F4{-.00167015276f, 1.37914359e-11f, .000549667922f, .341796845f};
        t.metadata[1] = F4{.000330250798f, .00335413008f, .0010034591f, .559570313f};
        t.position = t.metadata[16];
        t.largeAtlas.assign(size_t(t.atlasWidth) * t.atlasHeight, Depth(reversed, .4f));
    });
    for (float offset : {1.49f, 1.5f, 1.51f})
    {
        Case t = Base(reversed); Layout(t, 2, reversed);
        t.atlasWidth = 16; t.atlasHeight = 8; t.metadata[21][0] = 16; t.metadata[21][1] = 8;
        t.metadata[0][3] += offset / 16;
        t.largeAtlas.assign(128, Depth(reversed, .4f));
        t.name = "stabilized center offset atlas pixels " + std::to_string(offset);
        t.expected = offset <= 1.5f ? 0.f : 1.f; t.expectedStatus = offset <= 1.5f ? 3 : 0;
        tests.push_back(t);
    }
    tests.push_back(ExactNative(reversed));
    return tests;
}

static std::vector<Case> ReasonCases(bool reversed)
{
    std::vector<Case> tests;
    auto add = [&](const char* name, U reason, const std::function<void(Case&)>& change)
    {
        Case t = Base(reversed); t.name = name; t.expected = 1; t.expectedStatus = 100 + reason;
        change(t); tests.push_back(t);
    };
    add("invalid reason unarmed", 1, [](Case& t) { t.ready = 0; });
    add("invalid reason export marker", 2, [](Case& t) { t.metadata[23][3] = 0; });
    add("invalid reason export frame", 3, [](Case& t) { t.metadata[23][0] -= 1; });
    add("invalid reason export token", 4, [](Case& t) { t.metadata[23][2] += 1; });
    add("invalid reason atlas marker", 5, [](Case& t) { t.metadata[24][3] = 0; });
    add("invalid reason atlas frame", 6, [](Case& t) { t.metadata[24][0] -= 1; });
    add("invalid reason atlas token", 7, [](Case& t) { t.metadata[24][2] += 1; });
    add("invalid reason nonfinite metadata", 8, [](Case& t) { t.metadata[21][3] = std::numeric_limits<float>::quiet_NaN(); });
    add("invalid reason metadata dimensions", 9, [](Case& t) { t.metadata[21][0] = 0; });
    add("invalid reason bound atlas dimensions", 10, [](Case& t) { t.metadata[21][0] = 8; });
    add("invalid reason cascade count", 11, [](Case& t) { t.metadata[21][2] = 3; });
    add("invalid reason layout aspect", 12, [](Case& t) { t.metadata[21][2] = 2; });
    add("invalid reason sphere radius", 13, [&](Case& t) { Layout(t, 2, reversed); t.metadata[20][0] = 0; });
    add("invalid reason sphere center", 14, [&](Case& t) { Layout(t, 2, reversed); t.metadata[16][0] = std::numeric_limits<float>::quiet_NaN(); });
    add("invalid reason projected center W", 15, [&](Case& t) { Layout(t, 2, reversed); t.metadata[3] = F4{}; });
    add("invalid reason projected center cell", 16, [&](Case& t) { Layout(t, 2, reversed); t.metadata[0][3] = -1; });
    add("invalid reason projected center tolerance", 17, [&](Case& t) { Layout(t, 2, reversed); t.metadata[0][3] += .14f; });
    add("invalid reason duplicate projected cells", 18, [&](Case& t) { Layout(t, 2, reversed); Matrix(t, 1, .25f, .5f, .6f, reversed); });
    add("invalid reason active second radius infinity", 13, [&](Case& t)
    { Layout(t, 2, reversed); t.metadata[20][1] = std::numeric_limits<float>::infinity(); });
    add("invalid reason active third radius infinity", 13, [&](Case& t)
    { Layout(t, 4, reversed); t.metadata[20][2] = std::numeric_limits<float>::infinity(); });
    Case valid = Base(reversed); valid.name = "reason helper leaves sampled status unchanged"; valid.expectedStatus = 3; tests.push_back(valid);
    Case outside = Base(reversed); Layout(outside, 4, reversed); outside.position[0] = 10;
    outside.name = "reason helper leaves outside cascade unchanged"; outside.expected = 1; outside.expectedStatus = 1; tests.push_back(outside);
    outside = Base(reversed); outside.metadata[0][3] = 1.1f;
    outside.name = "reason helper leaves outside atlas unchanged"; outside.expected = 1; outside.expectedStatus = 2; tests.push_back(outside);
    outside = Base(reversed); outside.distance = 10; outside.position[0] = 20;
    outside.name = "reason helper leaves full distance fade unchanged"; outside.expected = 1; outside.expectedStatus = 4; tests.push_back(outside);
    tests.push_back(ExactNative(reversed));
    return tests;
}

int main(int argc, char** argv)
{
    try
    {
        Require(argc == 8 || argc == 9, "Usage: ShadowVisibilityGpu vertex normalPS reversedPS normalStatusPS reversedStatusPS normalReasonPS reversedReasonPS [reasons]");
        bool diagnosticOnly = argc == 9 && std::string(argv[8]) == "reasons";
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &requested, 1, D3D11_SDK_VERSION, &device, nullptr, &context));
        auto vertexBytes = Bytes(argv[1]); ComPtr<ID3D11VertexShader> vertex;
        Check(device->CreateVertexShader(vertexBytes.data(), vertexBytes.size(), nullptr, &vertex));
        context->VSSetShader(vertex.Get(), nullptr, 0);
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = desc.Height = desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> output; Check(device->CreateTexture2D(&desc, nullptr, &output));
        ComPtr<ID3D11RenderTargetView> view; Check(device->CreateRenderTargetView(output.Get(), nullptr, &view));
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; Check(device->CreateTexture2D(&desc, nullptr, &staging));
        ID3D11RenderTargetView* target = view.Get(); context->OMSetRenderTargets(1, &target, nullptr);
        D3D11_VIEWPORT viewport{0, 0, 1, 1, 0, 1}; context->RSSetViewports(1, &viewport);
        D3D11_RASTERIZER_DESC raster{}; raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
        ComPtr<ID3D11RasterizerState> rasterState; Check(device->CreateRasterizerState(&raster, &rasterState)); context->RSSetState(rasterState.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        U total = 0, blocked = 0, lit = 0, fractional = 0; float maxError = 0;
        for (U variant = 0; variant < 6; ++variant)
        {
            if (diagnosticOnly && variant < 4) continue;
            bool reversed = (variant & 1) != 0, statusMode = variant >= 2;
            Shader shader = Pixel(device.Get(), argv[2 + variant], true);
            context->PSSetShader(shader.pixel.Get(), nullptr, 0);
            auto tests = variant >= 4 ? ReasonCases(reversed) : statusMode ? StatusCases(reversed) : Cases(reversed);
            if (variant < 4)
            {
                auto tileTests = TileCases(reversed); tests.insert(tests.end(), tileTests.begin(), tileTests.end());
            }
            for (const Case& test : tests)
            {
                Resource metadata = Image(device.Get(), 25, 1, DXGI_FORMAT_R32G32B32A32_FLOAT, sizeof(F4), test.metadata.data());
                Resource atlas = Image(device.Get(), test.atlasWidth, test.atlasHeight, DXGI_FORMAT_R32_FLOAT, sizeof(float),
                    test.largeAtlas.empty() ? test.atlas.data() : test.largeAtlas.data());
                std::vector<ComPtr<ID3D11Buffer>> constants;
                D3D11_SHADER_DESC reflection{}; Check(shader.reflection->GetDesc(&reflection));
                for (U r = 0; r < reflection.BoundResources; ++r)
                {
                    D3D11_SHADER_INPUT_BIND_DESC binding{}; Check(shader.reflection->GetResourceBindingDesc(r, &binding));
                    if (binding.Type == D3D_SIT_CBUFFER)
                    {
                        auto cb = shader.reflection->GetConstantBufferByName(binding.Name);
                        D3D11_SHADER_BUFFER_DESC cbDesc{}; Check(cb->GetDesc(&cbDesc)); std::vector<char> bytes(cbDesc.Size);
                        for (U v = 0; v < cbDesc.Variables; ++v)
                        {
                            D3D11_SHADER_VARIABLE_DESC variable{}; Check(cb->GetVariableByIndex(v)->GetDesc(&variable));
                            std::string name = variable.Name;
                            auto put = [&](const void* value, size_t size)
                            {
                                Require(size <= variable.Size && variable.StartOffset + size <= bytes.size(), "Reflected constant bounds");
                                std::memcpy(bytes.data() + variable.StartOffset, value, size);
                            };
                            if (name == "_WorldSpaceCameraPos") put(test.camera.data(), 12);
                            else if (name == "_GTTestWorldPosition") put(test.position.data(), 16);
                            else if (name == "_GTFrameStamp") put(test.frame.data(), 16);
                            else if (name == "_GTCameraToken") put(&test.token, 4);
                            else if (name == "_GTShadowReady") put(&test.ready, 4);
                            else if (name == "_GTShadowDistance") put(&test.distance, 4);
                            else if (name == "_GTReceiverBias") put(&test.bias, 4);
                            else throw std::runtime_error("Unexpected constant " + name);
                        }
                        auto buffer = Constants(device.Get(), bytes.data(), cbDesc.Size); ID3D11Buffer* pointer = buffer.Get();
                        context->PSSetConstantBuffers(binding.BindPoint, 1, &pointer); constants.push_back(buffer);
                    }
                    else if (binding.Type == D3D_SIT_TEXTURE)
                    {
                        ID3D11ShaderResourceView* resource = nullptr;
                        if (std::string(binding.Name) == "_GTShadowData") resource = metadata.view.Get();
                        else if (std::string(binding.Name) == "_GTSunShadowAtlas") resource = atlas.view.Get();
                        else throw std::runtime_error("Unexpected texture binding");
                        context->PSSetShaderResources(binding.BindPoint, 1, &resource);
                    }
                    else throw std::runtime_error("Unexpected nontexture/nonconstant binding");
                }
                const F4 clear{-13, -13, -13, -13}; context->ClearRenderTargetView(view.Get(), clear.data()); context->Draw(3, 0);
                context->CopyResource(staging.Get(), output.Get()); D3D11_MAPPED_SUBRESOURCE mapped{};
                Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)); F4 observed{};
                std::memcpy(observed.data(), mapped.pData, sizeof(F4)); context->Unmap(staging.Get(), 0);
                F4 expected = statusMode ? F4{test.expected, float(test.expectedStatus), 0, 1} :
                    F4{test.expected, test.expected, test.expected, test.expected};
                for (U component = 0; component < 4; ++component)
                {
                    float value = observed[component];
                    float error = std::abs(value - expected[component]); maxError = std::max(maxError, error);
                    if (!std::isfinite(value) || error > .00002f)
                    {
                        std::cerr << "FAIL " << (reversed ? "reversed" : "normal") << " " << test.name
                            << " component=" << component << ": expected=" << expected[component] << " observed=" << value
                            << " RGBA=" << observed[0] << ',' << observed[1] << ',' << observed[2] << ',' << observed[3] << '\n'; return 1;
                    }
                }
                total++; if (test.expected == 0) blocked++; else if (test.expected == 1) lit++; else fractional++;
                std::cout << "PASS " << (reversed ? "reversed" : "normal") << " " << test.name << ": " << observed[0] << '\n';
                if (statusMode) std::cout << "  status=" << observed[1] << '\n';
            }
        }
        if (!diagnosticOnly)
            Require(blocked >= 10 && lit >= 10 && fractional >= 6, "Expected blocked/lit/fractional outcome coverage missing");
        std::cout << "PASS: " << total << " standalone WARP receiver cases; blocked=" << blocked << ", lit=" << lit
            << ", fractional=" << fractional << ", maxAbsError=" << maxError << ". No game bytecode/assets/license used.\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << '\n'; return 1; }
}
