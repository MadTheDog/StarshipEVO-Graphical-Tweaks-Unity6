// Standalone D3D11 WARP tests: entirely handmade synthetic inputs, no game assets.
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <array>
#include <vector>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <cstring>
#include <cstdint>
#include <algorithm>
using Microsoft::WRL::ComPtr;
using U=uint32_t;
static unsigned checks=0;
static void Check(HRESULT hr){if(FAILED(hr))throw std::runtime_error("D3D11 HRESULT "+std::to_string(U(hr)));}
static void Require(bool ok,const char* message){++checks;if(!ok)throw std::runtime_error(message);}
struct Record {U word[31];};
struct Pair {U x,y;};
struct StaticDescriptor {U base,stride,mask,source;};
struct CustomDescriptor {U source,destination,stride,mask;};
struct Params
{
    U sourceVertices,ownedVertices,workStart,workCount;
    U sourceStart,stride,variantBase,validMask;
    U inputStart,outputStart,mappingCount,maskCount;
    U lookupCount,customCount,tailStart,reserved;
};
static_assert(sizeof(Record)==124&&sizeof(Pair)==8&&sizeof(StaticDescriptor)==16&&sizeof(CustomDescriptor)==16&&sizeof(Params)==64,"GPU ABI");
struct Buffer {ComPtr<ID3D11Buffer> data;ComPtr<ID3D11ShaderResourceView> read;ComPtr<ID3D11UnorderedAccessView> write;U count,stride;};
static U ActualStride(const Buffer& buffer){D3D11_BUFFER_DESC desc{};buffer.data->GetDesc(&desc);return desc.StructureByteStride;}
static Buffer MakeBuffer(ID3D11Device* device,U count,U stride,const void* initial,bool writable)
{
    D3D11_BUFFER_DESC desc{};desc.ByteWidth=count*stride;desc.Usage=D3D11_USAGE_DEFAULT;
    desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|(writable?D3D11_BIND_UNORDERED_ACCESS:0);
    desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;desc.StructureByteStride=stride;
    D3D11_SUBRESOURCE_DATA data{};data.pSysMem=initial;Buffer result{};result.count=count;result.stride=stride;
    Check(device->CreateBuffer(&desc,initial?&data:nullptr,&result.data));
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{};srv.Format=DXGI_FORMAT_UNKNOWN;srv.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;srv.Buffer.NumElements=count;
    Check(device->CreateShaderResourceView(result.data.Get(),&srv,&result.read));
    if(writable){D3D11_UNORDERED_ACCESS_VIEW_DESC uav{};uav.Format=DXGI_FORMAT_UNKNOWN;uav.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;uav.Buffer.NumElements=count;Check(device->CreateUnorderedAccessView(result.data.Get(),&uav,&result.write));}
    return result;
}
struct Gpu
{
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> remap;ComPtr<ID3D11Buffer> constants;
    explicit Gpu(const char* path)
    {
        D3D_FEATURE_LEVEL requested=D3D_FEATURE_LEVEL_11_0,actual{};
        Check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,&requested,1,D3D11_SDK_VERSION,&device,&actual,&context));
        Require(actual>=requested,"SM5 unavailable");
        std::ifstream stream(path,std::ios::binary);Require(stream.good(),"Bytecode missing");
        std::vector<char> bytes((std::istreambuf_iterator<char>(stream)),std::istreambuf_iterator<char>());
        Check(device->CreateComputeShader(bytes.data(),bytes.size(),nullptr,&remap));
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=sizeof(Params);desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        Check(device->CreateBuffer(&desc,nullptr,&constants));
    }
    template<class T> Buffer BufferOf(const std::vector<T>& values,bool writable=false)
    {return MakeBuffer(device.Get(),U(values.size()),sizeof(T),values.data(),writable);}
    template<class T> std::vector<T> Read(const Buffer& buffer)
    {
        Require(sizeof(T)==buffer.stride,"Readback ABI mismatch");D3D11_BUFFER_DESC desc{};desc.ByteWidth=buffer.count*buffer.stride;
        desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Buffer> staging;
        Check(device->CreateBuffer(&desc,nullptr,&staging));context->CopyResource(staging.Get(),buffer.data.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};Check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        std::vector<T> values(buffer.count);std::memcpy(values.data(),mapped.pData,values.size()*sizeof(T));context->Unmap(staging.Get(),0);return values;
    }
    template<class T> void Upload(const Buffer& buffer,const std::vector<T>& values)
    {Require(values.size()==buffer.count&&sizeof(T)==buffer.stride,"Upload ABI mismatch");context->UpdateSubresource(buffer.data.Get(),0,nullptr,values.data(),0,0);}
    void Dispatch(const Params& p,std::array<ID3D11ShaderResourceView*,7> reads,std::array<ID3D11UnorderedAccessView*,3> writes,U threads=129)
    {
        context->UpdateSubresource(constants.Get(),0,nullptr,&p,0,0);ID3D11Buffer* cb=constants.Get();context->CSSetConstantBuffers(0,1,&cb);
        context->CSSetShader(remap.Get(),nullptr,0);context->CSSetShaderResources(0,7,reads.data());context->CSSetUnorderedAccessViews(0,3,writes.data(),nullptr);
        context->Dispatch((threads+63)/64,1,1);reads.fill(nullptr);writes.fill(nullptr);
        context->CSSetShaderResources(0,7,reads.data());context->CSSetUnorderedAccessViews(0,3,writes.data(),nullptr);
    }
};
int main(int argc,char** argv)
{
    try
    {
        Require(argc==2,"Usage: GlassPartialRemapGpu remap-v2.dxbc");Gpu gpu(argv[1]);
        std::vector<Record> records(72*3),ownedRecords(264*3);
        for(U v=0;v<records.size();++v)for(U w=0;w<31;++w)records[v].word[w]=v*31+w;
        for(U v=0;v<ownedRecords.size();++v)for(U w=0;w<31;++w)ownedRecords[v].word[w]=0xdead0000+v*31+w;
        std::vector<StaticDescriptor> lookup(72);lookup[5]={72,12,3,5};lookup[25]={120,24,3,25};lookup[37]={132,24,3,25};
        std::vector<CustomDescriptor> custom{{5,216,12,0},{25,228,24,1}};
        std::vector<Pair> masks{{0x100,5},{0x201,25}};
        std::vector<Pair> input{{5,0},{37,1},{25,1}};
        std::vector<U> clips{0x7fc01234,0x80000000,0xffffffff};
        Buffer source=gpu.BufferOf(records),owned=gpu.BufferOf(ownedRecords,true),lookupBuffer=gpu.BufferOf(lookup),customBuffer=gpu.BufferOf(custom),
            maskBuffer=gpu.BufferOf(masks),mapping=gpu.BufferOf(input),clip=gpu.BufferOf(clips);
        std::vector<Pair> empty(8,Pair{0xdead,0xcafe});std::vector<U> emptyClip(8,0xdeadbeef);
        Buffer output=gpu.BufferOf(empty,true),outputClip=gpu.BufferOf(emptyClip,true);
        Params p{};p.sourceVertices=216;p.ownedVertices=792;p.mappingCount=3;p.maskCount=2;p.lookupCount=72;p.customCount=2;p.tailStart=216;
        gpu.Dispatch(p,{source.read.Get(),nullptr,lookupBuffer.read.Get(),maskBuffer.read.Get(),mapping.read.Get(),clip.read.Get(),customBuffer.read.Get()},
            {owned.write.Get(),output.write.Get(),outputClip.write.Get()});
        auto actual=gpu.Read<Pair>(output);auto actualClip=gpu.Read<U>(outputClip);
        Require(actual[0].x==216&&actual[0].y==0,"valid custom slot with low mask0 must redirect the first native cluster");
        Require(actual[1].x==240&&actual[1].y==1&&actual[2].x==228&&actual[2].y==1,"custom two-cluster source preserves native input order and original build IDs");
        Require(actualClip[0]==0x7fc01234&&actualClip[1]==0x80000000&&actualClip[2]==0xffffffff,"raw native clip bits must survive");

        const std::vector<Pair> redirected{{216,0},{240,1},{228,1}},fallback{{5,0},{156,1},{144,1}};
        // Test-harness binding contract, NOT shader self detection or a test of
        // the future C# host. FXC makes GetDimensions stride a declared constant.
        auto hostAbiCompatible=[&]()
        {
            return ActualStride(source)==124&&ActualStride(owned)==124&&ActualStride(lookupBuffer)==16&&ActualStride(maskBuffer)==8&&
                ActualStride(mapping)==8&&ActualStride(clip)==4&&ActualStride(customBuffer)==16&&ActualStride(output)==8&&ActualStride(outputClip)==4;
        };
        auto run=[&](const char* label,const Params& rp,const std::vector<Pair>& expected,const std::vector<U>& expectedClips,U destination=0)
        {
            Require(expected.size()==expectedClips.size(),"Expected fixture list lengths differ");
            gpu.Upload(output,std::vector<Pair>(output.count,Pair{0xdead,0xcafe}));
            gpu.Upload(outputClip,std::vector<U>(outputClip.count,0xdeadbeef));
            gpu.Dispatch(rp,{source.read.Get(),nullptr,lookupBuffer.read.Get(),maskBuffer.read.Get(),mapping.read.Get(),clip.read.Get(),customBuffer.read.Get()},
                {owned.write.Get(),output.write.Get(),outputClip.write.Get()});
            auto values=gpu.Read<Pair>(output);auto bits=gpu.Read<U>(outputClip);
            for(U i=0;i<values.size();++i)
            {
                Pair want=i>=destination&&i-destination<expected.size()?expected[i-destination]:Pair{0xdead,0xcafe};
                if(values[i].x!=want.x||values[i].y!=want.y)throw std::runtime_error(std::string(label)+": mapping/sentinel mismatch at "+std::to_string(i));
                ++checks;
            }
            for(U i=0;i<bits.size();++i)
            {
                U want=i>=destination&&i-destination<expectedClips.size()?expectedClips[i-destination]:0xdeadbeef;
                if(bits[i]!=want)throw std::runtime_error(std::string(label)+": clip/sentinel mismatch at "+std::to_string(i));
                ++checks;
            }
        };
        run("initial valid redirects",p,redirected,clips);
        Params q=p;q.outputStart=3;run("offset output preserves prefix and suffix",q,redirected,clips,3);
        q=p;q.inputStart=2;q.mappingCount=129;run("input ends before declared count",q,{{228,1}},{0xffffffff});
        q=p;q.mappingCount=2;run("declared count limits excess dispatch",q,{{216,0},{240,1}},{0x7fc01234,0x80000000});
        q=p;q.mappingCount=0;run("zero work writes nothing",q,{},{});
        q=p;q.mappingCount=129;run("real input capacities limit excess dispatch",q,redirected,clips);
        q=p;q.outputStart=7;q.mappingCount=129;run("real output end limits both writes",q,{{216,0}},{0x7fc01234},7);
        q=p;q.inputStart=0xfffffff0;run("overflowing input offset",q,{},{});
        q=p;q.outputStart=0xfffffff0;run("overflowing output offset",q,{},{});
        std::cout<<"PASS: valid low0/nonzero custom slots, second clusters, original order/IDs/clip bits and guarded compact writes\n";

        // Every expected fallback is hand-derived from the two static sources:
        // A source5/stride12/base72, B source25/stride24/base120. B mask1 maps
        // its first/second clusters to144/156; no CPU shader clone is used.
        for(U mode=0;mode<3;++mode)
        {
            q=p;q.customCount=mode==0?0:mode==1?3:257;
            run("invalid declared custom count",q,fallback,clips);
        }
        q=p;q.customCount=1;run("active custom count truncates slot2",q,{{216,0},{156,1},{144,1}},clips);
        Buffer dummyCustom=gpu.BufferOf(std::vector<CustomDescriptor>(1));std::swap(customBuffer,dummyCustom);
        q=p;q.customCount=0;run("zero active count uses harmless dummy descriptor",q,fallback,clips);
        run("actual descriptor count shorter than declaration",p,fallback,clips);std::swap(customBuffer,dummyCustom);
        Buffer oversizedCustom=gpu.BufferOf(std::vector<CustomDescriptor>(257));std::swap(customBuffer,oversizedCustom);
        run("actual descriptor count exceeds256",p,fallback,clips);std::swap(customBuffer,oversizedCustom);
        for(U stride:std::vector<U>{4,8,12,20})
        {
            std::vector<U> zero(2*stride/4);Buffer malformed=MakeBuffer(gpu.device.Get(),2,stride,zero.data(),false);
            std::swap(customBuffer,malformed);Require(!hostAbiCompatible(),"actual custom ABI mismatch must be rejected by host BEFORE dispatch");std::swap(customBuffer,malformed);
        }
        // Deliberately bypass the host contract ONCE: the first16bytes are a
        // fully valid descriptor, but its actual D3D11 stride is20. The GPU
        // redirects it instead of rejecting the ABI. This characterizes the
        // compiled-constant limitation rather than claiming shader rejection.
        std::vector<U> wrongStrideWords{5,216,12,0,0xdddd,25,228,24,1,0xdddd};
        Buffer validLeadingWrongStride=MakeBuffer(gpu.device.Get(),2,20,wrongStrideWords.data(),false);
        std::swap(customBuffer,validLeadingWrongStride);Require(!hostAbiCompatible(),"strong custom ABI fixture must violate actual host stride contract");
        q=p;q.mappingCount=1;run("characterization: unsupported actual stride20 is not GPU rejected",q,{{216,0}},{0x7fc01234});
        std::swap(customBuffer,validLeadingWrongStride);
        auto savedMasks=masks;
        for(U encoded:std::vector<U>{0x301,0xffffff01,0x80000001})
        {
            masks[0]={encoded,5};gpu.Upload(maskBuffer,masks);
            run("out of range custom slot falls back to low mask",p,{{84,0},{240,1},{228,1}},clips);
        }
        masks={{1,5},{2,25}};gpu.Upload(maskBuffer,masks);q=p;q.customCount=0;
        run("V2 static whole-face path without custom slots",q,{{84,0},{180,1},{168,1}},clips);
        masks=savedMasks;gpu.Upload(maskBuffer,masks);
        std::vector<CustomDescriptor> boundary(256);boundary[255]={5,216,12,0};
        Buffer boundaryBuffer=gpu.BufferOf(boundary);std::swap(customBuffer,boundaryBuffer);
        masks[0]={0x10000,5};gpu.Upload(maskBuffer,masks);q=p;q.customCount=256;
        run("highest permitted custom slot256",q,{{216,0},{156,1},{144,1}},clips);
        masks=savedMasks;gpu.Upload(maskBuffer,masks);std::swap(customBuffer,boundaryBuffer);
        std::cout<<"PASS: GPU descriptor counts/dummy0active/slot fallback; actual descriptor stride is separately host-gated\n";

        CustomDescriptor savedCustom=custom[0];
        std::vector<CustomDescriptor> invalidCustom{{0,216,12,0},{6,216,12,0},{5,0,12,0},{5,215,12,0},{5,217,12,0},
            {5,264,12,0},{5,265,12,0},{5,0xffffffff,12,0},{5,216,0,0},{5,216,13,0},{5,216,24,0},
            {5,216,0xfffffffc,0},{5,216,12,1},{5,216,12,256}};
        for(const auto& d:invalidCustom)
        {
            custom[0]=d;gpu.Upload(customBuffer,custom);run("invalid custom source/stride/mask/destination",p,{{5,0},{240,1},{228,1}},clips);
        }
        custom[0]=savedCustom;gpu.Upload(customBuffer,custom);
        savedCustom=custom[1];custom[1]={25,252,24,1};gpu.Upload(customBuffer,custom);
        run("short custom tail falls back for both clusters",p,{{216,0},{156,1},{144,1}},clips);
        custom[1]=savedCustom;gpu.Upload(customBuffer,custom);
        for(U tail:std::vector<U>{0,71,119,215,217,265,0xffffffff})
        {q=p;q.tailStart=tail;run("invalid custom tail start",q,fallback,clips);}
        q=p;q.tailStart=120;
        run("tail must cover each validated static descriptor extent",q,{{216,0},{156,1},{144,1}},clips);
        masks[0]={0x108,5};gpu.Upload(maskBuffer,masks);run("bad low mask cannot authorize custom",p,{{5,0},{240,1},{228,1}},clips);
        masks[0]={0x100,25};gpu.Upload(maskBuffer,masks);run("recycled build source identity fails before custom",p,{{5,0},{240,1},{228,1}},clips);
        masks=savedMasks;gpu.Upload(maskBuffer,masks);
        std::cout<<"PASS: custom identity, full mask, exact stride, tail extent/alignment and uint overflow guards\n";

        StaticDescriptor savedLookup=lookup[5];
        std::vector<StaticDescriptor> invalidStatic{{72,12,0,5},{72,12,5,5},{72,12,511,5},{72,0,3,5},{72,13,3,5},
            {72,0xfffffffc,3,5},{71,12,3,5},{0xffffffff,12,3,5},{252,12,3,5},{72,12,3,0},{72,12,3,6}};
        for(const auto& d:invalidStatic)
        {
            lookup[5]=d;gpu.Upload(lookupBuffer,lookup);run("invalid static source proof retains native",p,{{5,0},{240,1},{228,1}},clips);
        }
        lookup[5]=savedLookup;gpu.Upload(lookupBuffer,lookup);
        for(U mode=0;mode<4;++mode)
        {
            q=p;if(mode==0)--q.sourceVertices;if(mode==1)--q.ownedVertices;if(mode==2)q.maskCount=0;if(mode==3)q.lookupCount=5;
            run("invalid declared source/lookup/mask dimensions",q,input,clips);
        }
        Buffer shortSource=gpu.BufferOf(std::vector<Record>(213));std::swap(source,shortSource);
        run("actual source vertex count differs from declared",p,input,clips);std::swap(source,shortSource);
        Buffer shortOwned=gpu.BufferOf(std::vector<Record>(216),true);std::swap(owned,shortOwned);
        run("actual owned vertex count differs from declared",p,input,clips);std::swap(owned,shortOwned);
        Buffer partialSource=gpu.BufferOf(std::vector<Record>(217));std::swap(source,partialSource);q=p;q.sourceVertices=217;
        run("nontriangle source count rejected despite matching declaration",q,input,clips);std::swap(source,partialSource);
        Buffer partialOwned=gpu.BufferOf(std::vector<Record>(793),true);std::swap(owned,partialOwned);q=p;q.ownedVertices=793;
        run("nontriangle owned count rejected despite matching declaration",q,input,clips);std::swap(owned,partialOwned);
        Buffer shortLookup=gpu.BufferOf(std::vector<StaticDescriptor>(lookup.begin(),lookup.begin()+6));std::swap(lookupBuffer,shortLookup);
        run("actual short source lookup",p,{{216,0},{37,1},{25,1}},clips);std::swap(lookupBuffer,shortLookup);
        Buffer shortMasks=gpu.BufferOf(std::vector<Pair>{masks[0]});std::swap(maskBuffer,shortMasks);
        run("actual short build masks",p,{{216,0},{37,1},{25,1}},clips);std::swap(maskBuffer,shortMasks);
        for(U which=0;which<4;++which)
        {
            U count=which==0?source.count:which==1?owned.count:which==2?lookupBuffer.count:maskBuffer.count;
            U stride=which<2?4:which==2?12:16;std::vector<U> zero(count*stride/4);
            Buffer bad=MakeBuffer(gpu.device.Get(),count,stride,zero.data(),which==1);
            Buffer* target=which==0?&source:which==1?&owned:which==2?&lookupBuffer:&maskBuffer;
            std::swap(*target,bad);Require(!hostAbiCompatible(),"source/owned/lookup/mask ABI must be host rejected BEFORE dispatch");std::swap(*target,bad);
        }
        std::vector<Pair> unknown{{0,0},{6,0},{17,0},{36,1},{24,1},{72,1},{0xffffffff,0},{5,0xffffffff}};
        lookup[6]={73,12,3,5};lookup[17]={84,12,3,5};lookup[36]={131,24,3,25};lookup[24]={119,24,3,25};gpu.Upload(lookupBuffer,lookup);
        std::vector<U> unknownClip{0,0x80000000,0x7f800000,0x7fc01234,0xffffffff,1,2,3};
        Buffer unknownMapping=gpu.BufferOf(unknown),unknownClipBuffer=gpu.BufferOf(unknownClip);
        std::swap(mapping,unknownMapping);std::swap(clip,unknownClipBuffer);q=p;q.mappingCount=8;
        run("zero/unknown/misaligned/outside/highUINT source and build IDs",q,unknown,unknownClip);
        std::swap(mapping,unknownMapping);std::swap(clip,unknownClipBuffer);
        lookup[6]={};lookup[17]={};lookup[36]={};lookup[24]={};gpu.Upload(lookupBuffer,lookup);
        std::cout<<"PASS: GPU source counts/ranges/static extent/identity proof; unknown native inputs remain unchanged; ABI host checks remain mandatory\n";

        Buffer shortInputClip=gpu.BufferOf(std::vector<U>{clips[0],clips[1]});std::swap(clip,shortInputClip);
        run("short actual input clip limits mapping writes",p,{{216,0},{240,1}},{clips[0],clips[1]});std::swap(clip,shortInputClip);
        Buffer shortInputMapping=gpu.BufferOf(std::vector<Pair>{input[0]});std::swap(mapping,shortInputMapping);
        run("short actual input mapping limits clip writes",p,{{216,0}},{clips[0]});std::swap(mapping,shortInputMapping);
        Buffer shortOutputClip=gpu.BufferOf(std::vector<U>(2,0xdeadbeef),true);std::swap(outputClip,shortOutputClip);
        run("short actual output clip limits mapping writes",p,{{216,0},{240,1}},{clips[0],clips[1]});std::swap(outputClip,shortOutputClip);
        Buffer shortOutputMapping=gpu.BufferOf(std::vector<Pair>(2,Pair{0xdead,0xcafe}),true);std::swap(output,shortOutputMapping);
        run("short actual output mapping limits clip writes",p,{{216,0},{240,1}},{clips[0],clips[1]});std::swap(output,shortOutputMapping);
        for(U which=0;which<4;++which)
        {
            Buffer* target=which==0?&mapping:which==1?&clip:which==2?&output:&outputClip;
            U stride=which%2==0?4:8;std::vector<U> zero(target->count*stride/4);
            Buffer bad=MakeBuffer(gpu.device.Get(),target->count,stride,zero.data(),which>=2);
            std::swap(*target,bad);
            Require(!hostAbiCompatible(),"input/output compact ABI must be host rejected BEFORE dispatch");
            std::swap(*target,bad);
        }
        std::cout<<"PASS: actual input/output dimensions bound writes; host ABI gate required (FXC stride20 characterization redirects, not GPU rejection)\n";

        // Native prefix length73 is not divisible by12: both static/tail starts
        // retain that residue. Only destination-minus-tail alignment is valid.
        Buffer shiftedSource=gpu.BufferOf(std::vector<Record>(73*3)),shiftedOwned=gpu.BufferOf(std::vector<Record>(265*3),true);
        std::swap(source,shiftedSource);std::swap(owned,shiftedOwned);
        lookup[5]={73,12,3,5};lookup[25]={121,24,3,25};lookup[37]={133,24,3,25};gpu.Upload(lookupBuffer,lookup);
        custom={{5,217,12,0},{25,229,24,1}};gpu.Upload(customBuffer,custom);
        q=p;q.sourceVertices=219;q.ownedVertices=795;q.tailStart=217;
        run("relative tail alignment with nonaligned native prefix",q,{{217,0},{241,1},{229,1}},clips);
        std::swap(source,shiftedSource);std::swap(owned,shiftedOwned);
        auto originalAfter=gpu.Read<Record>(source),ownedAfter=gpu.Read<Record>(owned);
        Require(std::memcmp(originalAfter.data(),records.data(),records.size()*sizeof(Record))==0,"native shape records changed");
        Require(std::memcmp(ownedAfter.data(),ownedRecords.data(),ownedRecords.size()*sizeof(Record))==0,"remap modified owned shape records");
        auto mappingsAfter=gpu.Read<Pair>(mapping);auto clipsAfter=gpu.Read<U>(clip);
        Require(std::memcmp(mappingsAfter.data(),input.data(),input.size()*sizeof(Pair))==0&&clipsAfter==clips,"native mapping/clip buffers changed");
        std::cout<<"PASS: relative custom-tail alignment, complete native/owned source preservation and untouched output sentinels\n";
        std::cout<<"PASS: GlassPartialRemap V2 synthetic WARP prototype; checks="<<checks<<'\n';return 0;
    }
    catch(const std::exception& error){std::cerr<<"FAIL: "<<error.what()<<'\n';return 1;}
}
