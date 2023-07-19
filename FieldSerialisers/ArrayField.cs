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

            _proxyType = typeof(ILCollections.List<>).MakeGenericType(elementType);
            var classPtr = GetClassPointerForType(_proxyType);

            Log($"ArrayField List<{elementType.Name}> ptr = {classPtr}", 5);

            _fieldType = il2cpp_class_get_type(classPtr);
        }

        public ArrayField(FieldInfo field) : this(field.FieldType.GetElementType(), field)
        {
        }

        protected override IntPtr FieldType => _fieldType;

        protected override Expression GetMonoToNativeExpression(Expression monoObj)
        {
            return ConvertArrayToIl2CppList(monoObj, _proxyType);
        }

        protected override Expression GetNativeToMonoExpression(Expression nativePtr)
        {
            var ctor = _proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
            Expression cppList = Expression.New(ctor, nativePtr);

            return ConvertListToMono(cppList, this.field.FieldType);
        }

        public static Expression ConvertListToMono(Expression cppList, Type monoType)
        {
            if (!monoType.IsArray) { throw new ArgumentException("monoType is not an array!"); }
            Type monoElementType = monoType.GetElementType();

            if (monoElementType.IsValueType)
            {
                Type cppElementType = cppList.Type.GetGenericArguments()[0];

                MethodInfo convertStructList = ((Func<ILCollections.List<int>, int[]>)ConvertStructList<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);

                return Expression.Call(convertStructList, cppList);
            }
            else
            {
                MethodInfo convertGeneralList = ((Func<ILCollections.List<int>, int[]>)ConvertGeneralList)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);

                return Expression.Call(convertGeneralList, cppList);
            }
        }

        public static Expression ConvertArrayToIl2CppList(Expression monoArray, Type cppType)
        {
            Type cppElementType = cppType.GetGenericArguments()[0];
            Type monoElementType = monoArray.Type.GetElementType();

            Expression cppList;

            if (cppElementType.IsValueType)
            {
                MethodInfo convertStructArray = ((Func<int[], ILCollections.List<int>>)ConvertStructArray<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);

                cppList = Expression.Call(convertStructArray, monoArray);
            }
            else
            {
                MethodInfo convertGeneralArray = ((Func<int[], ILCollections.List<int>>)ConvertGeneralArray)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);

                cppList = Expression.Call(convertGeneralArray, monoArray);
            }

            var ptr = typeof(Il2CppSystem.Object).GetProperty("Pointer");
            return Expression.Property(cppList, ptr);
        }

        private static TMono[] ConvertStructList<TMono, TCpp>(ILCollections.List<TCpp> cppList)
            where TMono : unmanaged
            where TCpp : unmanaged
        {
            int size = sizeof(TMono);
            if (size != sizeof(TCpp)) { throw new ArgumentException("Size mismatch in array copy."); }

            TMono[] res = new TMono[cppList.Count];
            fixed (TMono* resPtr = res)
            {
                for (int i = 0; i < res.Length; i++)
                {
                    *(TCpp*)(resPtr + i) = cppList[i];
                }
            }

            return res;
        }

        private static T[] ConvertGeneralList<T>(ILCollections.List<T> cppList)
        {
            T[] res = new T[cppList.Count];
            for (int i = 0; i < res.Length; i++)
            {
                res[i] = cppList[i];
            }

            return res;
        }

        public static ILCollections.List<TCpp> ConvertStructArray<TMono, TCpp>(TMono[] monoArray)
            where TMono : unmanaged
            where TCpp : unmanaged
        {
            var res = new ILCollections.List<TCpp>(monoArray.Length);
            fixed (TMono* monoPtr = monoArray)
            {
                for (int i = 0; i < monoArray.Length; i++)
                {
                    TMono* ptr = &monoPtr[i];
                    res.Add(*(TCpp*)ptr);
                }
            }

            return res;
        }

        public static ILCollections.List<T> ConvertGeneralArray<T>(T[] monoArray)
        {
            var res = new ILCollections.List<T>(monoArray.Length);

            for (int i = 0; i < monoArray.Length; i++)
            {
                res.Add(monoArray[i]);
            }

            return res;
        }
    }
}