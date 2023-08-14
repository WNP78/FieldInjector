using Iced.Intel;
using System;
using System.Collections.Generic;
using System.IO;

namespace FieldInjector
{
    internal static class XrefScannerLowLevelCustom
    {
        public static unsafe IEnumerable<IntPtr> JumpTargets(IntPtr codeStart)
        {
            if (codeStart == IntPtr.Zero) throw new NullReferenceException(nameof(codeStart));

            var stream = new UnmanagedMemoryStream((byte*)codeStart, 1000, 1000, FileAccess.Read);
            var codeReader = new StreamCodeReader(stream);
            var decoder = Decoder.Create(IntPtr.Size * 8, codeReader);
            decoder.IP = (ulong)codeStart;

            return JumpTargetsImpl(decoder);
        }

        private static IEnumerable<IntPtr> JumpTargetsImpl(Decoder myDecoder)
        {
            while (true)
            {
                myDecoder.Decode(out var instruction);
                if (myDecoder.LastError == DecoderError.NoMoreBytes) yield break;
                if (instruction.Mnemonic == Mnemonic.Int3) yield break;

                if (instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.Call)
                {
                    yield return (IntPtr)ExtractTargetAddress(in instruction);
                }
            }
        }

        private static ulong ExtractTargetAddress(in Instruction instruction)
        {
            switch (instruction.Op0Kind)
            {
                case OpKind.NearBranch16:
                    return instruction.NearBranch16;

                case OpKind.NearBranch32:
                    return instruction.NearBranch32;

                case OpKind.NearBranch64:
                    return instruction.NearBranch64;

                case OpKind.FarBranch16:
                    return instruction.FarBranch16;

                case OpKind.FarBranch32:
                    return instruction.FarBranch32;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}