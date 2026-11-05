using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GraphicalTweaks
{
    internal static class NativeContractFingerprint
    {
        internal static Dictionary<string, string> VerifyFeatures(ModuleDefinition module, NativeFeatureContract[] features, Action<NativeFeatureContract> additionalCheck)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var feature in features)
            {
                string reason = null;
                try
                {
                    Verify(module, feature.entries);
                    additionalCheck(feature);
                }
                catch (Exception error) { reason = error.GetType().Name + ": " + error.Message; }
                result.Add(feature.name, reason);
            }
            return result;
        }

        internal static void Verify(ModuleDefinition module, NativeContractEntry[] entries)
        {
            if (entries == null || entries.Length == 0) throw new NotSupportedException("Empty native feature contract.");
            foreach (var entry in entries)
            {
                var type = module.GetType(entry.type) ?? throw new NotSupportedException("Missing native type " + entry.type);
                string actual;
                switch (entry.kind)
                {
                    case "layout": actual = Layout(type); break;
                    case "field":
                        var field = type.Fields.SingleOrDefault(item => item.FullName == entry.member);
                        if (field == null) throw new NotSupportedException("Missing native field " + entry.member);
                        actual = Field(field);
                        break;
                    case "method":
                        var method = type.Methods.SingleOrDefault(item => item.FullName == entry.member);
                        if (method == null) throw new NotSupportedException("Missing native method " + entry.member);
                        actual = Method(method);
                        break;
                    default: throw new NotSupportedException("Unknown native contract kind " + entry.kind);
                }
                if (actual != entry.hash)
                    throw new NotSupportedException("Native contract changed: " + (entry.member ?? entry.type));
            }
        }

        internal static string Method(MethodDefinition method) => Hash(writer =>
        {
            writer.Write(method.FullName);
            writer.Write((int)method.Attributes);
            writer.Write((int)method.ImplAttributes);
            foreach (var parameter in method.Parameters)
            {
                writer.Write((int)parameter.Attributes);
                WriteConstant(writer, parameter.HasConstant, parameter.Constant);
            }
            writer.Write(method.HasBody);
            if (!method.HasBody) return;
            var body = method.Body;
            var indices = body.Instructions.Select((instruction, index) => (instruction, index))
                .ToDictionary(item => item.instruction, item => item.index);
            writer.Write(body.InitLocals);
            writer.Write(body.Variables.Count);
            foreach (var local in body.Variables) writer.Write(local.VariableType.FullName);
            writer.Write(body.Instructions.Count);
            foreach (var instruction in body.Instructions)
            {
                writer.Write((int)instruction.OpCode.Code);
                WriteOperand(writer, instruction.Operand, indices);
            }
            writer.Write(body.ExceptionHandlers.Count);
            foreach (var handler in body.ExceptionHandlers)
            {
                writer.Write((int)handler.HandlerType);
                writer.Write(handler.CatchType?.FullName ?? "");
                foreach (var boundary in new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart })
                    writer.Write(boundary == null ? -1 : indices[boundary]);
            }
        });

        internal static string Layout(TypeDefinition type) => Hash(writer =>
        {
            writer.Write(type.FullName);
            writer.Write((int)type.Attributes);
            writer.Write(type.BaseType?.FullName ?? "");
            writer.Write(type.PackingSize);
            writer.Write(type.ClassSize);
            foreach (var field in type.Fields)
            {
                writer.Write(field.FullName);
                writer.Write((int)field.Attributes);
                writer.Write(field.Offset);
                WriteConstant(writer, field.HasConstant, field.Constant);
            }
        });

        internal static string Field(FieldDefinition field) => Hash(writer =>
        {
            writer.Write(field.FullName);
            writer.Write((int)field.Attributes);
            writer.Write(field.Offset);
            WriteConstant(writer, field.HasConstant, field.Constant);
        });

        private static void WriteConstant(BinaryWriter writer, bool present, object value)
        {
            writer.Write(present);
            if (!present) return;
            writer.Write(value?.GetType().FullName ?? "null");
            writer.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }

        private static string Hash(Action<BinaryWriter> describe)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) describe(writer);
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void WriteOperand(BinaryWriter writer, object operand, Dictionary<Instruction, int> indices)
        {
            if (operand == null) { writer.Write((byte)0); return; }
            if (operand is MemberReference member)
            {
                writer.Write((byte)1);
                writer.Write(member.FullName);
                var type = member as TypeReference ?? member.DeclaringType;
                writer.Write(type?.Scope?.Name ?? "");
                return;
            }
            if (operand is Instruction target) { writer.Write((byte)2); writer.Write(indices[target]); return; }
            if (operand is Instruction[] targets)
            {
                writer.Write((byte)3);
                writer.Write(targets.Length);
                foreach (var instruction in targets) writer.Write(indices[instruction]);
                return;
            }
            if (operand is VariableDefinition local) { writer.Write((byte)4); writer.Write(local.Index); return; }
            if (operand is ParameterDefinition parameter) { writer.Write((byte)5); writer.Write(parameter.Index); return; }
            if (operand is CallSite call) { writer.Write((byte)6); writer.Write(call.FullName); return; }
            switch (operand)
            {
                case string text: writer.Write((byte)7); writer.Write(text); break;
                case int value: writer.Write((byte)8); writer.Write(value); break;
                case sbyte value: writer.Write((byte)9); writer.Write(value); break;
                case byte value: writer.Write((byte)10); writer.Write(value); break;
                case long value: writer.Write((byte)11); writer.Write(value); break;
                case float value: writer.Write((byte)12); writer.Write(value); break;
                case double value: writer.Write((byte)13); writer.Write(value); break;
                default: throw new NotSupportedException("Unknown native IL operand " + operand.GetType().FullName);
            }
        }
    }
}
