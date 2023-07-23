using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;
using static UnhollowerBaseLib.IL2CPP;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class CustomStructField<T> : SerialisedField where T : struct
    {
        private readonly IntPtr fieldType;
        private readonly IntPtr fieldClass;

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
            var serialiser = StructSerialiser<T>.Instance.GenerateSerialiser(monoObj, dataPtr);

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
}