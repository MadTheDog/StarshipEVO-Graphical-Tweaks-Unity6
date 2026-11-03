// Native bytecode is an ignored local input. All geometry/resources are synthetic.
#include "../GlassSurface.GpuTests/FixtureResources.h"
#include <algorithm>
struct Vertex { float p[3], n[3], t[4], b[3], uv[2]; U material; float center[3], size[3], minimum[3], maximum[3], offset[3]; };
struct Capture { F4 clip, normal, world, uv, material; };
static_assert(sizeof(Vertex) == 124 && sizeof(Capture) == 80, "Geometry GPU ABI");
struct GeometryCase
{
    std::string name; int resize = 0, recompute = 0, rotation = 0, mappingStart = 0, dynamic = 0;
    U counts = 0; float unit = 1; bool wedge = false, inactive = false, wedgeSamples = false;
    F4 position{1, 2, 3, 0};
    std::array<F4,8> dynamicVectors{};
    float dynamicScalar = 0.75f;
    int pixel = 0;
    int alpha = 2;
    U baseMaterial = 0;
    float resizeUnit = 1;
    F4 boundsCenter{0.5f,0.5f,0.5f,0}, boundsSize{1,1,1,0};
    std::array<F4, 4> entity{F4{1, 0, 0, 0}, F4{0, 1, 0, 0}, F4{0, 0, 1, 0}, F4{0, 0, 0, 1}};
    std::array<F4, 4> projection{F4{1, 0, 0, 0}, F4{0, 1, 0, 0}, F4{0, 0, 1, 0}, F4{0, 0, 0, 1}};
};
int main(int argc, char** argv)
{
    try
    {
        Require(argc == 5, "Usage: GlassGeometryGpu nativeVS candidateVS nativeGS candidateGS");
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &requested, 1, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<ID3D11VertexShader> native, candidate; ComPtr<ID3D11ShaderReflection> reflection;
        auto bytes = Bytes(argv[1]); Check(device->CreateVertexShader(bytes.data(), bytes.size(), nullptr, &native));
        bytes = Bytes(argv[2]); Check(device->CreateVertexShader(bytes.data(), bytes.size(), nullptr, &candidate));
        Check(D3DReflect(bytes.data(), bytes.size(), IID_PPV_ARGS(&reflection)));
        D3D11_SO_DECLARATION_ENTRY declaration[] = {{0,"CAPTURE",0,0,4,0},{0,"CAPTURE",1,0,4,0},{0,"CAPTURE",2,0,4,0},{0,"CAPTURE",3,0,4,0},{0,"CAPTURE",4,0,4,0}};
        U stride = sizeof(Capture); ComPtr<ID3D11GeometryShader> nativeCapture, candidateCapture;
        bytes = Bytes(argv[3]); Check(device->CreateGeometryShaderWithStreamOutput(bytes.data(), bytes.size(), declaration, 5, &stride, 1,
            D3D11_SO_NO_RASTERIZED_STREAM, nullptr, &nativeCapture));
        bytes = Bytes(argv[4]); Check(device->CreateGeometryShaderWithStreamOutput(bytes.data(), bytes.size(), declaration, 5, &stride, 1,
            D3D11_SO_NO_RASTERIZED_STREAM, nullptr, &candidateCapture));
        D3D11_BUFFER_DESC outputDesc{}; outputDesc.ByteWidth = 36 * sizeof(Capture); outputDesc.Usage = D3D11_USAGE_DEFAULT;
        outputDesc.BindFlags = D3D11_BIND_STREAM_OUTPUT; ComPtr<ID3D11Buffer> output, staging;
        Check(device->CreateBuffer(&outputDesc, nullptr, &output));
        outputDesc.Usage = D3D11_USAGE_STAGING; outputDesc.BindFlags = 0; outputDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Check(device->CreateBuffer(&outputDesc, nullptr, &staging));
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);

        std::array<F4, 64 * 4> rotations{};
        for (U r = 0; r < 64; ++r) for (U column = 0; column < 4; ++column) rotations[r * 4 + column][column] = 1;
        rotations[4] = F4{0, 0, -1, 0}; rotations[5] = F4{0, 1, 0, 0}; rotations[6] = F4{1, 0, 0, 0};
        std::vector<GeometryCase> cases; GeometryCase current;
        current.name = "unit cube"; cases.push_back(current);
        for (int mode = 0; mode <= 4; ++mode)
        {
            current = GeometryCase{}; current.resize = mode; current.counts = 2 | (3 << 4) | (4 << 8);
            current.name = "resize mode " + std::to_string(mode); cases.push_back(current);
        }
        current = GeometryCase{}; current.rotation = 1; current.counts = 1 | (2 << 4) | (3 << 8);
        current.name = "quarter rotation and resize"; cases.push_back(current);
        current.name = "quarter rotation and quarter unit"; current.unit = 0.25f; cases.push_back(current);
        current = GeometryCase{}; current.wedge = true; current.recompute = 1; current.counts = 1 | (2 << 4) | (4 << 8);
        current.name = "wedge recomputed normal"; cases.push_back(current);
        current.rotation = 1; current.name = "rotated wedge normal"; cases.push_back(current);
        current.entity = {F4{0,0,-2,0},F4{0,3,0,0},F4{4,0,0,0},F4{7,11,13,1}};
        current.name = "rotated wedge and nonuniform entity transform"; cases.push_back(current);
        current.projection = {F4{1.7f,0,0,0},F4{0,2.3f,0,0},F4{0.1f,0.2f,0.8f,1},F4{3,4,0.5f,0}};
        current.name = "nontrivial view projection"; cases.push_back(current);
        current.mappingStart = 2; current.name = "mapping range offset"; cases.push_back(current);
        current = GeometryCase{}; current.inactive = true; current.name = "inactive cluster"; cases.push_back(current);
        for(int mode : {1,2,3,4,5,6,7})
        {
            current=GeometryCase{};current.dynamic=mode;current.name="vertex dynamic mode "+std::to_string(mode);
            current.dynamicVectors[0]=F4{0.2f,0.3f,0,0};
            if(mode==4)
            {
                current.dynamicVectors[0]=F4{0,0,1,0};current.dynamicVectors[1]=F4{0,1,0,0};
                current.dynamicVectors[2]=F4{-1,0,0,0};current.dynamicVectors[3]=F4{0.1f,0.2f,0.3f,0};
            }
            cases.push_back(current);
        }
        for(int mode : {7,8})
        {current=GeometryCase{};current.resize=mode;current.counts=2|(3<<4)|(4<<8);current.name="radius linkage "+std::to_string(mode);cases.push_back(current);}
        for(int selection : {1, 2, 3}) for(float offset : {3.f, 3.75f, -2.f, -2.75f})
        {
            current=GeometryCase{};current.baseMaterial=8;current.dynamic=selection==2?0:1;current.pixel=selection==1?0:11;
            current.dynamicVectors[0]=F4{0.2f,0.3f,offset,0};current.dynamicVectors[1]=F4{-0.4f,0.7f,offset,0};
            current.resize=6;current.counts=2|(3<<4)|(4<<8);current.wedge=true;current.recompute=1;
            current.name="resolved material selection "+std::to_string(selection)+" offset "+std::to_string(offset);cases.push_back(current);
            if(offset==3.75f)
            {
                current.alpha=10;current.name+=" additive base alpha 10";cases.push_back(current);
            }
        }
        for(int mode : {5,6}) for(U counts : {0u, 2u|(3u<<4)|(4u<<8), 14u|(1u<<4)|(2u<<8), 1u|(14u<<4)})
        {
            current=GeometryCase{};current.resize=mode;current.counts=counts;current.wedgeSamples=true;
            current.name="wedge linkage "+std::to_string(mode)+" counts "+std::to_string(counts);cases.push_back(current);
            current.boundsCenter=F4{-0.3f,0.7f,-0.2f,0};current.boundsSize=F4{2,0.5f,1.5f,0};
            current.resizeUnit=0.25f;current.unit=0.25f;current.rotation=1;current.wedge=true;current.recompute=1;
            current.name+=" asymmetric bounds quarter unit";cases.push_back(current);
        }
        for(int resize=0;resize<=8;++resize)for(int selection : {0,1,2,3})for(U counts : {2u|(3u<<4)|(4u<<8),4u|(1u<<4)|(2u<<8),2u|(1u<<4)|(2u<<8)})
        {
            current=GeometryCase{};current.alpha=20;current.resize=resize;current.counts=counts;current.baseMaterial=8;
            current.dynamic=(selection==1||selection==3)?1:0;current.pixel=selection>=2?11:0;
            current.dynamicVectors[0]=F4{0.2f,0.3f,-2.75f,0};current.dynamicVectors[1]=F4{-0.4f,0.7f,3.75f,0};
            current.boundsCenter=F4{-0.3f,0.7f,-0.2f,0};current.boundsSize=F4{2,0.5f,1.5f,0};
            current.resizeUnit=0.5f;current.unit=0.25f;current.rotation=1;current.wedge=true;current.recompute=1;
            current.entity={F4{0,0,-2,0},F4{0,3,0,0},F4{4,0,0,0},F4{7,11,13,1}};
            current.name="squared hologram resize "+std::to_string(resize)+" selection "+std::to_string(selection)+" counts "+std::to_string(counts);
            cases.push_back(current);
        }
        float maximumError = 0;
        for (const auto& test : cases)
        {
            std::vector<Vertex> source(39);
            for (U i = 0; i < 36; ++i)
            {
                auto& v = source[3 + i]; v.p[0] = float(i % 3) / 2; v.p[1] = float((i / 3) % 3) / 2; v.p[2] = float((i / 9) % 3) / 2;
                v.material=test.baseMaterial;
                if(test.wedgeSamples)
                {
                    // Both diagonal halves, the lower-edge threshold and
                    // points slightly outside the bounds; no game mesh input.
                    const float samples[]={-0.02f,0,0.01f,0.25f,0.75f,1.02f};
                    v.p[0]=samples[i%6];v.p[1]=samples[i/6];
                }
                U axis = (i / 6) / 2; v.n[axis] = (i / 6) % 2 ? -1.f : 1.f;
                if (test.wedge) { v.n[0] = v.n[1] = 0.70710677f; v.n[2] = 0; }
                v.t[0] = v.t[3] = 1; v.b[1] = 1; v.uv[0] = float(i%3)/2; v.uv[1]=float((i/3)%3)/2;
                for (int j = 0; j < 3; ++j)
                {
                    v.p[j]=test.boundsCenter[j]+(v.p[j]-0.5f)*test.boundsSize[j];
                    v.center[j]=test.boundsCenter[j];v.size[j]=test.boundsSize[j];v.maximum[j]=1;
                }
            }
            Material material{}; material.alpha = test.alpha; material.border = material.normal = material.borderNormal = material.borderSpecular = -1;
            material.resize = test.resize; material.normalMode = test.recompute; material.unit=test.resizeUnit;material.uvScale[0] = material.uvScale[1] = 1;
            material.vertex=test.dynamic;material.path[0]=0.4f;material.path[1]=0.5f;material.path[2]=0.6f;
            material.pixel=test.pixel;
            material.geoHigh = 0.001f; material.geoLow = -0.001f; material.uLow = material.vLow = 0.45f; material.uHigh = material.vHigh = 0.55f;
            Build build{}; build.packed = test.rotation; build.counts = test.counts; build.unit = test.unit; build.health = 1;
            build.pixel=1;
            // Every alternate material has deliberately incompatible geometry.
            // Resolving a shading index must never reload these vertex controls.
            Material alternate=material;alternate.alpha=20;alternate.vertex=8;alternate.resize=99;alternate.unit=7;
            std::array<Material,32> materials;materials.fill(alternate);materials[test.baseMaterial]=material;
            std::memcpy(build.position, test.position.data(), 12);
            U mapping[6]{}; mapping[test.mappingStart * 2] = test.inactive ? 0 : 1;
            auto vector=test.dynamicVectors; float scalar[8]{};scalar[0]=test.dynamicScalar;
            std::map<std::string, Resource> resources;
            resources.emplace("VectorDynData", Structured(device.Get(), 8, 16, vector.data()));
            resources.emplace("FloatDynData", Structured(device.Get(), 8, 4, scalar));
            resources.emplace("_TransformDatasBuffer", Structured(device.Get(), 1, 64, test.entity.data()));
            resources.emplace("MaterialParams", Structured(device.Get(), U(materials.size()), sizeof(Material), materials.data()));
            resources.emplace("BrickBuildData", Structured(device.Get(), 1, sizeof(Build), &build));
            resources.emplace("TriBrickIDMapping", Structured(device.Get(), 3, 8, mapping));
            resources.emplace("ShapeMeshData", Structured(device.Get(), U(source.size()), sizeof(Vertex), source.data()));
            auto run = [&](bool original)
            {
                std::vector<ComPtr<ID3D11Buffer>> constants;
                ID3D11ShaderResourceView* nullResources[16]{}; context->VSSetShaderResources(0, 16, nullResources);
                ID3D11Buffer* nullConstants[14]{}; context->VSSetConstantBuffers(0, 14, nullConstants);
                auto bind = [&](U slot, const void* data, U size)
                { auto buffer = Constants(device.Get(), data, size); ID3D11Buffer* pointer = buffer.Get(); context->VSSetConstantBuffers(slot, 1, &pointer); constants.push_back(buffer); };
                if (original)
                {
                    std::vector<F4> globals(545); std::memcpy(globals.data() + 18, rotations.data(), sizeof(rotations));
                    std::memcpy(&globals[534][0], &test.mappingStart, 4); globals[544][0] = 1;
                    std::vector<F4> camera(5), lighting(46), draw(10), frame(21);
                    camera[4] = F4{0,0,1,0}; for (int i = 0; i < 4; ++i) draw[i][i] = 1; draw[9][3] = 1;
                    std::memcpy(frame.data() + 17, test.projection.data(), 64);
                    bind(0,globals.data(),U(globals.size()*16)); bind(1,camera.data(),U(camera.size()*16)); bind(2,lighting.data(),U(lighting.size()*16));
                    bind(3,draw.data(),U(draw.size()*16)); bind(4,frame.data(),U(frame.size()*16));
                    const char* names[] = {"VectorDynData","FloatDynData","_TransformDatasBuffer","MaterialParams","BrickBuildData","TriBrickIDMapping","ShapeMeshData"};
                    for (U slot = 0; slot < 7; ++slot) { auto view = resources.at(names[slot]).view.Get(); context->VSSetShaderResources(slot,1,&view); }
                    context->VSSetShader(native.Get(),nullptr,0); context->GSSetShader(nativeCapture.Get(),nullptr,0);
                }
                else
                {
                    D3D11_SHADER_DESC desc{}; Check(reflection->GetDesc(&desc));
                    for (U resource = 0; resource < desc.BoundResources; ++resource)
                    {
                        D3D11_SHADER_INPUT_BIND_DESC binding{}; Check(reflection->GetResourceBindingDesc(resource,&binding));
                        if (binding.Type == D3D_SIT_CBUFFER)
                        {
                            auto cb = reflection->GetConstantBufferByName(binding.Name); D3D11_SHADER_BUFFER_DESC cbDesc{}; Check(cb->GetDesc(&cbDesc));
                            std::vector<char> data(cbDesc.Size);
                            for (U i = 0; i < cbDesc.Variables; ++i)
                            {
                                D3D11_SHADER_VARIABLE_DESC variable{}; Check(cb->GetVariableByIndex(i)->GetDesc(&variable)); std::string name = variable.Name;
                                auto put = [&](const void* value,size_t size){std::memcpy(data.data()+variable.StartOffset,value,size);};
                                if (name == "VoxelRotations") put(rotations.data(),sizeof(rotations));
                                else if (name == "ClusterMappingStartIndex") put(&test.mappingStart,4);
                                else if (name == "unity_MatrixVP") put(test.projection.data(),64);
                                else if (name == "unity_ObjectToWorld") { float identity[16]{}; identity[0]=identity[5]=identity[10]=identity[15]=1; put(identity,64); }
                                else if (name == "unity_WorldTransformParams") { F4 transform{0,0,0,1}; put(transform.data(),16); }
                            }
                            bind(binding.BindPoint,data.data(),cbDesc.Size);
                        }
                        else { auto found = resources.find(binding.Name); if(found!=resources.end()){auto view=found->second.view.Get();context->VSSetShaderResources(binding.BindPoint,1,&view);} }
                    }
                    context->VSSetShader(candidate.Get(),nullptr,0); context->GSSetShader(candidateCapture.Get(),nullptr,0);
                }
                U offset=0; ID3D11Buffer* pointer=output.Get(); context->SOSetTargets(1,&pointer,&offset); context->DrawInstanced(36,1,0,0);
                pointer=nullptr;context->SOSetTargets(1,&pointer,&offset); context->CopyResource(staging.Get(),output.Get());
                D3D11_MAPPED_SUBRESOURCE mapped{};Check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
                std::vector<Capture> values(36);std::memcpy(values.data(),mapped.pData,36*sizeof(Capture));context->Unmap(staging.Get(),0);return values;
            };
            auto expected=run(true),observed=run(false);
            if(test.baseMaterial!=0)
            {
                U selected=test.baseMaterial;
                if(test.dynamic==1)selected=U(float(selected)+test.dynamicVectors[0][2]);
                if(test.pixel==11)selected=U(float(selected)+test.dynamicVectors[1][2]);
                Require(expected[0].material[0]==float(selected),"Native selected-material fixture did not exercise the intended nonzero index");
            }
            if(test.name=="unit cube")
                Require(expected[0].world==F4{1,2,3,1} && expected[0].normal==F4{1,0,0,0},
                    "Native geometry reference does not contain the expected nonzero transform/normal");
            for(U vertex=0;vertex<36;++vertex)for(U component=0;component<20;++component)
            {
                if(test.inactive && component==19)continue;
                const float* e=reinterpret_cast<const float*>(&expected[vertex]); const float* o=reinterpret_cast<const float*>(&observed[vertex]);
                float error=std::abs(e[component]-o[component]);maximumError=std::max(maximumError,error);
                if(!std::isfinite(e[component])||!std::isfinite(o[component])||error>0.00001f*std::max(1.f,std::abs(e[component])))
                {std::cerr<<"FAIL "<<test.name<<" vertex "<<vertex<<" component "<<component<<": native="<<e[component]<<" candidate="<<o[component]<<'\n';return 1;}
            }
            std::cout<<"PASS "<<test.name<<'\n';
        }
        std::cout<<"PASS: "<<cases.size()<<" native/candidate geometry cases, 36 vertices each; max component error="<<maximumError<<'\n';return 0;
    }
    catch(const std::exception& exception){std::cerr<<"FAIL: "<<exception.what()<<'\n';return 1;}
}
