using System;
using System.Linq.Expressions;
using System.Reflection;
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

        public override int GetFieldSize(out int align)
        {
            align = sizeof(IntPtr);
            return sizeof(IntPtr);
        }

        protected ArrayField(Type elementType, FieldInfo field) : base(field)
        {
            Log($"Creating array field for {field.FieldType.Name} {field.Name}", 5);

            if (elementType.IsEnum)
            {
                elementType = elementType.GetEnumUnderlyingType();
            }

            Log($"elementType = {elementType}", 5);

            this._proxyType = typeof(ILCollections.List<>).MakeGenericType(elementType);
            var classPtr = GetClassPointerForType(this._proxyType);

            Log($"ArrayField List<{elementType.Name}> ptr = {classPtr}", 5);

            this._fieldType = il2cpp_class_get_type(classPtr);
        }

        public ArrayField(FieldInfo field) : this(field.FieldType.GetElementType(), field)
        {
        }

        protected override IntPtr FieldType => this._fieldType;

        protected override Expression GetManagedToNativeExpression(Expression managedObj)
        {
            return ConvertArrayToIl2CppList(managedObj, this._proxyType);
        }

        protected override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
            Expression cppList = Expression.New(ctor, nativePtr);

            return ConvertListToManaged(cppList, this.field.FieldType);
        }

        public static Expression ConvertListToManaged(Expression cppList, Type managedType)
        {
            if (!managedType.IsArray) { throw new ArgumentException("managedType is not an array!"); }
            Type managedElementType = managedType.GetElementType();

            if (managedElementType.IsValueType)
            {
                Type cppElementType = cppList.Type.GetGenericArguments()[0];

                MethodInfo convertStructList = ((Func<ILCollections.List<int>, int[]>)ConvertStructList<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType, cppElementType);

                return Expression.Call(convertStructList, cppList);
            }
            else
            {
                MethodInfo convertGeneralList = ((Func<ILCollections.List<int>, int[]>)ConvertGeneralList)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType);

                return Expression.Call(convertGeneralList, cppList);
            }
        }

        public static Expression ConvertArrayToIl2CppList(Expression managedArray, Type cppType)
        {
            Type cppElementType = cppType.GetGenericArguments()[0];
            Type managedElementType = managedArray.Type.GetElementType();

            Expression cppList;

            if (cppElementType.IsValueType)
            {
                MethodInfo convertStructArray = ((Func<int[], ILCollections.List<int>>)ConvertStructArray<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType, cppElementType);

                cppList = Expression.Call(convertStructArray, managedArray);
            }
            else
            {
                MethodInfo convertGeneralArray = ((Func<int[], ILCollections.List<int>>)ConvertGeneralArray)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(managedElementType);

                cppList = Expression.Call(convertGeneralArray, managedArray);
            }

            var ptr = typeof(Il2CppSystem.Object).GetProperty("Pointer");
            return Expression.Property(cppList, ptr);
        }

        /// <summary>
        /// IL2CPP List -> Managed Array
        /// </summary>
        private static TManaged[] ConvertStructList<TManaged, TCpp>(ILCollections.List<TCpp> cppList)
            where TManaged : unmanaged
            where TCpp : unmanaged
        {
            int size = sizeof(TManaged);
            if (size != sizeof(TCpp)) { throw new ArgumentException("Size mismatch in array copy."); }

            TManaged[] res = new TManaged[cppList.Count];
            fixed (TManaged* resPtr = res)
            {
                for (int i = 0; i < res.Length; i++)
                {
                    *(TCpp*)(resPtr + i) = cppList[i];
                }
            }

            return res;
        }

        /// <summary>
        /// IL2CPP List -> Managed Array
        /// </summary>
        private static T[] ConvertGeneralList<T>(ILCollections.List<T> cppList)
        {
            T[] res = new T[cppList.Count];
            for (int i = 0; i < res.Length; i++)
            {
                res[i] = cppList[i];
            }

            return res;
        }

        /// <summary>
        /// Managed Array -> IL2CPP List
        /// </summary>
        public static ILCollections.List<TCpp> ConvertStructArray<TManaged, TCpp>(TManaged[] managedArray)
            where TManaged : unmanaged
            where TCpp : unmanaged
        {
            var res = new ILCollections.List<TCpp>(managedArray.Length);
            fixed (TManaged* managedPtr = managedArray)
            {
                for (int i = 0; i < managedArray.Length; i++)
                {
                    TManaged* ptr = &managedPtr[i];
                    res.Add(*(TCpp*)ptr);
                }
            }

            return res;
        }

        /// <summary>
        /// Managed Array -> IL2CPP list
        /// </summary>
        public static ILCollections.List<T> ConvertGeneralArray<T>(T[] managedArray)
        {
            var res = new ILCollections.List<T>(managedArray.Length);

            for (int i = 0; i < managedArray.Length; i++)
            {
                res.Add(managedArray[i]);
            }

            return res;
        }
    }
}