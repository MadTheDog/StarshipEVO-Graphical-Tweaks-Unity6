using System;
using System.IO;
using GraphicalTweaks;
using Mono.Cecil;
using Mono.Cecil.Cil;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
using var first = ModuleDefinition.CreateModule("Game", ModuleKind.Dll);
var type = new TypeDefinition("Game", "RenderPass", TypeAttributes.Public, first.TypeSystem.Object);
first.Types.Add(type);
var method = new MethodDefinition("Draw", MethodAttributes.Public | MethodAttributes.Static, first.TypeSystem.Int32);
type.Methods.Add(method);
method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 1));
method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
string original = NativeContractFingerprint.Method(method);
method.Body.Instructions[0].Operand = 2;
Check(original != NativeContractFingerprint.Method(method), "changed draw behavior changes its contract");
method.Body.Instructions[0].Operand = 1;
Check(original == NativeContractFingerprint.Method(method), "restoring draw behavior restores its contract");
type.Methods.Insert(0, new MethodDefinition("Unrelated", MethodAttributes.Public, first.TypeSystem.Void));
first.Mvid = Guid.NewGuid();
using var image = new MemoryStream();
first.Write(image);
image.Position = 0;
using var rebuilt = ModuleDefinition.ReadModule(image);
var roundTrip = rebuilt.GetType("Game.RenderPass").Methods[1];
Check(original == NativeContractFingerprint.Method(roundTrip), "unrelated method and new MVID preserve the draw contract");

var layout = new TypeDefinition("Game", "BufferData", TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed,
    first.ImportReference(typeof(ValueType)));
first.Types.Add(layout);
layout.Fields.Add(new FieldDefinition("Index", FieldAttributes.Public, first.TypeSystem.Int32));
layout.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public, first.TypeSystem.Single));
string oldLayout = NativeContractFingerprint.Layout(layout);
layout.Fields[1].FieldType = first.TypeSystem.Int32;
Check(oldLayout != NativeContractFingerprint.Layout(layout), "same-size field-type changes reject a buffer layout");
layout.Fields[1].FieldType = first.TypeSystem.Single;
var field = layout.Fields[0];
layout.Fields.RemoveAt(0);
layout.Fields.Add(field);
Check(oldLayout != NativeContractFingerprint.Layout(layout), "field order changes reject a buffer layout");
var pass = new FieldDefinition("Transparent", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
    first.TypeSystem.Int32) { Constant = 5 };
layout.Fields.Add(pass);
string enumLayout = NativeContractFingerprint.Layout(layout);
string enumField = NativeContractFingerprint.Field(pass);
pass.Constant = 6;
Check(enumLayout != NativeContractFingerprint.Layout(layout) && enumField != NativeContractFingerprint.Field(pass), "enum value changes reject both layout and field contracts");
var entry = new NativeContractEntry { kind = "method", type = type.FullName, member = method.FullName, hash = original };
NativeContractFingerprint.Verify(rebuilt, new[] { entry });
Check(true, "matching target contract accepts a rebuilt assembly");
entry.hash = new string('0', 64);
bool rejected = false;
try { NativeContractFingerprint.Verify(rebuilt, new[] { entry }); }
catch (NotSupportedException) { rejected = true; }
Check(rejected, "changed target digest rejects its feature");
entry.hash = original;
entry.member = "missing";
rejected = false;
try { NativeContractFingerprint.Verify(rebuilt, new[] { entry }); }
catch (NotSupportedException) { rejected = true; }
Check(rejected, "missing target method rejects its feature");
var matched = new NativeContractEntry { kind = "method", type = type.FullName, member = method.FullName, hash = original };
var changed = new NativeContractEntry { kind = "method", type = type.FullName, member = method.FullName, hash = new string('0', 64) };
var admitted = NativeContractFingerprint.VerifyFeatures(rebuilt, new[]
{
    new NativeFeatureContract { name = "changed", entries = new[] { changed } },
    new NativeFeatureContract { name = "matching", entries = new[] { matched } }
}, _ => { });
Check(admitted["changed"] != null && admitted["matching"] == null, "a changed native feature does not block a matching feature");
admitted = NativeContractFingerprint.VerifyFeatures(rebuilt, new[]
{
    new NativeFeatureContract { name = "glass", entries = new[] { matched } },
    new NativeFeatureContract { name = "renderer", entries = new[] { matched } }
}, feature => { if (feature.name == "glass") throw new NotSupportedException("changed shader"); });
Check(admitted["glass"] != null && admitted["renderer"] == null, "shader rejection remains isolated from renderer admission");
byte[] ShaderFixture(byte lighting, byte unrelated, int relocation = 0)
{
    using var programs = new MemoryStream();
    using (var writer = new BinaryWriter(programs, System.Text.Encoding.UTF8, true))
    {
        int count = 5 + relocation;
        int start = 4 + count * 12;
        writer.Write(count);
        for (int index = 0; index < count; index++) { writer.Write(start + index * 60); writer.Write(60); writer.Write(0); }
        for (int index = 0; index < count; index++)
        {
            writer.Write(new byte[32]);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("DXBC"));
            writer.Write(new byte[20]);
            writer.Write(28);
            programs.GetBuffer()[start + index * 60 + 40] = lighting;
        }
    }
    byte[] raw = programs.ToArray();
    using var packed = new MemoryStream();
    packed.WriteByte(0xf0);
    int extra = raw.Length - 15;
    while (extra >= 255) { packed.WriteByte(255); extra -= 255; }
    packed.WriteByte((byte)extra);
    packed.Write(raw);
    using var shader = new MemoryStream();
    using (var writer = new BinaryWriter(shader, System.Text.Encoding.UTF8, true))
    {
        void Text(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            writer.Write(bytes.Length); writer.Write(bytes);
            while (shader.Position % 4 != 0) writer.Write((byte)0);
        }
        Text("5_TRANSPARENT_BASE");
        foreach (int stage in new[] { 15, 17 })
        {
            writer.Write(2);
            foreach (int index in stage == 15 ? new[] { 1, 2 } : new[] { 3, 4 })
            {
                writer.Write(index + relocation); writer.Write(0); writer.Write(33L); writer.Write(stage);
            }
        }
        Text("6_DEFERRED_CUTOUT");
        writer.Write(unrelated);
        while (shader.Position % 4 != 0) writer.Write((byte)0);
        Text("Custom/Voxel");
        writer.Write(new byte[20]);
        writer.Write(1); writer.Write(4);
        foreach (int value in new[] { 0, (int)packed.Length, raw.Length }) { writer.Write(1); writer.Write(1); writer.Write(value); }
        writer.Write((int)packed.Length); writer.Write(packed.ToArray());
    }
    return shader.ToArray();
}
byte[] nativeShader = ShaderFixture(7, 1);
string passHash = ShaderProgramFingerprint.TransparentPass(nativeShader, "Custom/Voxel");
Check(passHash == ShaderProgramFingerprint.TransparentPass(ShaderFixture(7, 2), "Custom/Voxel"), "unrelated shader pass changes preserve glass admission");
Check(passHash != ShaderProgramFingerprint.TransparentPass(ShaderFixture(8, 1), "Custom/Voxel"), "transparent executable changes reject the glass shader contract");
Check(passHash == ShaderProgramFingerprint.TransparentPass(ShaderFixture(7, 1, 1), "Custom/Voxel"), "unrelated shader programs can relocate transparent bytecode indices");
rejected = false;
try { ShaderProgramFingerprint.TransparentPass(new byte[16], "Custom/Voxel"); }
catch (NotSupportedException) { rejected = true; }
Check(rejected, "missing shader pass rejects only its contract");
byte[] AssetFixture(long objectId)
{
    using var asset = new MemoryStream();
    using (var writer = new BinaryWriter(asset, System.Text.Encoding.UTF8, true))
    {
        writer.Write(new byte[20]);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("6000.6.6f1\0"));
        writer.Write(19); writer.Write(false);
        writer.Write(1); writer.Write(48); writer.Write(false); writer.Write((short)-1); writer.Write(new byte[16]);
        writer.Write(1);
        while (asset.Position % 4 != 0) writer.Write((byte)0);
        writer.Write(objectId); writer.Write(0u); writer.Write((uint)nativeShader.Length); writer.Write(0);
        while (asset.Position < 128) writer.Write((byte)0);
        writer.Write(nativeShader);
    }
    byte[] result = asset.ToArray();
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), 108);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4, 4), result.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8, 4), 21);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(12, 4), 128);
    return result;
}
using var firstAsset = new MemoryStream(AssetFixture(17));
using var reorderedAsset = new MemoryStream(AssetFixture(241));
Check(NativeShaderContract.Fingerprint(firstAsset, "Custom/Voxel") == passHash, "asset reader finds the exact named transparent shader");
Check(NativeShaderContract.Fingerprint(reorderedAsset, "Custom/Voxel") == passHash, "asset object ID changes preserve the transparent shader contract");
rejected = false;
try { NativeShaderContract.Fingerprint(new MemoryStream(new byte[16]), "Custom/Voxel"); }
catch (EndOfStreamException) { rejected = true; }
Check(rejected, "truncated native asset fails closed");
rejected = false;
try { ShaderProgramFingerprint.TransparentPass(nativeShader.AsSpan(0, nativeShader.Length - 4).ToArray(), "Custom/Voxel"); }
catch (NotSupportedException) { rejected = true; }
Check(rejected, "truncated compressed shader range fails closed");
Console.WriteLine("PASS native contract fingerprint regression checks: " + checks);
