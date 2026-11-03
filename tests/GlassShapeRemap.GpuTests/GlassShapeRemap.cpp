// Standalone D3D11 WARP verification. Every record and shape is synthetic.
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
using U = uint32_t;
static void Check(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("D3D11 HRESULT " + std::to_string(U(hr))); }
static void Require(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
struct Record { U word[31]; };
struct Pair { U x, y; };
struct Descriptor { U base, stride, mask, source; };
struct Params
{
    U sourceVertices, ownedVertices, workStart, workCount;
    U sourceStart, stride, variantBase, validMask;
    U inputStart, outputStart, mappingCount, maskCount;
    U lookupCount, reserved[3];
};
static_assert(sizeof(Record) == 124 && sizeof(Pair) == 8 && sizeof(Descriptor) == 16 && sizeof(Params) == 64, "GPU ABI");
struct Buffer { ComPtr<ID3D11Buffer> data; ComPtr<ID3D11ShaderResourceView> read; ComPtr<ID3D11UnorderedAccessView> write; U count; };
static Buffer MakeBuffer(ID3D11Device* device, U count, U stride, const void* initial, bool writable)
{
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = count * stride; desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (writable ? D3D11_BIND_UNORDERED_ACCESS : 0);
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; desc.StructureByteStride = stride;
    D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = initial;
    Buffer result{}; result.count = count; Check(device->CreateBuffer(&desc, initial ? &data : nullptr, &result.data));
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{}; srv.Format = DXGI_FORMAT_UNKNOWN;
    srv.ViewDimension = D3D11_SRV_DIMENSION_BUFFER; srv.Buffer.NumElements = count;
    Check(device->CreateShaderResourceView(result.data.Get(), &srv, &result.read));
    if (writable)
    {
        D3D11_UNORDERED_ACCESS_VIEW_DESC uav{}; uav.Format = DXGI_FORMAT_UNKNOWN;
        uav.ViewDimension = D3D11_UAV_DIMENSION_BUFFER; uav.Buffer.NumElements = count;
        Check(device->CreateUnorderedAccessView(result.data.Get(), &uav, &result.write));
    }
    return result;
}
template<class T> static bool Equal(const T& a, const T& b) { return std::memcmp(&a, &b, sizeof(T)) == 0; }
template<class T> static std::vector<T> Read(ID3D11Device* device, ID3D11DeviceContext* context, const Buffer& buffer)
{
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = buffer.count * sizeof(T);
    desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Buffer> staging; Check(device->CreateBuffer(&desc, nullptr, &staging));
    context->CopyResource(staging.Get(), buffer.data.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{}; Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    std::vector<T> result(buffer.count); std::memcpy(result.data(), mapped.pData, result.size() * sizeof(T));
    context->Unmap(staging.Get(), 0); return result;
}
static ComPtr<ID3D11ComputeShader> Load(ID3D11Device* device, const char* path)
{
    std::ifstream stream(path, std::ios::binary); Require(stream.good(), "Bytecode missing");
    std::vector<char> bytes((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    ComPtr<ID3D11ComputeShader> shader;
    Check(device->CreateComputeShader(bytes.data(), bytes.size(), nullptr, &shader)); return shader;
}
struct Gpu
{
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> copy, build, remap; ComPtr<ID3D11Buffer> constants;
    Gpu(char** argv)
    {
        D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0, actual{};
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &requested, 1,
            D3D11_SDK_VERSION, &device, &actual, &context));
        Require(actual >= requested, "SM5 unavailable");
        copy = Load(device.Get(), argv[1]); build = Load(device.Get(), argv[2]); remap = Load(device.Get(), argv[3]);
        D3D11_BUFFER_DESC desc{}; desc.ByteWidth = sizeof(Params); desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        Check(device->CreateBuffer(&desc, nullptr, &constants));
    }
    void Dispatch(ID3D11ComputeShader* kernel, const Params& p, U threads,
                  std::array<ID3D11ShaderResourceView*, 6> reads, std::array<ID3D11UnorderedAccessView*, 3> writes)
    {
        context->UpdateSubresource(constants.Get(), 0, nullptr, &p, 0, 0);
        ID3D11Buffer* cb = constants.Get(); context->CSSetConstantBuffers(0, 1, &cb);
        context->CSSetShader(kernel, nullptr, 0); context->CSSetShaderResources(0, 6, reads.data());
        context->CSSetUnorderedAccessViews(0, 3, writes.data(), nullptr);
        context->Dispatch((threads + 63) / 64, 1, 1);
        reads.fill(nullptr); writes.fill(nullptr);
        context->CSSetShaderResources(0, 6, reads.data()); context->CSSetUnorderedAccessViews(0, 3, writes.data(), nullptr);
    }
    template<class T> Buffer BufferOf(const std::vector<T>& values, bool writable = false)
    { return MakeBuffer(device.Get(), U(values.size()), sizeof(T), values.data(), writable); }
    template<class T> std::vector<T> Readback(const Buffer& buffer) { return Read<T>(device.Get(), context.Get(), buffer); }
    template<class T> void Upload(const Buffer& buffer, const std::vector<T>& values)
    { Require(values.size() == buffer.count, "Upload size mismatch"); context->UpdateSubresource(buffer.data.Get(), 0, nullptr, values.data(), 0, 0); }
};
struct Shape { U source, stride, mask, base; };

int main(int argc, char** argv)
{
    try
    {
        Require(argc == 4, "Usage: GlassShapeRemapGpu copy.dxbc build.dxbc remap.dxbc");
        Gpu gpu(argv);
        constexpr U nativeTriangles = 240, tailVertices = 21;
        std::vector<Shape> shapes{ {5,12,7,240}, {25,24,15,336}, {60,120,7,720}, {190,12,255,1680} };
        constexpr U ownedTriangles = 4752;
        std::vector<Record> source(nativeTriangles * 3), initial(ownedTriangles * 3 + tailVertices);
        Record sentinel{}; for (U w = 0; w < 31; ++w) sentinel.word[w] = 0xdead0000u + w;
        std::fill(initial.begin(), initial.end(), sentinel);
        for (U v = 0; v < source.size(); ++v) for (U w = 0; w < 31; ++w)
            source[v].word[w] = (v * 31 + w) ^ ((w % 3 == 0) ? 0x7fc00000u : 0x80000000u);
        std::vector<U> bits(nativeTriangles);
        for (const Shape& s : shapes)
        {
            U groups = 0; for (U mask = s.mask; mask; mask >>= 1) ++groups;
            for (U t = 0; t < s.stride; ++t) bits[s.source + t] = t % 7 == 6 ? 0 : 1u << (t % groups);
        }
        // Explicit zero-record padding and nonzero unmaskable facets are distinct.
        for (U t = 20; t < 24; ++t)
        {
            bits[25 + t] = 0;
            for (U v = 0; v < 3; ++v) source[(25 + t) * 3 + v] = Record{};
        }
        Buffer original = gpu.BufferOf(source), faceBits = gpu.BufferOf(bits), owned = gpu.BufferOf(initial, true);
        Params p{}; p.sourceVertices = U(source.size()); p.ownedVertices = U(initial.size());
        auto copyRange = [&](Params cp, U start, U count)
        {
            cp.workStart = start; cp.workCount = count;
            gpu.Dispatch(gpu.copy.Get(), cp, count, {original.read.Get()}, {owned.write.Get()});
        };
        copyRange(p, 0, 97); copyRange(p, 97, p.sourceVertices - 97);
        auto copied = gpu.Readback<Record>(owned);
        for (U v = 0; v < copied.size(); ++v)
            Require(Equal(copied[v], v < source.size() ? source[v] : sentinel), "Native raw byte preservation or copy output tail failed");
        std::cout << "PASS: native 124-byte pool copy, NaN payloads, chunk offsets and untouched appended tail\n";
        auto buildRange = [&](const Shape& s, Params bp, U start, U count)
        {
            bp.sourceStart = s.source; bp.stride = s.stride; bp.variantBase = s.base; bp.validMask = s.mask;
            bp.workStart = start; bp.workCount = count;
            gpu.Dispatch(gpu.build.Get(), bp, count, {original.read.Get(), faceBits.read.Get()}, {owned.write.Get()});
        };
        for (const Shape& s : shapes)
        {
            U count = (s.mask + 1) * s.stride * 3;
            buildRange(s, p, 0, 137); buildRange(s, p, 137, count - 137);
        }
        auto generated = gpu.Readback<Record>(owned);
        for (U v = 0; v < source.size(); ++v) Require(Equal(generated[v], source[v]), "Variant generation modified native prefix");
        for (const Shape& s : shapes) for (U mask = 0; mask <= s.mask; ++mask) for (U v = 0; v < s.stride * 3; ++v)
        {
            U t = v / 3, expected = s.source * 3 + ((bits[s.source + t] & mask) ? t * 3 : v);
            Require(Equal(generated[s.base * 3 + mask * s.stride * 3 + v], source[expected]), "Complete-record variant mismatch");
        }
        for (U v = ownedTriangles * 3; v < generated.size(); ++v) Require(Equal(generated[v], sentinel), "Variant output tail changed");
        std::cout << "PASS: all masks for 1/2/10-cluster shapes and 8-group boundary; padding and unmaskable records preserved\n";

        std::vector<U> savedBits = bits;
        bits[5] = 3; bits[6] = 256; // Invalid multi-bit/out-of-group entries fail open per triangle.
        gpu.Upload(faceBits,bits); buildRange(shapes[0],p,0,8*36);
        auto invalidBitsOutput = gpu.Readback<Record>(owned);
        for(U mask=0;mask<8;++mask)for(U v=0;v<36;++v)
        {
            U bit=bits[5+v/3];bool oneBit=bit!=0 && (bit&(bit-1))==0 && (bit&~7u)==0;
            U expectedVertex=15+((oneBit&&(mask&bit))?(v/3)*3:v);
            Require(Equal(invalidBitsOutput[240*3+mask*36+v],source[expectedVertex]),"Malformed face-bit entry collapsed a triangle");
        }
        bits=savedBits;gpu.Upload(faceBits,bits);buildRange(shapes[0],p,0,8*36);
        std::cout << "PASS: multi-bit and unknown triangle group entries retain complete triangles\n";

        // Bad descriptors/counts must not write even one record; every operation
        // is checked against the complete already-generated output.
        std::vector<Shape> invalidShapes{ {0,12,7,240}, {5,0,7,240}, {5,13,7,240}, {235,12,7,240},
            {5,12,0,240}, {5,12,5,240}, {5,12,511,240}, {5,12,7,239}, {5,12,7,0xffffffffu},
            {5,0xfffffffcu,7,240}, {5,12,255,ownedTriangles-12}, {0xfffffffdu,12,7,240} };
        for (const Shape& s : invalidShapes) buildRange(s, p, 0, 193);
        Params bad = p; --bad.sourceVertices; buildRange(shapes[0], bad, 0, 193);
        bad = p; --bad.ownedVertices; buildRange(shapes[0], bad, 0, 193);
        buildRange(shapes[0], p, 0xfffffff0u, 193);
        copyRange(p, 0xfffffff0u, 193); bad = p; --bad.sourceVertices; copyRange(bad, 0, 193);
        Buffer shortBits=gpu.BufferOf(std::vector<U>(16,1));std::swap(faceBits,shortBits);
        buildRange(shapes[0],p,0,193);std::swap(faceBits,shortBits);
        bad=p;bad.sourceStart=5;bad.stride=12;bad.variantBase=240;bad.validMask=7;bad.workCount=193;
        Buffer shortShape=gpu.BufferOf(std::vector<Record>(3,sentinel),true);
        gpu.Dispatch(gpu.build.Get(),bad,193,{original.read.Get(),faceBits.read.Get()},{shortShape.write.Get()});
        auto shortShapeOutput=gpu.Readback<Record>(shortShape);
        Require(Equal(shortShapeOutput[0],sentinel)&&Equal(shortShapeOutput[1],sentinel)&&Equal(shortShapeOutput[2],sentinel),"Actual undersized owned shape buffer was written");
        auto afterBad = gpu.Readback<Record>(owned);
        Require(std::memcmp(afterBad.data(), generated.data(), generated.size() * sizeof(Record)) == 0, "Bad descriptor/count/overflow wrote shape records");
        std::cout << "PASS: invalid shape descriptors, dimensions and overflowing work offsets write nothing\n";

        std::vector<Descriptor> lookup(nativeTriangles);
        std::vector<Pair> masks;
        std::vector<Pair> input(3, Pair{0xbaad,0xbeef}), expected(3, Pair{0xbaad,0xbeef});
        for (const Shape& s : shapes)
        {
            for (U c = 0; c < s.stride; c += 12) lookup[s.source + c] = {s.base + c, s.stride, s.mask, s.source};
            for (U mask = 0; mask <= s.mask; ++mask)
            {
                U id = U(masks.size()); masks.push_back({mask,s.source});
                for (U c = 0; c < s.stride; c += 12)
                {
                    input.push_back({s.source+c,id}); expected.push_back({mask ? s.base+mask*s.stride+c : s.source+c,id});
                }
            }
        }
        auto addPassthrough = [&](U triangle, Pair mask)
        {
            U id = U(masks.size()); masks.push_back(mask); input.push_back({triangle,id}); expected.push_back(input.back());
        };
        addPassthrough(5, {8,5}); addPassthrough(5, {0x80000001u,5}); // reject entire invalid mask
        addPassthrough(5, {1,25}); // recycled build ID has wrong expected shape
        addPassthrough(0, {7,5}); addPassthrough(6, {7,5}); addPassthrough(239, {7,5});
        lookup[6]={241,12,7,5}; // Nonzero descriptor at a misaligned source triangle.
        addPassthrough(17, {7,5});lookup[17]={252,12,7,5}; // Outside described shape's cluster range.
        addPassthrough(240, {7,5}); addPassthrough(0xffffffffu, {7,5});
        input.push_back({5,0xffffffffu}); expected.push_back(input.back());
        const U rangeCount = U(input.size())-3;
        input.insert(input.end(), 4, Pair{0xbad,0xbad}); expected.insert(expected.end(), 4, Pair{0xbad,0xbad});
        std::vector<U> clip(input.size()); for (U i = 0; i < clip.size(); ++i) clip[i] = 0x7f800000u+i;
        Buffer lookupBuffer = gpu.BufferOf(lookup), maskBuffer = gpu.BufferOf(masks), nativeMapping = gpu.BufferOf(input), nativeClip = gpu.BufferOf(clip);
        constexpr U capacity = 640;
        std::vector<Pair> emptyMapping(capacity, Pair{0xdead,0xcafe}); std::vector<U> emptyClip(capacity,0xdeadbeefu);
        Buffer nearMapping = gpu.BufferOf(emptyMapping,true), nearClip = gpu.BufferOf(emptyClip,true);
        Buffer farMapping = gpu.BufferOf(emptyMapping,true), farClip = gpu.BufferOf(emptyClip,true);
        p.maskCount = U(masks.size()); p.lookupCount = U(lookup.size());
        auto remapRange = [&](Buffer& output, Buffer& outputClip, Params rp, U start, U destination, U count)
        {
            rp.inputStart = start; rp.outputStart = destination; rp.mappingCount = count;
            gpu.Dispatch(gpu.remap.Get(), rp, count, {original.read.Get(),nullptr,lookupBuffer.read.Get(),maskBuffer.read.Get(),nativeMapping.read.Get(),nativeClip.read.Get()},
                {owned.write.Get(),output.write.Get(),outputClip.write.Get()});
        };
        auto verify = [&](Buffer& output, Buffer& outputClip, U start, U destination, U count, const std::vector<Pair>& before, const std::vector<U>& beforeClip)
        {
            auto values = gpu.Readback<Pair>(output); auto clips = gpu.Readback<U>(outputClip);
            for (U i = 0; i < values.size(); ++i)
            {
                bool changed = i >= destination && i-destination < count;
                Require(Equal(values[i],changed ? expected[start+i-destination] : before[i]), "Compact remap/cluster offset/guard mismatch");
                Require(clips[i] == (changed ? clip[start+i-destination] : beforeClip[i]), "Clip bits or output range changed");
            }
        };
        remapRange(nearMapping,nearClip,p,3,0,rangeCount);
        verify(nearMapping,nearClip,3,0,rangeCount,emptyMapping,emptyClip);
        Require(gpu.Readback<U>(nativeClip)==clip,"Native clip changed");
        auto preservedInput = gpu.Readback<Pair>(nativeMapping);
        Require(std::memcmp(preservedInput.data(),input.data(),input.size()*sizeof(Pair))==0,"Native mapping changed");
        std::cout << "PASS: all mask/cluster remaps, native zero/unknown passthrough, ID reuse, invalid masks, exact clip bits and native inputs\n";

        remapRange(farMapping,farClip,p,19,0,31); verify(farMapping,farClip,19,0,31,emptyMapping,emptyClip);
        auto nearBefore = gpu.Readback<Pair>(nearMapping); auto nearClipBefore = gpu.Readback<U>(nearClip);
        remapRange(farMapping,farClip,p,29,4,13);
        remapRange(nearMapping,nearClip,p,41,7,23); verify(nearMapping,nearClip,41,7,23,nearBefore,nearClipBefore);
        auto farBefore = gpu.Readback<Pair>(farMapping); auto farClipBefore = gpu.Readback<U>(farClip);
        remapRange(nearMapping,nearClip,p,63,0,17);
        Require(gpu.Readback<U>(farClip)==farClipBefore,"Near overwrote far storage");
        auto farUnchanged = gpu.Readback<Pair>(farMapping);
        Require(std::memcmp(farBefore.data(),farUnchanged.data(),farBefore.size()*sizeof(Pair))==0,"Near/far mapping isolation failed");
        std::vector<Pair> grownInitial(capacity*2,Pair{0xdead,0xcafe}); std::vector<U> grownClipInitial(capacity*2,0xdeadbeefu);
        Buffer grownMapping = gpu.BufferOf(grownInitial,true), grownClip = gpu.BufferOf(grownClipInitial,true);
        nearBefore = gpu.Readback<Pair>(nearMapping); nearClipBefore = gpu.Readback<U>(nearClip);
        remapRange(grownMapping,grownClip,p,3,0,rangeCount); verify(grownMapping,grownClip,3,0,rangeCount,grownInitial,grownClipInitial);
        auto oldMapping = gpu.Readback<Pair>(nearMapping);
        Require(std::memcmp(nearBefore.data(),oldMapping.data(),nearBefore.size()*sizeof(Pair))==0 && gpu.Readback<U>(nearClip)==nearClipBefore,"Growth changed retained previous allocation");
        std::cout << "PASS: near/far independent storage in both orders, batch output offsets and retained growth allocation\n";

        // Range guards must leave destination sentinels untouched, or limit
        // valid work by BOTH clip and mapping dimensions rather than uniforms.
        gpu.Upload(nearMapping,emptyMapping); gpu.Upload(nearClip,emptyClip);
        remapRange(nearMapping,nearClip,p,0xfffffff0u,0,129);
        remapRange(nearMapping,nearClip,p,3,0xfffffff0u,129);
        Require(gpu.Readback<U>(nearClip)==emptyClip,"Overflowing range wrote output");
        auto noWrites = gpu.Readback<Pair>(nearMapping);
        Require(std::memcmp(noWrites.data(),emptyMapping.data(),emptyMapping.size()*sizeof(Pair))==0,"Overflowing range wrote mapping");
        remapRange(nearMapping,nearClip,p,U(input.size())-2,capacity-2,129);
        verify(nearMapping,nearClip,U(input.size())-2,capacity-2,2,emptyMapping,emptyClip);
        // The lookup metadata itself can be corrupt; every case must retain
        // the native mapping and copied clip data rather than invent geometry.
        Descriptor saved = lookup[5];
        std::vector<Descriptor> corrupt{{240,12,5,5},{240,13,7,5},{240,0xfffffffcu,7,5},{0xffffffffu,12,7,5},
            {239,12,7,5},{ownedTriangles-12,12,7,5},{240,12,7,0},{240,12,7,6},{240,12,0,5},{240,12,511,5}};
        U nonzero = 0; for(U i=3;i<input.size();++i)if(input[i].x==5 && masks[input[i].y].x==1){nonzero=i;break;}
        Require(nonzero!=0,"Fixture nonzero mask missing");
        for (const Descriptor& d : corrupt)
        {
            lookup[5]=d;gpu.Upload(lookupBuffer,lookup); remapRange(nearMapping,nearClip,p,nonzero,0,1);
            Require(Equal(gpu.Readback<Pair>(nearMapping)[0],input[nonzero]),"Invalid lookup descriptor remapped native input");
        }
        lookup[5]=saved;gpu.Upload(lookupBuffer,lookup);
        for(U mode=0;mode<4;++mode)
        {
            bad=p;
            if(mode==0)bad.sourceVertices--;
            if(mode==1)bad.ownedVertices--;
            if(mode==2)bad.maskCount=0;
            if(mode==3)bad.lookupCount=5;
            remapRange(nearMapping,nearClip,bad,nonzero,0,1);
            Require(Equal(gpu.Readback<Pair>(nearMapping)[0],input[nonzero]),"Bad declared counts did not preserve native mapping");
        }
        // Actual dimensions, not just trusted uniform counts, constrain every
        // source/destination. Short clip buffers must constrain mapping writes too.
        gpu.Upload(nearMapping,emptyMapping);gpu.Upload(nearClip,emptyClip);
        Buffer shortInputClip=gpu.BufferOf(std::vector<U>(clip.begin(),clip.begin()+5));
        std::swap(nativeClip,shortInputClip);remapRange(nearMapping,nearClip,p,3,0,129);std::swap(nativeClip,shortInputClip);
        verify(nearMapping,nearClip,3,0,2,emptyMapping,emptyClip);
        Buffer shortOutputClip=gpu.BufferOf(std::vector<U>(3,0xdeadbeefu),true);
        gpu.Upload(nearMapping,emptyMapping);remapRange(nearMapping,shortOutputClip,p,3,0,129);
        auto boundedMapping=gpu.Readback<Pair>(nearMapping);auto boundedClip=gpu.Readback<U>(shortOutputClip);
        for(U i=0;i<capacity;++i)Require(Equal(boundedMapping[i],i<3?expected[3+i]:emptyMapping[i]),"Short actual output clip did not limit mapping writes");
        for(U i=0;i<3;++i)Require(boundedClip[i]==clip[3+i],"Short actual output clip mismatch");
        Buffer shortOutputMapping=gpu.BufferOf(std::vector<Pair>(3,Pair{0xdead,0xcafe}),true);
        gpu.Upload(nearClip,emptyClip);remapRange(shortOutputMapping,nearClip,p,3,0,129);
        auto boundedClip2=gpu.Readback<U>(nearClip);
        for(U i=0;i<capacity;++i)Require(boundedClip2[i]==(i<3?clip[3+i]:emptyClip[i]),"Short actual output mapping did not limit clip writes");
        Buffer shortMasks=gpu.BufferOf(std::vector<Pair>(1,Pair{1,5}));std::swap(maskBuffer,shortMasks);
        remapRange(nearMapping,nearClip,p,nonzero,0,1);std::swap(maskBuffer,shortMasks);
        Require(Equal(gpu.Readback<Pair>(nearMapping)[0],input[nonzero]),"Actual mask count ignored");
        Buffer shortLookup=gpu.BufferOf(std::vector<Descriptor>(5));std::swap(lookupBuffer,shortLookup);
        remapRange(nearMapping,nearClip,p,nonzero,0,1);std::swap(lookupBuffer,shortLookup);
        Require(Equal(gpu.Readback<Pair>(nearMapping)[0],input[nonzero]),"Actual lookup count ignored");
        std::swap(owned,shortShape);remapRange(nearMapping,nearClip,p,nonzero,0,1);std::swap(owned,shortShape);
        Require(Equal(gpu.Readback<Pair>(nearMapping)[0],input[nonzero]),"Actual owned shape count ignored");
        auto unchangedSource = gpu.Readback<Record>(original);
        Require(std::memcmp(unchangedSource.data(),source.data(),source.size()*sizeof(Record))==0,"Native shape input changed");
        Require(std::memcmp(gpu.Readback<Record>(owned).data(),generated.data(),generated.size()*sizeof(Record))==0,"Remap changed owned shape records");
        std::cout << "PASS: compact input/output bounds, uint overflow, corrupt descriptors and declared count guards\n";
        std::cout << "PASS: GlassShapeRemap standalone GPU prototype (synthetic data only)\n";
        return 0;
    }
    catch(const std::exception& error){std::cerr<<"FAIL: "<<error.what()<<'\n';return 1;}
}
