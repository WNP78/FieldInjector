using System;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using MelonLoader;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;
using UnhollowerBaseLib.Runtime.VersionSpecific.Type;
using static UnhollowerBaseLib.IL2CPP;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;

namespace FieldInjector
{
    internal static class Util
    {
        internal static Expression LogExpression(string msg, Expression ex)
        {
            var log = ((Action<string>)MelonLogger.Msg).Method;
            var toStr = typeof(object).GetMethod("ToString", new Type[] { });
            var concat = ((Func<string, string, string>)string.Concat).Method;

            Expression str = Expression.Call(Expression.Convert(ex, typeof(object)), toStr);

            if (!ex.Type.IsValueType)
            {
                var isNull = Expression.Equal(ex, Expression.Constant(null));
                str = Expression.Condition(
                    isNull,
                    Expression.Constant("null"),
                    str);
            }

            return Expression.Call(log, Expression.Call(concat, Expression.Constant(msg), str));
        }

        internal static int AlignTo(int value, int alignment)
        {
            if (alignment > 0)
            {
                int mod = value % alignment;
                if (mod > 0)
                {
                    value += alignment - mod;
                }
            }

            return value;
        }

        internal static unsafe int GetTypeSize(INativeTypeStruct type, out uint align)
        {
            // TODO: (FieldLayout.cpp) FieldLayout::GetTypeSizeAndAlignment
            var t = type.Type;
            align = 0;
        handle_enum:
            switch (t)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return 1;
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return 2;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return 2; // I think? Il2CppChar being wchar_t probably
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return 4;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return 8;
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return 8; // assuming 64-bit, deal with it
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_FNPTR:
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    return IntPtr.Size;
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    var klass1 = Wrap((Il2CppClass*)il2cpp_class_from_il2cpp_type(type.Pointer));
                    if (type.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE && klass1.EnumType)
                    {
                        var chrs = (byte*)klass1.Name;
                        MelonLogger.Msg($"Enum Basetype debug: {klass1.Pointer}, {(IntPtr)chrs}, {Marshal.StringToHGlobalAnsi("test")}");
                        MelonLogger.Msg($"Chr: {(char)(*chrs)}");
                        MelonLogger.Msg($"elemt={(IntPtr)klass1.ElementClass}");
                        MelonLogger.Msg($"elemtname={Marshal.PtrToStringAnsi(il2cpp_type_get_name((IntPtr)klass1.ElementClass))}");
                        t = ((MyIl2CppType*)il2cpp_class_enum_basetype(klass1.Pointer))->type;
                        goto handle_enum;
                    }

                    return il2cpp_class_value_size(il2cpp_class_from_il2cpp_type(type.Pointer), ref align);
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                default:
                    throw new InvalidOperationException();
            }
        }

        internal static IntPtr GetClassPointerForType<T>()
        {
            if (typeof(T) == typeof(void)) { return Il2CppClassPointerStore<Il2CppSystem.Void>.NativeClassPtr; }
            return Il2CppClassPointerStore<T>.NativeClassPtr;
        }

        internal static IntPtr GetClassPointerForType(Type type, bool bypassEnums = false)
        {
            if (type == typeof(void)) { return Il2CppClassPointerStore<Il2CppSystem.Void>.NativeClassPtr; }

            if (type.IsEnum && !bypassEnums)
            {
                throw new NotSupportedException("Trying to get pointer for enum type");
            }

            return (IntPtr)typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField("NativeClassPtr").GetValue(null);
        }

        internal static void SetClassPointerForType(Type type, IntPtr value)
        {
            typeof(Il2CppClassPointerStore<>).MakeGenericType(type)
                .GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr)).SetValue(null, value);
        }
    }
}