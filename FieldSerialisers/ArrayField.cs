using MelonLoader;
using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnhollowerBaseLib;
using static FieldInjector.Util;
using static MelonLoader.MelonLogger;
using static UnhollowerBaseLib.IL2CPP;
using ILCollections = Il2CppSystem.Collections.Generic;

namespace FieldInjector.FieldSerialisers
{
    internal unsafe class ArrayField : SerialisedField
    {
        protected IntPtr _fieldType;

        protected Type _proxyType;

        protected Type _elementType;

        public override int GetFieldSize(out int align)
        {
            align = sizeof(IntPtr);
            return sizeof(IntPtr);
        }

        protected ArrayField(Type elementType, FieldInfo field) : base(field)
        {
            this._elementType = elementType;

            Log($"Creating array field for {field.FieldType.Name} {field.Name}", 5);

            if (elementType.IsEnum)
            {
                elementType = elementType.GetEnumUnderlyingType();
            }

            Log($"elementType = {elementType}", 5);

            if (elementType.IsValueType)
            {
                this._proxyType = typeof(Il2CppStructArray<>).MakeGenericType(elementType);
            }
            else if (elementType == typeof(string))
            {
                this._proxyType = typeof(Il2CppStringArray);
            }
            else
            {
                this._proxyType = typeof(Il2CppReferenceArray<>).MakeGenericType(elementType);
            }

            Log($"ProxyType = {this._proxyType}", 5);

            var classPtr = GetClassPointerForType(elementType);

            Log($"classPtr = {classPtr}", 5);

            var arrayClassPtr = il2cpp_array_class_get(classPtr, 1);

            Log($"arrayClassPtr = {arrayClassPtr}", 5);

            this._fieldType = il2cpp_class_get_type(arrayClassPtr);
            
            Log($"fieldType = {this._fieldType}", 5);
        }

        public ArrayField(FieldInfo field) : this(field.FieldType.GetElementType(), field)
        {
        }

        public override IntPtr FieldType => this._fieldType;

        public override Expression GetManagedToNativeExpression(Expression managedObj)
        {
            Expression cppArray;
            var managedElementType = this._elementType;
            if (this._proxyType == typeof(Il2CppStringArray))
            {
                cppArray = Expression.New(this._proxyType.GetConstructor(new Type[] { typeof(string[]) }), managedObj);
            }
            else if (managedElementType.IsValueType)
            {
                Type cppElementType = this._proxyType.BaseType.GetGenericArguments()[0];

                // only supports unmanaged structs
                cppArray = Expression.Call(((Func<float[], Il2CppStructArray<float>>)ConvertArray<float, float>).Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType, cppElementType), managedObj);
            }
            else
            {
                Msg($"managedElementType = {managedElementType}, _proxyType = {this._proxyType}");
                cppArray = Expression.New(this._proxyType.GetConstructor(new Type[] { managedElementType.MakeArrayType() }), managedObj);
            }

            return Expression.Property(cppArray, "Pointer");
        }

        public override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
            Expression cppList = Expression.New(ctor, nativePtr);

            return ConvertArrayToManaged(cppList, this._elementType.MakeArrayType());
        }

        public static Expression ConvertArrayToManaged(Expression cppArray, Type managedType)
        {
            if (!managedType.IsArray) { throw new ArgumentException("managedType is not an array!"); }
            // cppArray is of type _proxyType, managedtype is an array
            var managedElementType = managedType.GetElementType();
            var cppElementType = cppArray.Type.BaseType.GetGenericArguments()[0];

            if (cppElementType == managedElementType)
            {
                return Expression.Call(((Func<Il2CppArrayBase<float>, float[]>)ConvertArray).Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType), cppArray);
            }
            else if (managedElementType.IsValueType)
            {
                return Expression.Call(
                    ((Func<Il2CppStructArray<float>, float[]>)ConvertStructArray<float, float>).Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType, cppElementType), cppArray);
            }

            throw new NotImplementedException();
        }

        public static T[] ConvertArray<T>(Il2CppArrayBase<T> array)
        {
            var res = new T[array.Length];
            array.CopyTo(res, 0);
            return res;
        }

        public static TM[] ConvertStructArray<TM, TC>(Il2CppStructArray<TC> array) where TM : unmanaged where TC : unmanaged
        {
            var res = new TM[array.Length];
            fixed (TM* destPtr = res)
            {
                for (int i = 0; i < res.Length; i++)
                {
                    *(TC*)(destPtr + i) = array[i];
                }
            }

            return res;
        }

        public static Il2CppStructArray<TC> ConvertArray<TM, TC>(TM[] array) where TM : unmanaged where TC : unmanaged
        {
            var res = new Il2CppStructArray<TC>(array.Length);
            
            fixed (TM* srcPtr = array)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    res[i] = *(TC*)(srcPtr + i);
                }
            }

            return res;
        }
    }
}