using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using static UnhollowerBaseLib.IL2CPP;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class CustomStructField<T> : SerialisedField where T : struct
    {
        private readonly IntPtr fieldType;
        private readonly IntPtr fieldClass;
        private int fieldLength;
        private uint fieldAlign;

        public CustomStructField(FieldInfo field) : base(field)
        {
            if (field.FieldType != typeof(T))
            {
                throw new ArgumentException("CustomStructField field type does not match T");
            }

            if (!SerialisationHandler._injectedStructs.TryGetValue(field.FieldType, out this.fieldClass))
            {
                throw new InvalidOperationException("Tried to create CustomStructField for non-injected struct");
            }

            this.fieldType = il2cpp_class_get_type(this.fieldClass);
            this.fieldLength = il2cpp_class_value_size(this.fieldClass, ref this.fieldAlign);
            MelonLogger.Msg($"CSF Length: {this.fieldLength}");
        }

        public override IntPtr FieldType => this.fieldType;

        internal unsafe int Offset => ((MyIl2CppFieldInfo*)this.NativeField)->offset;

        public override int GetFieldSize(out int align)
        {
            align = (int)this.fieldAlign;
            return this.fieldLength;
        }

        internal static unsafe void ClearMem(IntPtr target, int length)
        {
            if (length % 8 == 0)
            {
                for (int i = 0; i < length / 8; i++)
                {
                    ((ulong*)target)[i] = 0;
                }
            }
            else if (length % 4 == 0)
            {
                for (int i = 0; i < length / 4; i++)
                {
                    ((uint*)target)[i] = 0;
                }
            }
            else if (length % 2 == 0)
            {
                for (int i = 0; i < length / 2; i++)
                {
                    ((ushort*)target)[i] = 0;
                }
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    ((byte*)target)[i] = 0;
                }
            }
        }

        internal static unsafe IntPtr GetFieldPtrAndClear(IntPtr obj, IntPtr field, int length)
        {
            var offset = ((MyIl2CppFieldInfo*)field)->offset;
            IntPtr ptr = obj + offset;
            ClearMem(ptr, length);
            return ptr;
        }

        internal static unsafe IntPtr GetFieldPtr(IntPtr obj, IntPtr field)
        {
            var offset = ((MyIl2CppFieldInfo*)field)->offset;
            return obj + offset;
        }

        public override IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
        {
            if (this.NativeField == IntPtr.Zero)
            {
                throw new InvalidOperationException("Something went very wrong");
            }

            Expression monoValue = Expression.Field(monoObj, this.field);

            MethodInfo getFieldPtr = ((Func<IntPtr, IntPtr, int, IntPtr>)GetFieldPtrAndClear).Method;

            var ptrVar = Expression.Variable(typeof(IntPtr), "fieldPtr");
            var managedVar = Expression.Variable(typeof(T), "managedValue");
            Expression fieldPtr = Expression.Call(getFieldPtr, nativePtr, Expression.Constant(this.NativeField), Expression.Constant(this.fieldLength));
            yield return Expression.Block(new ParameterExpression[] { ptrVar, managedVar },
                Expression.Assign(ptrVar, fieldPtr),
                Expression.Assign(managedVar, monoValue),
                StructSerialiser<T>.Instance.GenerateSerialiser(managedVar, ptrVar));
        }

        public override IEnumerable<Expression> GetDeserialiseExpression(Expression monoObj, Expression nativePtr, Expression fieldPtr)
        {
            var getFieldPtr = ((Func<IntPtr, IntPtr, IntPtr>)GetFieldPtr).Method;
            var targetPtr = Expression.Add(nativePtr, Expression.Constant(this.Offset));
            var deserialisedStruct = StructSerialiser<T>.Instance.GenerateDeserialiser(targetPtr);
            yield return Expression.Assign(
                Expression.Field(monoObj, this.field),
                deserialisedStruct);
        }

        public override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            throw new NotImplementedException(); // not called since we overrode GetSerialiseExpression
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            throw new NotImplementedException(); // not called since we overrode GetDeserialiseExpression
        }
    }
}