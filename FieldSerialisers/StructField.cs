using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;
using static FieldInjector.Util;
using static UnhollowerBaseLib.IL2CPP;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class CustomStructField<T> : SerialisedField where T : struct
    {
        private readonly IntPtr fieldType;
        private readonly IntPtr fieldClass;
        private StructSerialiser<T> structSerialiser;

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
        }

        public override IntPtr FieldType => this.fieldType;

        public override int GetFieldSize(out int align)
        {
            align = 0;
            return (int)(Wrap((Il2CppClass*)this.fieldClass).ActualSize - Marshal.SizeOf<Il2CppObject>());
        }

        public override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            // temp var to store the pointer to the il2cpp object
            var temp = Expression.Variable(typeof(IntPtr), "ptr");

            // create il2cpp boxed struct
            var obj = Expression.Call(((Func<IntPtr, IntPtr>)il2cpp_object_new).Method, Expression.Constant(this.fieldClass));

            // get the struct data (skip past the object headers)
            var dataPtr = Expression.Call(((Func<IntPtr, IntPtr>)il2cpp_object_unbox).Method, temp);

            // run serialiser on struct data ptr
            var serialiser = StructSerialiser<T>.Instance.GenerateSerialiser(Expression.Field(monoObj, this.field), dataPtr);

            // combine into a block expression
            return Expression.Block(new ParameterExpression[] { temp },
                Expression.Assign(temp, obj), // temp = newobject
                serialiser, // serialise(temp.data)
                temp); // return temp
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var dataPtr = Expression.Call(((Func<IntPtr, IntPtr>)il2cpp_object_unbox).Method, nativePtr);
            var returnStruct = StructSerialiser<T>.Instance.GenerateDeserialiser(nativePtr);
            return returnStruct;
        }
    }

    internal unsafe class StructField : SerialisedField
    {
        public StructField(FieldInfo field) : base(field)
        {
            if (base.TargetType.IsEnum)
            {
                this._serialisedType = base.TargetType.GetEnumUnderlyingType();
            }
            else
            {
                this._serialisedType = base.TargetType;
            }

            this.fieldClass = GetClassPointerForType(this.TargetType);
            this._fieldType = il2cpp_class_get_type(this.fieldClass);
        }

        private readonly IntPtr fieldClass;

        private readonly IntPtr _fieldType;

        private readonly Type _serialisedType;

        public override IntPtr FieldType => this._fieldType;

        protected override Type TargetType => this._serialisedType;

        public override unsafe int GetFieldSize(out int align)
        {
            align = 0;
            return (int)(Wrap((Il2CppClass*)this.fieldClass).ActualSize - Marshal.SizeOf<Il2CppObject>());
        }

        public static unsafe T GetValue<T>(IntPtr obj, IntPtr field) where T : unmanaged
        {
            MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;
            void* dest = (char*)obj + fieldInfo->offset;

            return *(T*)dest;
        }

        public override Expression GetManagedToNativeExpression(Expression monoValue)
        {
            throw new NotImplementedException();
        }

        internal static unsafe void SetValue<T>(T value, IntPtr obj, IntPtr field) where T : unmanaged
        {
            MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;

            void* dest = (byte*)obj + fieldInfo->offset;
            *(T*)dest = value;
        }

        public override IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
        {
            var freeMethod = new Action<IntPtr>(Marshal.FreeHGlobal).Method;

            if (this.NativeField == IntPtr.Zero)
            {
                throw new InvalidOperationException("Something went very wrong");
            }

            Expression monoValue = Expression.Field(monoObj, this.field);

            MethodInfo setValue = ((Action<int, IntPtr, IntPtr>)SetValue).Method
                .GetGenericMethodDefinition()
                .MakeGenericMethod(monoValue.Type);

            yield return Expression.Call(setValue, monoValue, nativePtr, Expression.Constant(this.NativeField));
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            // for normal struct types
            // new Il2CppSystem.Object(ptr).Unbox<T>()
            var ctor = typeof(Il2CppSystem.Object).GetConstructor(new Type[] { typeof(IntPtr) });
            var unbox = typeof(Il2CppSystem.Object).GetMethod("Unbox")
                            .MakeGenericMethod(this.TargetType);

            Expression res = Expression.Call(Expression.New(ctor, nativePtr), unbox);

            if (res.Type != this.field.FieldType)
            {
                res = Expression.Convert(res, this.field.FieldType);
            }

            return res;
        }
    }
}