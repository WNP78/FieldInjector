using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using ILCollections = Il2CppSystem.Collections.Generic;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class ListField : ArrayField
    {
        public ListField(FieldInfo field) : base(field.FieldType.GetGenericArguments()[0], field)
        {
        }

        public override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            MethodInfo toArray = monoObj.Type.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance);

            return base.GetManagedToNativeExpression(Expression.Call(monoObj, toArray));
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var res = base.GetNativeToManagedExpression(nativePtr);
            var ctor = this.field.FieldType.GetConstructor(new Type[] { typeof(IEnumerable<>).MakeGenericType(this._elementType) });
            return Expression.New(ctor, res);
        }
    }
}