#ifndef GT_GPU_FIXTURE_RESOURCES_INCLUDED
#define GT_GPU_FIXTURE_RESOURCES_INCLUDED
// Original D3D11 WARP interoperability fixture. Native DXBC is an untracked
// local input; all buffers, textures and geometry below are synthetic.
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d11shader.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <vector>
#include <array>
#include <map>
#include <string>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <cstring>
#include <cmath>
#include <cstdint>
using Microsoft::WRL::ComPtr;
using U = uint32_t;
using F4 = std::array<float, 4>;
inline void Check(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D11 HRESULT " + std::to_string((unsigned)result)); }
inline void Require(bool condition, const char* reason) { if (!condition) throw std::runtime_error(reason); }
inline std::vector<char> Bytes(const char* path)
{
    std::ifstream input(path, std::ios::binary); Require(input.good(), "Shader bytecode missing");
    return std::vector<char>((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
}
struct Material
{
    int id, alpha, vertex, diffuse, border, tiling;
    float unit, uLow, uHigh, uWidth, vLow, vHigh, vWidth, geoHigh, geoLow;
    int borderMode, normal, specular, borderNormal, borderSpecular, projection, paint, blend, normalMode;
    float uvScale[2]; int buildOffset, pixel, resize; float path[3]; int overlay;
};
struct Build { U pixel, vertex, packed, counts; float position[3]; U color; float unit, health; int transform; };
struct Palette { float color[4], roughness, metallic; };
static_assert(sizeof(Material) == 132 && sizeof(Build) == 44 && sizeof(Palette) == 24, "Material GPU ABI");
struct Resource { ComPtr<ID3D11Resource> resource; ComPtr<ID3D11ShaderResourceView> view; };
struct Shader { std::vector<char> bytes; ComPtr<ID3D11PixelShader> pixel; ComPtr<ID3D11ShaderReflection> reflection; };

inline Resource Structured(ID3D11Device* device, U count, U stride, const void* values)
{
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = count * stride; desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE; desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; desc.StructureByteStride = stride;
    D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = values;
    ComPtr<ID3D11Buffer> buffer; Check(device->CreateBuffer(&desc, &data, &buffer));
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{}; srv.ViewDimension = D3D11_SRV_DIMENSION_BUFFER; srv.Buffer.NumElements = count;
    Resource result; Check(buffer.As(&result.resource)); Check(device->CreateShaderResourceView(buffer.Get(), &srv, &result.view)); return result;
}
inline Resource Texture(ID3D11Device* device, F4 value, bool array, bool cube = false)
{
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = desc.Height = desc.MipLevels = 1; desc.ArraySize = cube ? 6 : 1;
    desc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE; desc.MiscFlags = cube ? D3D11_RESOURCE_MISC_TEXTURECUBE : 0;
    D3D11_SUBRESOURCE_DATA data[6]{}; for (auto& entry : data) { entry.pSysMem = value.data(); entry.SysMemPitch = sizeof(F4); }
    ComPtr<ID3D11Texture2D> texture; Check(device->CreateTexture2D(&desc, data, &texture));
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{}; srv.Format = desc.Format;
    if (cube) { srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURECUBE; srv.TextureCube.MipLevels = 1; }
    else if (array) { srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY; srv.Texture2DArray.MipLevels = srv.Texture2DArray.ArraySize = 1; }
    else { srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; srv.Texture2D.MipLevels = 1; }
    Resource result; Check(texture.As(&result.resource)); Check(device->CreateShaderResourceView(texture.Get(), &srv, &result.view)); return result;
}
inline ComPtr<ID3D11Buffer> Constants(ID3D11Device* device, const void* values, U bytes)
{
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = (bytes + 15) & ~15u; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = values;
    ComPtr<ID3D11Buffer> result; Check(device->CreateBuffer(&desc, &data, &result)); return result;
}
inline Shader Pixel(ID3D11Device* device, const char* path, bool reflect)
{
    Shader shader; shader.bytes = Bytes(path); Check(device->CreatePixelShader(shader.bytes.data(), shader.bytes.size(), nullptr, &shader.pixel));
    if (reflect) Check(D3DReflect(shader.bytes.data(), shader.bytes.size(), IID_PPV_ARGS(&shader.reflection)));
    return shader;
}


#endif
