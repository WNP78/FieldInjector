﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime.VersionSpecific.FieldInfo;
using static UnhollowerBaseLib.IL2CPP;
using UnhollowerBaseLib.Runtime;
using static FieldInjector.Util;

namespace FieldInjector
{
    internal unsafe abstract class SerialisedField
    {
        private FieldInfo field;

        protected abstract IntPtr FieldType { get; }

        public IntPtr NativeField { get; set; }

        public FieldInfo ManagedField => this.field;

        protected virtual Type TargetType => this.field.FieldType;

        protected SerialisedField(FieldInfo field)
        {
            this.field = field;
        }

        protected abstract Expression GetNativeToMonoExpression(Expression nativePtr);

        protected abstract Expression GetMonoToNativeExpression(Expression monoObj);

        public virtual IEnumerable<Expression> GetDeserialiseExpression(Expression monoObj, Expression nativePtr, Expression fieldPtr)
        {
            // OLD:
            // monoObj.field = NativeToMono(il2cpp_field_get_value_object(nativeFieldInfo, nativePtr));
            /* New 
             * fieldPtr = il2cpp_field_get_value_object(nativeFieldInfo, nativePtr);
             * monoObj.field = fieldPtr != IntPtr.Zero ? NativeToMono(fieldPtr) : default;
            */
            if (this.NativeField == IntPtr.Zero)
            {
                throw new InvalidOperationException("Something went very wrong");
            }

            MethodInfo get_value = ((Func<IntPtr, IntPtr, IntPtr>)il2cpp_field_get_value_object).Method;

            Expression fieldValuePtr = Expression.Call(
                get_value,
                Expression.Constant(this.NativeField),
                nativePtr);

            yield return Expression.Assign(fieldPtr, fieldValuePtr);

            Expression hasValue = Expression.NotEqual(fieldPtr, Expression.Constant(IntPtr.Zero));

            yield return Expression.Assign(Expression.Field(monoObj, this.field),
                Expression.Condition(hasValue,
                    this.GetNativeToMonoExpression(fieldPtr),
                    Expression.Default(this.field.FieldType)
                    )
                );
        }

        public virtual IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
        {
            // valueType:
            // field_set_value_object(nativePtr, this.NativeField, MonoToNative(monoObj.field))
            // refType:
            // field_set_value_object(nativePtr, this.NativeField, monoObj.field != null ? MonoToNative(monoObj.field) : IntPtr.Zero)

            if (this.NativeField == IntPtr.Zero)
            {
                throw new InvalidOperationException("Something went very wrong");
            }

            MethodInfo set_value = ((Action<IntPtr, IntPtr, IntPtr>)field_set_value_object).Method;

            Expression monoValue = Expression.Field(monoObj, this.field);

            Expression fieldValuePtr = this.GetMonoToNativeExpression(monoValue);

            if (!this.field.FieldType.IsValueType)
            {
                fieldValuePtr = Expression.Condition(
                    Expression.NotEqual(monoValue, Expression.Constant(null)),
                    fieldValuePtr, Expression.Constant(IntPtr.Zero));
            }


            yield return Expression.Call(set_value,
                nativePtr,
                Expression.Constant(this.NativeField),
                fieldValuePtr);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "il2cpp method wrapper")]
        private static void field_set_value_object(IntPtr instance, IntPtr field, IntPtr obj)
        {
            il2cpp_field_set_value(instance, field, (void*)obj);
        }

        public static SerialisedField InferFromField(FieldInfo field)
        {
            Type fieldType = field.FieldType;
            if (typeof(Il2CppSystem.Object).IsAssignableFrom(fieldType))
            {
                return new ObjectField(field);
            }
            else if (fieldType == typeof(string))
            {
                return new StringField(field);
            }
            else if (fieldType.IsValueType)
            {
                return new StructField(field);
            }
            else if (fieldType.IsArray && fieldType.GetArrayRank() == 1)
            {
                return new ArrayField(field);
            }
            else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return new ListField(field);
            }

            throw new NotSupportedException($"Field type not supported: {field}");
        }

        public abstract int GetFieldSize(out int align);

        public void FillFieldInfoStruct(INativeFieldInfoStruct infoOut, Il2CppClass* klassPtr, ref int offset)
        {
            infoOut.Name = Marshal.StringToHGlobalAnsi(this.field.Name);
            infoOut.Parent = klassPtr;

            var typePtr = (MyIl2CppType*)Marshal.AllocHGlobal(Marshal.SizeOf<MyIl2CppType>());
            *typePtr = *(MyIl2CppType*)this.FieldType;

            typePtr->attrs = (ushort)Il2CppSystem.Reflection.FieldAttributes.Public;

            infoOut.Type = (Il2CppTypeStruct*)typePtr;

            var size = this.GetFieldSize(out int align);
            offset = AlignTo(offset, align);

            infoOut.Offset = offset;

            offset += size;
        }

        public override string ToString()
        {
            return this.GetType().Name + ":" + this.TargetType;
        }
    }
}
