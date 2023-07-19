﻿using System;
using System.Linq.Expressions;
using System.Reflection;
using static UnhollowerBaseLib.IL2CPP;
using static FieldInjector.Util;

namespace FieldInjector
{
    internal class ObjectField : SerialisedField
        {
            protected override IntPtr FieldType => il2cpp_class_get_type(GetClassPointerForType(this.TargetType));

            public override int GetFieldSize(out int align)
            {
                align = sizeof(IntPtr);
                return sizeof(IntPtr);
            }

            public ObjectField(FieldInfo field) : base(field) { }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                var ctor = this.TargetType.GetConstructor(new Type[] { typeof(IntPtr) });
                return Expression.New(ctor, nativePtr);
            }

            protected override Expression GetMonoToNativeExpression(Expression monoObj)
            {
                var prop = typeof(Il2CppSystem.Object).GetProperty("Pointer");
                return Expression.Property(monoObj, prop);
            }
        }

}
