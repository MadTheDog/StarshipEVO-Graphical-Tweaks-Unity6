#include "FixtureResources.h"
#include <sstream>
struct Case
{
    std::string name; int alpha = 2, pixel = 0, normalMap = -1;
    F4 diffuse{126.f / 255, 126.f / 255, 126.f / 255, 0.37f};
    F4 specular{68.f / 255, 150.f / 255, 1, 23.f / 255};
    F4 normal{0, 0, 1, 0}, camera{0, 0, 1, 0}, light{0, 0, 1, 0};
    F4 palette{0.2f, 0.4f, 0.7f, 0}, chroma{0.3f, 0.7f, 0.5f, 0};
    U paint = 0; float factor = 1, wobble = 1, buildFlag = 0;
    bool varyingLookup = false;
    F4 worldSpan{}, normalDelta{};
    F4 environment{0,0,0,1},emission{};
    float overHeat=0,time=0;int debugColors=0;
    bool cancellationStress=false;
    int border=-1,projection=0;bool barrier=false;
    F4 uv{0.5f,0.5f,0,0},uvSpan{};
    int borderMode=0,borderNormal=-1,borderSpecular=-1,blendMode=0;
    float resizeUnit=1;bool borderResponse=false;
    int paintMode=0;
};

static Resource BorderTexture(ID3D11Device* device,F4 base,U kind)
{
    constexpr U size=16,mips=5;std::array<std::vector<F4>,2*mips> pixels;
    std::array<D3D11_SUBRESOURCE_DATA,2*mips> data{};
    for(U layer=0;layer<2;++layer)for(U mip=0;mip<mips;++mip)
    {
        U index=layer*mips+mip,width=size>>mip;pixels[index].resize(width*width);
        for(U y=0;y<width;++y)for(U x=0;x<width;++x)
        {
            F4 value=base;float u=(x+0.5f)/width,v=(y+0.5f)/width;
            if(layer==1)
            {
                if(kind==0)value=F4{0.25f+0.5f*u,0.7f-0.4f*v,0.2f+0.5f*v,0.1f+0.8f*u+0.01f*mip};
                else if(kind==1)value=F4{0.2f+0.6f*u,0.2f+0.5f*v,0.25f,0.15f+0.65f*u+0.01f*mip};
                else value=F4{0.5f+0.2f*u,0.5f-0.2f*v,1,1};
            }
            pixels[index][y*width+x]=value;
        }
        data[index].pSysMem=pixels[index].data();data[index].SysMemPitch=width*sizeof(F4);
    }
    D3D11_TEXTURE2D_DESC desc{};desc.Width=desc.Height=size;desc.MipLevels=mips;desc.ArraySize=2;
    desc.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> texture;Check(device->CreateTexture2D(&desc,data.data(),&texture));
    Resource result;Check(texture.As(&result.resource));Check(device->CreateShaderResourceView(texture.Get(),nullptr,&result.view));return result;
}

static Resource VaryingLookup(ID3D11Device* device)
{
    // Original synthetic curved table, deliberately varying along both axes.
    // A constant table cannot detect incorrect reflection/roughness coordinates.
    constexpr U size=64,mipCount=7;std::array<std::vector<F4>,mipCount> values;
    std::array<D3D11_SUBRESOURCE_DATA,mipCount> data{};
    for(U mip=0;mip<mipCount;++mip)
    {
        U width=size>>mip;values[mip].resize(width*width);
        for(U y=0;y<width;++y)for(U x=0;x<width;++x)
        {
            float u=(x+0.5f)/width,v=(y+0.5f)/width;
            values[mip][y*width+x]=F4{0.002f+0.04f*u*u+0.05f*v+0.002f*std::sin(8*u)+mip*0.003f,0,0,1};
        }
        data[mip].pSysMem=values[mip].data();data[mip].SysMemPitch=width*sizeof(F4);
    }
    D3D11_TEXTURE2D_DESC desc{};desc.Width=desc.Height=size;desc.MipLevels=mipCount;desc.ArraySize=1;
    desc.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> texture;Check(device->CreateTexture2D(&desc,data.data(),&texture));
    Resource result;Check(texture.As(&result.resource));Check(device->CreateShaderResourceView(texture.Get(),nullptr,&result.view));return result;
}

struct VariableBinding { U offset;std::string name; };
struct ConstantBinding { U bytes=0;std::vector<VariableBinding> variables; };
struct MetadataBindings
{
    std::map<U,ConstantBinding> constants;std::map<U,std::string> resources;
    explicit MetadataBindings(const char* path)
    {
        if(std::string(path)=="-")return;
        std::ifstream input(path);Require(input.good(),"Candidate binding metadata missing");
        std::string line;while(std::getline(input,line))
        {
            std::istringstream fields(line);char type;U slot;fields>>type>>slot;
            Require(!fields.fail(),"Invalid candidate binding metadata");
            if(type=='C'){U bytes;fields>>bytes;Require(slot<14&&bytes>0&&bytes<=65536&&bytes%16==0,"Invalid cbuffer metadata");constants[slot].bytes=bytes;}
            else if(type=='V'){U offset;std::string name;fields>>offset>>name;constants[slot].variables.push_back({offset,name});}
            else if(type=='T'){std::string name;fields>>name;Require(slot<32,"Invalid resource metadata");resources[slot]=name;}
            else throw std::runtime_error("Unknown candidate binding record");
            Require(!fields.fail(),"Truncated candidate binding record");
        }
        Require(!constants.empty(),"Candidate binding metadata contains no cbuffers");
    }
};

int main(int argc, char** argv)
{
    try
    {
        Require(argc == 8, "Usage: GlassSurfaceGpu nativePS candidatePS nativeInputsVS candidateInputsVS brdfMode metadata-or-dash forcedOnly");
        const int brdfMode=std::stoi(argv[5]);Require(brdfMode==0||brdfMode==1,"Invalid BRDF fixture mode");
        MetadataBindings bindings(argv[6]);bool forcedOnly=std::stoi(argv[7])!=0;
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &requested, 1, D3D11_SDK_VERSION, &device, nullptr, &context));
        Shader native = Pixel(device.Get(), argv[1], false), candidate = Pixel(device.Get(), argv[2], true);
        ComPtr<ID3D11VertexShader> nativeVertex, candidateVertex;
        auto vertexBytes = Bytes(argv[3]); Check(device->CreateVertexShader(vertexBytes.data(), vertexBytes.size(), nullptr, &nativeVertex));
        vertexBytes = Bytes(argv[4]); Check(device->CreateVertexShader(vertexBytes.data(), vertexBytes.size(), nullptr, &candidateVertex));
        D3D11_TEXTURE2D_DESC outputDesc{}; outputDesc.Width = outputDesc.Height = 4; outputDesc.MipLevels = outputDesc.ArraySize = 1;
        outputDesc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT; outputDesc.SampleDesc.Count = 1;
        outputDesc.Usage = D3D11_USAGE_DEFAULT; outputDesc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> output; Check(device->CreateTexture2D(&outputDesc, nullptr, &output));
        ComPtr<ID3D11RenderTargetView> outputView; Check(device->CreateRenderTargetView(output.Get(), nullptr, &outputView));
        outputDesc.Usage = D3D11_USAGE_STAGING; outputDesc.BindFlags = 0; outputDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; Check(device->CreateTexture2D(&outputDesc, nullptr, &staging));
        ID3D11RenderTargetView* outputPointer = outputView.Get(); context->OMSetRenderTargets(1, &outputPointer, nullptr);
        D3D11_VIEWPORT viewport{0, 0, 4, 4, 0, 1}; context->RSSetViewports(1, &viewport);
        D3D11_RASTERIZER_DESC raster{}; raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
        ComPtr<ID3D11RasterizerState> rasterState; Check(device->CreateRasterizerState(&raster, &rasterState)); context->RSSetState(rasterState.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11_SAMPLER_DESC samplerDesc{}; samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler; Check(device->CreateSamplerState(&samplerDesc, &sampler));
        ID3D11SamplerState* samplers[16]; for (auto& entry : samplers) entry = sampler.Get(); context->PSSetSamplers(0, 16, samplers);
        D3D11_BLEND_DESC blendDesc{};auto& blendTarget=blendDesc.RenderTarget[0];blendTarget.BlendEnable=TRUE;
        blendTarget.SrcBlend=blendTarget.SrcBlendAlpha=D3D11_BLEND_ONE;
        blendTarget.DestBlend=blendTarget.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
        blendTarget.BlendOp=blendTarget.BlendOpAlpha=D3D11_BLEND_OP_ADD;blendTarget.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        ComPtr<ID3D11BlendState> premultiplied;Check(device->CreateBlendState(&blendDesc,&premultiplied));

        std::vector<Case> cases;
        Case current; current.name = "clear, palette tint"; cases.push_back(current);
        current.diffuse=F4{0.49f,0.495f,0.49f,0};current.palette=F4{0.5f,0.5f,0.5f,0};
        current.name="nongrey base texture with grey palette paint";cases.push_back(current);
        current.paintMode=1;current.name="nongrey unpainted texture retains RGB";cases.push_back(current);
        current=Case{};
        current.name = "clear, oblique light"; current.light = F4{0.6f, 0, 0.8f, 0}; cases.push_back(current);
        current.name = "clear, oblique view"; current.camera = F4{0.8f, 0, 0.6f, 0}; cases.push_back(current);
        current.name = "clear, packed custom paint"; current.paint = 0x00b04010; cases.push_back(current);
        current.name = "clear, unpainted texture"; current.specular[2] = 0; cases.push_back(current);
        current.name = "nonmetal, zero input alpha"; current.specular = F4{0, 0.6f, 0, 0}; cases.push_back(current);
        current.name = "metal, full input alpha"; current.specular = F4{1, 0.6f, 0, 1}; cases.push_back(current);
        current = Case{}; current.name = "frosted forced alpha"; current.alpha = 7; current.specular[3] = 0.82f; cases.push_back(current);
        current = Case{}; current.name = "entity build preview tint"; current.buildFlag = 3; cases.push_back(current);
        current = Case{}; current.name = "normal texture"; current.normalMap = 0; cases.push_back(current);
        for (int dynamic : {0, 1}) for (float factor : {0.f, 0.5f, 2.f, 100.f})
        {
            current = Case{}; current.alpha = 9; current.pixel = dynamic; current.factor = factor;
            current.name = "glow alpha mode " + std::to_string(dynamic) + ", factor " + std::to_string(factor); cases.push_back(current);
        }
        current = Case{}; current.alpha = 9; current.pixel = 1; current.factor = 0; current.chroma = F4{0, 0, 0, 0};
        current.name = "black global glow falls back to diffuse alpha"; cases.push_back(current);
        current.chroma = F4{1, 1, 1, 0}; current.paint = 0xff000000; current.name = "black packed glow falls back to diffuse alpha"; cases.push_back(current);
        current.paint = 0xff000010; current.factor = 0.5f; current.wobble = 0.8f; current.name = "packed glow factor and wobble"; cases.push_back(current);
        current=Case{};current.varyingLookup=true;current.name="varying lookup, oblique view and light";
        current.camera=F4{0.3f,0.4f,0.8660254f,0};current.light=F4{0.6f,0,0.8f,0};cases.push_back(current);
        current.normal=F4{0.3f,0.2f,0.93f,0};current.name="varying lookup, nonunit surface normal";cases.push_back(current);
        current.light=F4{0.5976f,0,0.7968f,0};current.name="varying lookup, light length 0.996";cases.push_back(current);
        current.light=F4{0.3f,0,0.4f,0};current.name="varying lookup, light length 0.5";cases.push_back(current);
        current.light=F4{0.3f,-0.3f,0.9f,0};current.worldSpan=F4{2.4f,1.7f,-3.2f,0};
        current.name="varying lookup, spatial view derivatives and mip selection";cases.push_back(current);
        current.normalDelta=F4{0.15f,-0.08f,0,0};current.name="varying lookup, spatial view and normal derivatives";cases.push_back(current);
        current=Case{};current.environment=F4{0.4f,0.8f,1.2f,1};current.name="nonzero reflection, native full minus light zero";cases.push_back(current);
        current.emission=F4{0.3f,0.7f,1.1f,0};current.overHeat=0.2f;current.debugColors=1;current.time=1.23f;
        current.name="reflection plus common emission, heat and debug color";cases.push_back(current);
        current.environment=F4{16,32,64,1};current.varyingLookup=true;current.worldSpan=F4{2.4f,1.7f,-3.2f,0};
        current.name="HDR reflection with spatial derivatives and emission";cases.push_back(current);
        current.environment=F4{50000,100000,200000,1};current.cancellationStress=true;
        current.name="extreme HDR subtraction roundoff bound";cases.push_back(current);
        current=Case{};current.border=1;current.barrier=true;current.name="unsupported border is an opaque loss barrier";cases.push_back(current);
        current.projection=1;current.name="unsupported projection is an opaque loss barrier";cases.push_back(current);
        current.alpha=5;current.specular[3]=0.82f;current.name="unsupported material barrier precedes alpha clip";cases.push_back(current);
        current=Case{};current.alpha=9;current.pixel=13;current.barrier=true;current.name="unsupported glow alpha is an opaque loss barrier";cases.push_back(current);
        current=Case{};current.normalMap=0;current.normal=F4{0.3f,0.2f,0.93f,0};
        current.name="normal texture with nonunit interpolated normal";cases.push_back(current);
        for(int borderMode:{3,4,5})for(int blendMode:{0,1})
        {
            current=Case{};current.borderResponse=true;current.border=1;current.borderMode=borderMode;current.blendMode=blendMode;
            current.borderNormal=current.borderSpecular=1;current.resizeUnit=0.25f;
            current.uv=F4{1.1f,1.3f,2,3};current.uvSpan=F4{0.9f,0.8f,0,0};
            current.name="border mode "+std::to_string(borderMode)+" blend "+std::to_string(blendMode)+" resized UV gradients";cases.push_back(current);
            current.borderSpecular=-1;current.name+=" no border specular";cases.push_back(current);
            current.borderNormal=-1;current.normal=F4{0,0,1.5f,0};current.name+=" raw border normal";cases.push_back(current);
        }

        float maximumRgbError = 0, maximumAlphaError = 0,maximumCancellationError=0;
        std::vector<int> selections{brdfMode};if(!forcedOnly)selections.push_back(-1);
        for(int selectionMode : selections)for (const Case& test : cases)
        {
            Material material{}; material.alpha = test.alpha; material.pixel = test.pixel;
            material.border=test.border;material.projection=test.projection;material.borderNormal = material.borderSpecular = -1; material.normal = test.normalMap; material.specular = 0;
            material.borderMode=test.borderMode;material.borderNormal=test.borderNormal;material.borderSpecular=test.borderSpecular;material.blend=test.blendMode;
            material.paint=test.paintMode;
            material.tiling = 1; material.unit = material.uvScale[0] = material.uvScale[1] = test.resizeUnit;
            material.uLow = material.vLow = 0.45f; material.uHigh = material.vHigh = 0.55f; material.uWidth = material.vWidth = 0.1f;
            Build build{}; build.color = test.paint; build.unit = build.health = 1;
            Palette palette{}; std::memcpy(palette.color, test.palette.data(), sizeof(F4));
            float scalar = test.factor; F4 vector=test.emission; U cutout[2]{};
            float identity[16]{}; identity[0] = identity[5] = identity[10] = identity[15] = 1;
            std::map<std::string, Resource> resources;
            resources.emplace("_EntityCanBuild", Structured(device.Get(), 1, 4, &test.buildFlag));
            resources.emplace("VectorDynData", Structured(device.Get(), 1, 16, vector.data()));
            resources.emplace("FloatDynData", Structured(device.Get(), 1, 4, &scalar));
            resources.emplace("_TransformDatasBuffer", Structured(device.Get(), 1, 64, identity));
            resources.emplace("MaterialParams", Structured(device.Get(), 1, sizeof(Material), &material));
            resources.emplace("BrickBuildData", Structured(device.Get(), 1, sizeof(Build), &build));
            resources.emplace("PaintData", Structured(device.Get(), 1, sizeof(Palette), &palette));
            resources.emplace("_CutoutDyndataIds", Structured(device.Get(), 1, 8, cutout));
            resources.emplace("MAIN_TEX", test.borderResponse?BorderTexture(device.Get(),test.diffuse,0):Texture(device.Get(), test.diffuse, true));
            resources.emplace("SPECULAR_TEX", test.borderResponse?BorderTexture(device.Get(),test.specular,1):Texture(device.Get(), test.specular, true));
            resources.emplace("NORMAL_TEX", test.borderResponse?BorderTexture(device.Get(),F4{0.7f,0.3f,1,1},2):Texture(device.Get(), F4{0.7f, 0.3f, 1, 1}, true));
            resources.emplace("_NoiseCurlTex", Texture(device.Get(), F4{0, 0, 0, 0}, false));
            resources.emplace("unity_NHxRoughness", test.varyingLookup?VaryingLookup(device.Get()):Texture(device.Get(), F4{0.02f,0,0,0}, false));
            resources.emplace("unity_SpecCube0", Texture(device.Get(), test.environment, false, true));
            resources.emplace("unity_SpecCube1", Texture(device.Get(), test.environment, false, true));
            std::vector<ComPtr<ID3D11Buffer>> constantOwnership;
            std::array<float, 24> fixture{}; std::memcpy(fixture.data(), test.normal.data(), sizeof(F4));
            std::memcpy(&fixture[8],test.worldSpan.data(),sizeof(F4));std::memcpy(&fixture[12],test.normalDelta.data(),sizeof(F4));
            std::memcpy(&fixture[16],test.uv.data(),sizeof(F4));std::memcpy(&fixture[20],test.uvSpan.data(),sizeof(F4));
            std::memcpy(&fixture[4], &test.paint, sizeof(U)); auto fixtureBuffer = Constants(device.Get(), fixture.data(), sizeof(fixture));
            ID3D11Buffer* fixturePointer = fixtureBuffer.Get(); context->VSSetConstantBuffers(0, 1, &fixturePointer);
            auto render = [&](bool original,bool zeroLight,const F4* background=nullptr)
            {
                ID3D11ShaderResourceView* nullResources[32]{}; context->PSSetShaderResources(0, 32, nullResources);
                ID3D11Buffer* nullConstants[14]{}; context->PSSetConstantBuffers(0, 14, nullConstants);
                if (original)
                {
                    std::vector<F4> globals(564); globals[2] = zeroLight?F4{}:F4{1.4f, 1.4f, 1.4f, 1}; globals[538][3] = test.wobble; globals[561] = test.chroma;
                    globals[562][0]=test.overHeat;std::memcpy(&globals[562][2],&test.debugColors,4);
                    std::vector<F4> camera(5); camera[4] = test.camera;camera[1][3]=test.time;
                    std::vector<F4> draw(10); for (int i = 0; i < 4; ++i) draw[i][i] = 1; draw[9][3] = 1;
                    std::vector<F4> probes(8); probes[1][3] = 1; probes[3] = probes[7] = F4{1, 1, 0, 0};
                    auto bind = [&](U slot, const void* values, U size) { auto buffer = Constants(device.Get(), values, size); ID3D11Buffer* pointer = buffer.Get(); context->PSSetConstantBuffers(slot, 1, &pointer); constantOwnership.push_back(buffer); };
                    bind(0, globals.data(), U(globals.size() * sizeof(F4))); bind(1, camera.data(), U(camera.size() * sizeof(F4)));
                    bind(2, test.light.data(), sizeof(F4)); bind(3, draw.data(), U(draw.size() * sizeof(F4))); bind(4, probes.data(), U(probes.size() * sizeof(F4)));
                    const char* names[] = {"_NoiseCurlTex","unity_SpecCube0","unity_SpecCube1","_EntityCanBuild","VectorDynData","FloatDynData","MAIN_TEX","NORMAL_TEX","_TransformDatasBuffer","MaterialParams","BrickBuildData","PaintData","SPECULAR_TEX","_CutoutDyndataIds"};
                    for (U slot = 0; slot < 14; ++slot)
                    {
                        const char* name=brdfMode==1&&slot==1?"unity_NHxRoughness":(brdfMode==1&&slot==2?"unity_SpecCube0":names[slot]);
                        ID3D11ShaderResourceView* view=resources.at(name).view.Get();context->PSSetShaderResources(slot,1,&view);
                    }
                    context->VSSetShader(nativeVertex.Get(), nullptr, 0); context->PSSetShader(native.pixel.Get(), nullptr, 0);
                }
                else
                {
                    auto fill=[&](std::vector<char>& bytes,U offset,const std::string& name)
                    {
                        auto put=[&](const void* value,size_t size)
                        {Require(offset+size<=bytes.size(),"Candidate variable exceeds cbuffer");std::memcpy(bytes.data()+offset,value,size);};
                        if(name=="_WorldSpaceCameraPos")put(test.camera.data(),12);
                        else if(name=="_WorldSpaceLightPos0")put(test.light.data(),16);
                        else if(name=="_LightColor0"){F4 color{1.4f,1.4f,1.4f,1};put(color.data(),16);}
                        else if(name=="unity_ObjectToWorld")put(identity,64);
                        else if(name=="unity_WorldTransformParams"){F4 transform{0,0,0,1};put(transform.data(),16);}
                        else if(name=="_LightWobble")put(&test.wobble,4);
                        else if(name=="_GlowChromaVarying")put(test.chroma.data(),16);
                        else if(name=="_GTLossDiagnosticMode"){U mode=1;put(&mode,4);}
                        else if(name=="_GTBRDFMode")put(&selectionMode,4);
                    };
                    auto bind=[&](U slot,const std::vector<char>& bytes)
                    {
                        auto buffer=Constants(device.Get(),bytes.data(),U(bytes.size()));ID3D11Buffer* pointer=buffer.Get();
                        context->PSSetConstantBuffers(slot,1,&pointer);constantOwnership.push_back(buffer);
                    };
                    if(!bindings.constants.empty())
                    {
                        for(const auto& entry:bindings.constants)
                        {
                            std::vector<char> bytes(entry.second.bytes);
                            for(const auto& variable:entry.second.variables)fill(bytes,variable.offset,variable.name);
                            bind(entry.first,bytes);
                        }
                        for(const auto& entry:bindings.resources)
                        {
                            auto found=resources.find(entry.second);
                            if(found!=resources.end()){auto view=found->second.view.Get();context->PSSetShaderResources(entry.first,1,&view);}
                        }
                    }
                    else
                    {
                    D3D11_SHADER_DESC desc{}; Check(candidate.reflection->GetDesc(&desc));
                    for (U resource = 0; resource < desc.BoundResources; ++resource)
                    {
                        D3D11_SHADER_INPUT_BIND_DESC binding{}; Check(candidate.reflection->GetResourceBindingDesc(resource, &binding));
                        if (binding.Type == D3D_SIT_CBUFFER)
                        {
                            auto reflection = candidate.reflection->GetConstantBufferByName(binding.Name); D3D11_SHADER_BUFFER_DESC cb{}; Check(reflection->GetDesc(&cb));
                            std::vector<char> bytes(cb.Size);
                            for (U v = 0; v < cb.Variables; ++v)
                            {
                                D3D11_SHADER_VARIABLE_DESC variable{}; Check(reflection->GetVariableByIndex(v)->GetDesc(&variable)); std::string name = variable.Name;
                                fill(bytes,variable.StartOffset,name);
                            }
                            bind(binding.BindPoint,bytes);
                        }
                        else if (binding.Type != D3D_SIT_SAMPLER)
                        {
                            auto value = resources.find(binding.Name);
                            if (value != resources.end()) { ID3D11ShaderResourceView* view = value->second.view.Get(); context->PSSetShaderResources(binding.BindPoint, 1, &view); }
                        }
                    }
                    }
                    context->VSSetShader(candidateVertex.Get(), nullptr, 0); context->PSSetShader(candidate.pixel.Get(), nullptr, 0);
                }
                context->OMSetBlendState(background?premultiplied.Get():nullptr,nullptr,0xffffffffu);
                F4 clear=background?*background:F4{}; context->ClearRenderTargetView(outputView.Get(), clear.data()); context->Draw(3, 0);
                context->CopyResource(staging.Get(), output.Get()); D3D11_MAPPED_SUBRESOURCE mapped{};
                Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)); std::array<F4,16> result{};
                for(U y=0;y<4;++y)std::memcpy(&result[y*4],(char*)mapped.pData+y*mapped.RowPitch,4*sizeof(F4));
                context->Unmap(staging.Get(),0);return result;
            };
            auto observedPixels=render(false,false);
            auto fullPixels=test.barrier?std::array<F4,16>{}:render(true,false);
            auto zeroPixels=test.barrier?std::array<F4,16>{}:render(true,true);
            auto referencePixels=fullPixels;
            for(U pixel=0;pixel<16;++pixel)
            {
                Require(fullPixels[pixel][3]==zeroPixels[pixel][3],"Native alpha depends on direct light in fixture");
                for(U component=0;component<3;++component)referencePixels[pixel][component]-=zeroPixels[pixel][component];
                if(test.barrier)referencePixels[pixel]=F4{0,0,0,1};
            }
            F4 reference=referencePixels[10],observed=observedPixels[10];
            if (test.name == "clear, palette tint")
            {
                Require(reference[0] + reference[1] + reference[2] > 0.01f, "Native reference direct lighting is unexpectedly zero");
                std::cout << "Native clear RGB=" << reference[0] << ',' << reference[1] << ',' << reference[2]
                    << " candidate=" << observed[0] << ',' << observed[1] << ',' << observed[2] << '\n';
            }
            float caseError=0,fullPeak=0,zeroPeak=0;
            for(U pixel=0;pixel<16;++pixel)for (int component = test.alpha == 9&&!test.barrier ? 3 : 0; component < 4; ++component)
            {
                reference=referencePixels[pixel];observed=observedPixels[pixel];
                float error = std::abs(reference[component] - observed[component]);
                if(component<3)
                {
                    caseError=std::max(caseError,error);fullPeak=std::max(fullPeak,std::abs(fullPixels[pixel][component]));
                    zeroPeak=std::max(zeroPeak,std::abs(zeroPixels[pixel][component]));
                }
                if (component < 3 && !test.cancellationStress) maximumRgbError = std::max(maximumRgbError, error);
                else if(component<3)maximumCancellationError=std::max(maximumCancellationError,error);
                else maximumAlphaError = std::max(maximumAlphaError, error);
                float tolerance = 0.0001f * std::max(1.f, std::abs(reference[component]));
                if(test.cancellationStress&&component<3)
                {
                    float magnitude=std::max(std::abs(fullPixels[pixel][component]),std::abs(zeroPixels[pixel][component]));
                    float ulp=std::nextafter(magnitude,INFINITY)-magnitude;
                    tolerance=std::max(tolerance,2*ulp);
                }
                if (!std::isfinite(reference[component]) || !std::isfinite(observed[component]) || std::abs(reference[component] - observed[component]) > tolerance)
                {
                    std::cerr << "FAIL " << (selectionMode<0?"auto ":"forced ") << test.name << " pixel " << pixel << " component " << component << ": native=" << reference[component] << " candidate=" << observed[component]
                        <<" nativeRGBA="<<reference[0]<<','<<reference[1]<<','<<reference[2]<<','<<reference[3]
                        <<" candidateRGBA="<<observed[0]<<','<<observed[1]<<','<<observed[2]<<','<<observed[3]<<'\n'; return 1;
                }
            }
            std::cout << "PASS " << (selectionMode<0?"auto ":"forced ") << test.name << ": nativeAlpha=" << reference[3] << " candidateAlpha=" << observed[3] << '\n';
            if(test.environment[0]!=0)
                std::cout<<"SUBTRACTION fullPeak="<<fullPeak<<" zeroPeak="<<zeroPeak<<" RGBerror="<<caseError<<
                    (test.cancellationStress?" (extreme HDR stress, within 2 ULP output-rounding bound)":"")<<'\n';
            if(!test.cancellationStress)
            {
                F4 backgroundLoss{0.11f,0.07f,0.19f,0.31f};auto blended=render(false,false,&backgroundLoss);
                for(U pixel=0;pixel<16;++pixel)for(U component=0;component<3;++component)
                {
                    float alpha=observedPixels[pixel][3];
                    float expected=test.barrier?0:observedPixels[pixel][component]+(1-alpha)*backgroundLoss[component];
                    Require(std::abs(blended[pixel][component]-expected)<0.000001f,"Loss-layer blend does not preserve source-over attenuation");
                    if(test.barrier)
                        Require(blended[pixel][component]==0,"Unsupported foreground must erase previously accumulated loss");
                    else if(test.alpha!=9)
                    {
                        float nativeBackground=0.8f+backgroundLoss[component];
                        float corrected=fullPixels[pixel][component]+(1-alpha)*nativeBackground-blended[pixel][component];
                        float retained=zeroPixels[pixel][component]+(1-alpha)*0.8f;
                        Require(std::abs(corrected-retained)<0.00001f,"Known foreground correction changes retained native reflection/emission/background");
                    }
                }
            }
        }
        std::cout << "PASS: " << cases.size()*selections.size() << " native/candidate pixel-stage cases using synthetic GPU inputs; max RGB error="
                  << maximumRgbError << ", max alpha error=" << maximumAlphaError << "; extreme-HDR cancellation error="<<maximumCancellationError<<'\n';
        return 0;
    }
    catch (const std::exception& exception) { std::cerr << "FAIL: " << exception.what() << '\n'; return 1; }
}
