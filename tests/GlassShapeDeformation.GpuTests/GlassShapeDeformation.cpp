// Reads ignored owner-local expanded records and CPU outputs; contains no mesh/native shader data.
#include "../GlassSurface.GpuTests/FixtureResources.h"
#include <algorithm>
#include <iomanip>
#include <limits>
struct Vertex { float p[3],n[3],t[4],b[3],uv[2];U material;float center[3],size[3],minimum[3],maximum[3],offset[3]; };
struct P3 { float x,y,z; };
static_assert(sizeof(Vertex)==124&&sizeof(P3)==12,"Fixture layout");
template<class T> T Read(std::ifstream& stream){T value{};stream.read(reinterpret_cast<char*>(&value),sizeof(value));Require(stream.good(),"Truncated fixture");return value;}
template<class T> std::vector<T> ReadArray(std::ifstream& stream,U count)
{std::vector<T> values(count);stream.read(reinterpret_cast<char*>(values.data()),std::streamsize(values.size()*sizeof(T)));Require(stream.good(),"Truncated fixture array");return values;}
static uint64_t Ulp(float a,float b)
{
    if(a==b)return 0;U x=0,y=0;std::memcpy(&x,&a,4);std::memcpy(&y,&b,4);
    int64_t ix=(x&0x80000000u)?int64_t(0x80000000u)-int64_t(x&0x7fffffffu):int64_t(0x80000000u)+x;
    int64_t iy=(y&0x80000000u)?int64_t(0x80000000u)-int64_t(y&0x7fffffffu):int64_t(0x80000000u)+y;
    return uint64_t(ix>iy?ix-iy:iy-ix);
}
int main(int argc,char** argv)
{
    try
    {
        Require(argc==4,"Usage: fixture vertexShader captureShader input.bin");
        std::ifstream input(argv[3],std::ios::binary);Require(input.good(),"Fixture missing");Require(Read<U>(input)==0x47534431u,"Fixture version");
        Require(Read<U>(input)==64,"Rotation count");auto rowMajor=ReadArray<float>(input,1024);std::array<float,1024> columns{};
        for(U r=0;r<64;r++)for(U row=0;row<4;row++)for(U col=0;col<4;col++)columns[r*16+col*4+row]=rowMajor[r*16+row*4+col];
        std::array<Material,1024> materials{};U materialCount=Read<U>(input);Require(materialCount<1024,"Material count bound");
        for(U i=0;i<materialCount;i++)
        {
            U id=Read<U>(input);Require(id<materials.size(),"Material ID bound");auto& m=materials[id];m.id=int(id);m.alpha=2;m.border=-1;
            m.resize=Read<int>(input);m.unit=Read<float>(input);m.geoLow=Read<float>(input);m.geoHigh=Read<float>(input);m.uvScale[0]=m.uvScale[1]=1;
        }
        std::map<U,std::vector<Vertex>> shapes;U shapeCount=Read<U>(input);Require(shapeCount==11,"Shape count");
        for(U i=0;i<shapeCount;i++){U id=Read<U>(input),count=Read<U>(input);Require(count>0&&count<=360,"Vertex count bound");shapes.emplace(id,ReadArray<Vertex>(input,count));}
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL requested=D3D_FEATURE_LEVEL_11_0;
        Check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,&requested,1,D3D11_SDK_VERSION,&device,nullptr,&context));
        auto bytes=Bytes(argv[1]);ComPtr<ID3D11VertexShader> vertex;Check(device->CreateVertexShader(bytes.data(),bytes.size(),nullptr,&vertex));
        ComPtr<ID3D11ShaderReflection> reflection;Check(D3DReflect(bytes.data(),bytes.size(),IID_PPV_ARGS(&reflection)));
        D3D11_SO_DECLARATION_ENTRY declaration{0,"CAPTURE",0,0,3,0};U stride=12;ComPtr<ID3D11GeometryShader> capture;
        bytes=Bytes(argv[2]);Check(device->CreateGeometryShaderWithStreamOutput(bytes.data(),bytes.size(),&declaration,1,&stride,1,D3D11_SO_NO_RASTERIZED_STREAM,nullptr,&capture));
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=360*12;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_STREAM_OUTPUT;
        ComPtr<ID3D11Buffer> output,staging;Check(device->CreateBuffer(&desc,nullptr,&output));desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;Check(device->CreateBuffer(&desc,nullptr,&staging));
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);context->VSSetShader(vertex.Get(),nullptr,0);context->GSSetShader(capture.Get(),nullptr,0);
        std::array<F4,4> identity{F4{1,0,0,0},F4{0,1,0,0},F4{0,0,1,0},F4{0,0,0,1}};std::array<F4,8> vectors{};std::array<float,8> scalars{};
        std::map<std::string,Resource> resources;
        resources.emplace("VectorDynData",Structured(device.Get(),8,16,vectors.data()));resources.emplace("FloatDynData",Structured(device.Get(),8,4,scalars.data()));
        resources.emplace("_TransformDatasBuffer",Structured(device.Get(),1,64,identity.data()));resources.emplace("MaterialParams",Structured(device.Get(),U(materials.size()),sizeof(Material),materials.data()));
        std::vector<ComPtr<ID3D11Buffer>> constants;D3D11_SHADER_DESC shaderDesc{};Check(reflection->GetDesc(&shaderDesc));
        for(U binding=0;binding<shaderDesc.BoundResources;binding++)
        {
            D3D11_SHADER_INPUT_BIND_DESC resource{};Check(reflection->GetResourceBindingDesc(binding,&resource));if(resource.Type!=D3D_SIT_CBUFFER)continue;
            auto cb=reflection->GetConstantBufferByName(resource.Name);D3D11_SHADER_BUFFER_DESC description{};Check(cb->GetDesc(&description));std::vector<char> data(description.Size);
            for(U v=0;v<description.Variables;v++)
            {
                D3D11_SHADER_VARIABLE_DESC variable{};Check(cb->GetVariableByIndex(v)->GetDesc(&variable));std::string name=variable.Name;
                if(name=="VoxelRotations")std::memcpy(data.data()+variable.StartOffset,columns.data(),sizeof(columns));
                else if(name=="unity_MatrixVP"||name=="unity_ObjectToWorld")std::memcpy(data.data()+variable.StartOffset,identity.data(),64);
                else if(name=="unity_WorldTransformParams"){F4 value{0,0,0,1};std::memcpy(data.data()+variable.StartOffset,value.data(),16);}
            }
            auto buffer=Constants(device.Get(),data.data(),description.Size);auto pointer=buffer.Get();context->VSSetConstantBuffers(resource.BindPoint,1,&pointer);constants.push_back(buffer);
        }
        U cases=Read<U>(input);Require(cases==1760,"Bounded case count");uint64_t exact=0,bitExact=0,total=0,vertices=0,maxUlp=0;double maxError=0,maxDistance=0;
        std::array<uint64_t,9> exactByMode{},totalByMode{};std::array<double,9> distanceByMode{};std::array<uint64_t,9> ulpByMode{};
        for(U c=0;c<cases;c++)
        {
            U id=Read<U>(input);auto found=shapes.find(id);Require(found!=shapes.end(),"Unknown fixture shape");auto& source=found->second;
            Build brick{};brick.packed=Read<U>(input);brick.counts=Read<U>(input);brick.unit=Read<float>(input);auto p=Read<P3>(input);std::memcpy(brick.position,&p,12);brick.health=1;
            auto expected=ReadArray<P3>(input,U(source.size()));std::vector<Vertex> padded(3+source.size());std::copy(source.begin(),source.end(),padded.begin()+3);U mapping[2]={1,0};
            resources["BrickBuildData"]=Structured(device.Get(),1,sizeof(Build),&brick);resources["TriBrickIDMapping"]=Structured(device.Get(),1,8,mapping);resources["ShapeMeshData"]=Structured(device.Get(),U(padded.size()),124,padded.data());
            for(U b=0;b<shaderDesc.BoundResources;b++)
            {D3D11_SHADER_INPUT_BIND_DESC binding{};Check(reflection->GetResourceBindingDesc(b,&binding));if(binding.Type!=D3D_SIT_CBUFFER){auto resource=resources.find(binding.Name);if(resource!=resources.end()){auto view=resource->second.view.Get();context->VSSetShaderResources(binding.BindPoint,1,&view);}}}
            U offset=0;auto pointer=output.Get();context->SOSetTargets(1,&pointer,&offset);context->DrawInstanced(U(source.size()),1,0,0);pointer=nullptr;context->SOSetTargets(1,&pointer,&offset);context->CopyResource(staging.Get(),output.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};Check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));auto observed=static_cast<const P3*>(mapped.pData);
            U mode=U(materials[source[0].material].resize);Require(mode==0||mode==7||mode==8,"Unexpected mode");
            for(U v=0;v<source.size();v++)
            {
                const float* e=reinterpret_cast<const float*>(&expected[v]);const float* o=reinterpret_cast<const float*>(&observed[v]);double squared=0;
                for(U j=0;j<3;j++){Require(std::isfinite(e[j])&&std::isfinite(o[j]),"Nonfinite comparison");double error=double(e[j])-o[j];squared+=error*error;maxError=std::max(maxError,std::abs(error));uint64_t ulp=Ulp(e[j],o[j]);maxUlp=std::max(maxUlp,ulp);ulpByMode[mode]=std::max(ulpByMode[mode],ulp);if(e[j]==o[j]){exact++;exactByMode[mode]++;}U eb=0,ob=0;std::memcpy(&eb,&e[j],4);std::memcpy(&ob,&o[j],4);if(eb==ob)bitExact++;total++;totalByMode[mode]++;}
                double distance=std::sqrt(squared);maxDistance=std::max(maxDistance,distance);distanceByMode[mode]=std::max(distanceByMode[mode],distance);vertices++;
            }
            context->Unmap(staging.Get(),0);
        }
        Require(input.peek()==std::ifstream::traits_type::eof(),"Trailing fixture data");
        std::cout<<std::setprecision(12)<<"WARP: cases="<<cases<<", vertices="<<vertices<<", numericallyExactComponents="<<exact<<"/"<<total<<", bitExactComponents="<<bitExact<<"/"<<total<<", differingComponents="<<total-exact<<", maxULP="<<maxUlp<<", maxAbsComponent="<<maxError<<", maxDistance="<<maxDistance<<'\n';
        for(U mode:{0u,7u,8u})std::cout<<"resizeMode="<<mode<<", exact="<<exactByMode[mode]<<"/"<<totalByMode[mode]<<", maxULP="<<ulpByMode[mode]<<", maxDistance="<<distanceByMode[mode]<<'\n';
        Require(maxDistance<=.01,"Diagnostic sanity bound exceeded; this is not an exact-match acceptance test");
        std::cout<<(exact==total?"All measured positions are numerically exact in this bounded CPU/WARP fixture. ":"Measured positions differ; no exact-parity claim. ")
            <<"Sanity bound0.01 is not a coverage tolerance or native runtime acceptance. DXBC mad may differ on other backends; CPU uses no explicit FMA.\n";return 0;
    }
    catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}
}
