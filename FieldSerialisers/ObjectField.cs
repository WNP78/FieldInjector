﻿using System;
using System.Linq.Expressions;
using System.Reflection;
using static FieldInjector.Util;
using static UnhollowerBaseLib.IL2CPP;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class ObjectField : SerialisedField
    {
        public override IntPtr FieldType => il2cpp_class_get_type(GetClassPointerForType(this.TargetType));

        public override int GetFieldSize(out int align)
        {
            align = sizeof(IntPtr);
            return sizeof(IntPtr);
        }

        public ObjectField(FieldInfo field) : base(field)
        {
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var ctor = this.TargetType.GetConstructor(new Type[] { typeof(IntPtr) });
            return Expression.New(ctor, nativePtr);
        }

        public override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            var prop = typeof(Il2CppSystem.Object).GetProperty("Pointer");
            return Expression.Property(monoObj, prop);
        }
    }
}