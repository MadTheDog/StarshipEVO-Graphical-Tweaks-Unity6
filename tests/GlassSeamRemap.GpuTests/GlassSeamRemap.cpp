// Standalone D3D11 WARP test. All input records are synthetic original data.
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
using Microsoft::WRL::ComPtr;
using U = uint32_t;

static void Check(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D11 HRESULT " + std::to_string((unsigned)result)); }
static void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
struct Vertex { float p[3], n[3], t[4], b[3], uv[2]; U material; float center[3], size[3], minimum[3], maximum[3], offset[3]; };
struct Build { U pixel, vertex, packed, counts; float position[3]; U color; float unit, health; int transform; };
struct Mapping { U triangle, build; };
static_assert(sizeof(Vertex) == 124 && sizeof(Build) == 44 && sizeof(Mapping) == 8, "GPU ABI stride");
struct Buffer { ComPtr<ID3D11Buffer> data; ComPtr<ID3D11ShaderResourceView> read; ComPtr<ID3D11UnorderedAccessView> write; };

static Buffer MakeBuffer(ID3D11Device* device, U count, U stride, const void* initial, bool writable)
{
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = count * stride; desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (writable ? D3D11_BIND_UNORDERED_ACCESS : 0);
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; desc.StructureByteStride = stride;
    D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = initial;
    Buffer result; Check(device->CreateBuffer(&desc, initial ? &data : nullptr, &result.data));
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

template<class T> static std::vector<T> Read(ID3D11Device* device, ID3D11DeviceContext* context, Buffer& source, U count)
{
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = count * sizeof(T);
    desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Buffer> staging; Check(device->CreateBuffer(&desc, nullptr, &staging));
    context->CopyResource(staging.Get(), source.data.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{}; Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    std::vector<T> values(count); std::memcpy(values.data(), mapped.pData, count * sizeof(T));
    context->Unmap(staging.Get(), 0); return values;
}

static ComPtr<ID3D11ComputeShader> Load(ID3D11Device* device, const char* path)
{
    std::ifstream input(path, std::ios::binary);
    Require(input.good(), "Compute bytecode missing");
    std::vector<char> bytes((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    ComPtr<ID3D11ComputeShader> shader;
    Check(device->CreateComputeShader(bytes.data(), bytes.size(), nullptr, &shader)); return shader;
}

static void ClearBindings(ID3D11DeviceContext* context)
{
    ID3D11ShaderResourceView* reads[5]{}; ID3D11UnorderedAccessView* writes[2]{};
    context->CSSetShaderResources(0, 5, reads); context->CSSetUnorderedAccessViews(0, 2, writes, nullptr);
}

int main(int argc, char** argv)
{
    try
    {
        Require(argc == 3, "Usage: GlassSeamRemapGpu copy.dxbc remap.dxbc");
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0, actual{};
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &requested, 1,
            D3D11_SDK_VERSION, &device, &actual, &context));
        Require(actual >= D3D_FEATURE_LEVEL_11_0, "Shader model 5 device unavailable");
        auto copy = Load(device.Get(), argv[1]); auto remap = Load(device.Get(), argv[2]);
        D3D11_BUFFER_DESC cbd{}; cbd.ByteWidth = 32; cbd.Usage = D3D11_USAGE_DEFAULT; cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ComPtr<ID3D11Buffer> constants; Check(device->CreateBuffer(&cbd, nullptr, &constants));
        ID3D11Buffer* constantPointer = constants.Get(); context->CSSetConstantBuffers(0, 1, &constantPointer);

        constexpr U sourceCount = 147, cubeTriangle = 5, variantCount = 64 * 36;
        std::vector<Vertex> source(sourceCount);
        for (U i = 0; i < sourceCount; ++i)
        {
            float* words = reinterpret_cast<float*>(&source[i]);
            for (U word = 0; word < 31; ++word) words[word] = float(i * 32 + word + 1) / 4096;
            source[i].material = 421;
        }
        for (U i = 0; i < 36; ++i)
        {
            Vertex& record = source[3 * cubeTriangle + i]; U face = i / 6;
            record.n[0] = record.n[1] = record.n[2] = 0;
            record.n[face / 2] = face % 2 ? -1.f : 1.f;
        }
        Buffer original = MakeBuffer(device.Get(), sourceCount, sizeof(Vertex), source.data(), false);
        Buffer cloned = MakeBuffer(device.Get(), sourceCount + variantCount, sizeof(Vertex), nullptr, true);
        U params[8] = { sourceCount, cubeTriangle, 0, 0, 0, 0, 0, 0 };
        context->UpdateSubresource(constants.Get(), 0, nullptr, params, 0, 0);
        context->CSSetShader(copy.Get(), nullptr, 0);
        ID3D11ShaderResourceView* copyRead = original.read.Get(); ID3D11UnorderedAccessView* copyWrite = cloned.write.Get();
        context->CSSetShaderResources(0, 1, &copyRead); context->CSSetUnorderedAccessViews(0, 1, &copyWrite, nullptr);
        context->Dispatch((sourceCount + variantCount + 63) / 64, 1, 1); ClearBindings(context.Get());
        auto output = Read<Vertex>(device.Get(), context.Get(), cloned, sourceCount + variantCount);
        Require(std::memcmp(output.data(), source.data(), sourceCount * sizeof(Vertex)) == 0, "Original pool bytes changed");
        for (U mask = 0; mask < 64; ++mask) for (U v = 0; v < 36; ++v)
        {
            U expected = (mask & (1u << (v / 6))) ? (v / 3) * 3 : v;
            Require(std::memcmp(&output[sourceCount + mask * 36 + v], &source[cubeTriangle * 3 + expected], sizeof(Vertex)) == 0,
                "Covered/visible triangle record mismatch");
        }
        std::cout << "PASS: native pool byte preservation and all 2,304 complete variant records\n";

        // Generate all 24 proper cube orientations independently using signed
        // permutation matrices, then test every mask in every orientation.
        std::vector<std::array<int, 6>> rotations;
        for (int x = 0; x < 3; ++x) for (int y = 0; y < 3; ++y) for (int z = 0; z < 3; ++z)
        {
            if (x == y || y == z || x == z) continue;
            int parity = ((x - y) * (y - z) * (x - z)) > 0 ? -1 : 1;
            for (int sx : {-1, 1}) for (int sy : {-1, 1}) for (int sz : {-1, 1})
            {
                if (parity * sx * sy * sz != 1) continue;
                std::array<int, 6> faces{}; int axes[] = { x, y, z }, signs[] = { sx, sy, sz };
                for (int axis = 0; axis < 3; ++axis)
                {
                    faces[axis * 2] = axes[axis] * 2 + (signs[axis] > 0 ? 0 : 1);
                    faces[axis * 2 + 1] = faces[axis * 2] ^ 1;
                }
                rotations.push_back(faces);
            }
        }
        Require(rotations.size() == 24, "Cube orientation enumeration failed");
        std::vector<U> lookup(24 * 64), masks(1541, 63);
        std::vector<Build> builds(1540);
        for (U rotation = 0; rotation < 24; ++rotation) for (U mask = 0; mask < 64; ++mask)
        {
            U local = 0;
            for (U face = 0; face < 6; ++face) if (mask & (1u << rotations[rotation][face])) local |= 1u << face;
            lookup[rotation * 64 + mask] = local;
            masks[rotation * 64 + mask] = mask; builds[rotation * 64 + mask].packed = (0xabu << 5) | rotation;
        }
        builds[1536].packed = 24; builds[1537].packed = 31;
        constexpr U mappingStart = 3, mappingCount = 1542, totalMappings = mappingStart + mappingCount + 3;
        std::vector<Mapping> input(totalMappings, Mapping{ 0xbaad, 0xbeef });
        for (U i = 0; i < 1536; ++i) input[mappingStart + i] = Mapping{ cubeTriangle, i };
        input[mappingStart + 1536] = Mapping{ cubeTriangle + 12, 63 }; // unrelated source geometry
        input[mappingStart + 1537] = Mapping{ 0, 63 }; // inactive mapping
        input[mappingStart + 1538] = Mapping{ cubeTriangle, 1536 }; // unsupported rotation24
        input[mappingStart + 1539] = Mapping{ cubeTriangle, 1537 }; // unsupported rotation31
        input[mappingStart + 1540] = Mapping{ cubeTriangle, 1540 }; // mask exists, build is out of bounds
        input[mappingStart + 1541] = Mapping{ cubeTriangle, 1541 }; // mask is out of bounds
        constexpr U compactCapacity = mappingCount + 3;
        std::vector<Mapping> initial(compactCapacity, Mapping{ 0xdead, 0xcafe });
        std::vector<U> originalClip(totalMappings), initialClip(compactCapacity, 0xdeadbeef);
        for(U i=0;i<totalMappings;i++)originalClip[i]=0x7f800000u+i; // Preserve all bits, including NaN payloads.
        Buffer sourceMapping = MakeBuffer(device.Get(), totalMappings, sizeof(Mapping), input.data(), false);
        Buffer targetMapping = MakeBuffer(device.Get(), compactCapacity, sizeof(Mapping), initial.data(), true);
        Buffer sourceClip = MakeBuffer(device.Get(), totalMappings, sizeof(U), originalClip.data(), false);
        Buffer targetClip = MakeBuffer(device.Get(), compactCapacity, sizeof(U), initialClip.data(), true);
        Buffer buildBuffer = MakeBuffer(device.Get(), U(builds.size()), sizeof(Build), builds.data(), false);
        Buffer maskBuffer = MakeBuffer(device.Get(), U(masks.size()), sizeof(U), masks.data(), false);
        Buffer lookupBuffer = MakeBuffer(device.Get(), U(lookup.size()), sizeof(U), lookup.data(), false);
        params[2] = mappingStart; params[3] = mappingCount; params[4] = U(builds.size()); params[5] = U(masks.size());
        context->UpdateSubresource(constants.Get(), 0, nullptr, params, 0, 0);
        context->CSSetShader(remap.Get(), nullptr, 0);
        ID3D11ShaderResourceView* reads[] = { sourceMapping.read.Get(), buildBuffer.read.Get(), maskBuffer.read.Get(), lookupBuffer.read.Get(), sourceClip.read.Get() };
        ID3D11UnorderedAccessView* writes[] = { targetMapping.write.Get(), targetClip.write.Get() };
        context->CSSetShaderResources(0, 5, reads); context->CSSetUnorderedAccessViews(0, 2, writes, nullptr);
        context->Dispatch((mappingCount + 63) / 64, 1, 1); ClearBindings(context.Get());
        auto mapped = Read<Mapping>(device.Get(), context.Get(), targetMapping, compactCapacity);
        auto clips = Read<U>(device.Get(), context.Get(), targetClip, compactCapacity);
        auto expectedMapping = [&](U index)
        {
            Mapping result=input[index];
            if(result.triangle==cubeTriangle && result.build<masks.size() && result.build<builds.size())
            {
                U mask=masks[result.build]&63, rotation=builds[result.build].packed&31;
                if(mask!=0 && rotation<24)
                {
                    U local=lookup[rotation*64+mask];
                    if(local!=0)result.triangle=sourceCount/3+local*12;
                }
            }
            return result;
        };
        for (U i = 0; i < compactCapacity; ++i)
        {
            Mapping expected = initial[i];
            if (i < mappingCount)
            {
                expected = expectedMapping(mappingStart+i);
            }
            Require(std::memcmp(&mapped[i], &expected, sizeof(Mapping)) == 0, "Compact mapping zero-base/rotation/range mismatch");
            Require(clips[i]==(i<mappingCount?originalClip[mappingStart+i]:initialClip[i]),"Compact clip-Z bits/range mismatch");
        }
        auto preservedMapping=Read<Mapping>(device.Get(),context.Get(),sourceMapping,totalMappings);
        auto preservedClip=Read<U>(device.Get(),context.Get(),sourceClip,totalMappings);
        Require(std::memcmp(preservedMapping.data(),input.data(),input.size()*sizeof(Mapping))==0,"Native input mapping changed");
        Require(preservedClip==originalClip,"Native clip-Z changed");
        std::cout << "PASS: all 1,536 rotated masks, zero-base compact mapping, clip-Z bit preservation, native inputs and range guards\n";

        // Near/far command buffers can execute in either order. Their compact
        // output resources must be independent even though both start at zero.
        constexpr U cameraCapacity=32;
        std::vector<Mapping> cameraInitial(cameraCapacity,Mapping{0xabcd,0xdcba});
        std::vector<U> clipInitial(cameraCapacity,0xfedcba98);
        Buffer nearMapping=MakeBuffer(device.Get(),cameraCapacity,sizeof(Mapping),cameraInitial.data(),true);
        Buffer farMapping=MakeBuffer(device.Get(),cameraCapacity,sizeof(Mapping),cameraInitial.data(),true);
        Buffer nearClip=MakeBuffer(device.Get(),cameraCapacity,sizeof(U),clipInitial.data(),true);
        Buffer farClip=MakeBuffer(device.Get(),cameraCapacity,sizeof(U),clipInitial.data(),true);
        auto dispatch=[&](Buffer& destination,Buffer& destinationClip,U sourceStart,U destinationStart,U count)
        {
            params[2]=sourceStart;params[3]=count;params[6]=destinationStart;
            context->UpdateSubresource(constants.Get(),0,nullptr,params,0,0);
            context->CSSetShader(remap.Get(),nullptr,0);
            ID3D11UnorderedAccessView* outputViews[]={destination.write.Get(),destinationClip.write.Get()};
            context->CSSetShaderResources(0,5,reads);context->CSSetUnorderedAccessViews(0,2,outputViews,nullptr);
            context->Dispatch((count+63)/64,1,1);ClearBindings(context.Get());
        };
        auto verifyCamera=[&](Buffer& destination,Buffer& destinationClip,U start,U count)
        {
            auto values=Read<Mapping>(device.Get(),context.Get(),destination,cameraCapacity);
            auto clipValues=Read<U>(device.Get(),context.Get(),destinationClip,cameraCapacity);
            for(U i=0;i<cameraCapacity;i++)
            {
                Mapping expected=i<count?expectedMapping(start+i):cameraInitial[i];
                Require(std::memcmp(&values[i],&expected,sizeof(Mapping))==0,"Near/far compact mapping isolation failed");
                Require(clipValues[i]==(i<count?originalClip[start+i]:clipInitial[i]),"Near/far clip-Z isolation failed");
            }
        };
        dispatch(nearMapping,nearClip,3,0,17);dispatch(farMapping,farClip,100,0,9);
        verifyCamera(nearMapping,nearClip,3,17);verifyCamera(farMapping,farClip,100,9);
        dispatch(farMapping,farClip,200,0,9);dispatch(nearMapping,nearClip,500,0,21);
        verifyCamera(farMapping,farClip,200,9);verifyCamera(nearMapping,nearClip,500,21);
        std::cout<<"PASS: independent near/far mappings and clip-Z in both camera submission orders\n";

        // A later compute batch uses an explicit compact output offset; native
        // source and compact destination offsets must never be conflated.
        auto beforeBatch=Read<Mapping>(device.Get(),context.Get(),nearMapping,cameraCapacity);
        auto beforeBatchClip=Read<U>(device.Get(),context.Get(),nearClip,cameraCapacity);
        dispatch(nearMapping,nearClip,20,5,7);
        auto afterBatch=Read<Mapping>(device.Get(),context.Get(),nearMapping,cameraCapacity);
        auto afterBatchClip=Read<U>(device.Get(),context.Get(),nearClip,cameraCapacity);
        for(U i=0;i<cameraCapacity;i++)
        {
            Mapping expected=i>=5&&i<12?expectedMapping(20+i-5):beforeBatch[i];
            Require(std::memcmp(&afterBatch[i],&expected,sizeof(Mapping))==0,"Compact batch output offset/range guard failed");
            Require(afterBatchClip[i]==(i>=5&&i<12?originalClip[20+i-5]:beforeBatchClip[i]),"Compact batch clip-Z offset/range guard failed");
        }
        dispatch(nearMapping,nearClip,0xfffffffeu,0,4);
        dispatch(nearMapping,nearClip,20,0xfffffffeu,4);
        auto afterOverflow=Read<Mapping>(device.Get(),context.Get(),nearMapping,cameraCapacity);
        auto afterOverflowClip=Read<U>(device.Get(),context.Get(),nearClip,cameraCapacity);
        Require(std::memcmp(afterOverflow.data(),afterBatch.data(),cameraCapacity*sizeof(Mapping))==0,"Overflow guard changed compact mapping");
        Require(afterOverflowClip==afterBatchClip,"Overflow guard changed compact clip-Z");
        std::cout<<"PASS: distinct source/output batch offsets, untouched capacity tail and overflow guards\n";

        // Growing a camera range uses a new resource while recorded old draws
        // retain their old resource. Writing the larger buffer cannot mutate it.
        constexpr U grownCapacity=64;
        Buffer grownMapping=MakeBuffer(device.Get(),grownCapacity,sizeof(Mapping),nullptr,true);
        Buffer grownClip=MakeBuffer(device.Get(),grownCapacity,sizeof(U),nullptr,true);
        dispatch(grownMapping,grownClip,300,0,40);
        auto grown=Read<Mapping>(device.Get(),context.Get(),grownMapping,grownCapacity);
        auto grownZ=Read<U>(device.Get(),context.Get(),grownClip,grownCapacity);
        for(U i=0;i<40;i++)
        {
            Mapping expected=expectedMapping(300+i);
            Require(std::memcmp(&grown[i],&expected,sizeof(Mapping))==0&&grownZ[i]==originalClip[300+i],"Grown compact range failed");
        }
        auto retainedNear=Read<Mapping>(device.Get(),context.Get(),nearMapping,cameraCapacity);
        Require(std::memcmp(retainedNear.data(),afterBatch.data(),cameraCapacity*sizeof(Mapping))==0,"Growth overwrote a retained camera buffer");
        std::cout<<"PASS: growth retains previous camera output and compact allocation is independent of native capacity\n";
        return 0;
    }
    catch (const std::exception& exception) { std::cerr << "FAIL: " << exception.what() << '\n'; return 1; }
}
