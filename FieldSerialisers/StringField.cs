using System;
using System.Linq.Expressions;
using System.Reflection;
using UnhollowerBaseLib;
using static UnhollowerBaseLib.IL2CPP;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class StringField : SerialisedField
    {
        public override IntPtr FieldType => il2cpp_class_get_type(Il2CppClassPointerStore<string>.NativeClassPtr);

        public StringField(FieldInfo field) : base(field)
        {
        }

        public override int GetFieldSize(out int align)
        {
            align = sizeof(IntPtr);
            return sizeof(IntPtr);
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var method = ((Func<IntPtr, string>)Il2CppStringToManaged).Method;
            return Expression.Call(method, nativePtr);
        }

        public override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            var method = ((Func<string, IntPtr>)ManagedStringToIl2Cpp).Method;
            return Expression.Call(method, monoObj);
        }
    }
}