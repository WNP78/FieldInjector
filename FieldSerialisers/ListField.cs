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

        protected override Expression GetManagedToNativeExpression(Expression monoObj)
        {
            return Expression.Property(ListToCpp(monoObj, _proxyType), "Pointer");
        }

        protected override Expression GetNativeToManagedExpression(Expression nativePtr)
        {
            var ctor = _proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
            Expression cppList = Expression.New(ctor, nativePtr);
            return ListToManaged(cppList, this.field.FieldType);
        }

        public static Expression ListToCpp(Expression list, Type cppType)
        {
            Type monoElementType = list.Type.GetGenericArguments()[0];

            MethodInfo converter;
            if (monoElementType.IsValueType)
            {
                Type cppElementType = cppType.GetGenericArguments()[0];
                converter = ((Func<List<int>, ILCollections.List<int>>)ListToCppStruct<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);
            }
            else
            {
                converter = ((Func<List<int>, ILCollections.List<int>>)ListToCppRef)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);
            }

            return Expression.Call(converter, list);
        }

        private static ILCollections.List<TCpp> ListToCppStruct<TMono, TCpp>(List<TMono> list)
            where TMono : unmanaged
            where TCpp : unmanaged
        {
            ILCollections.List<TCpp> res = new ILCollections.List<TCpp>(list.Count);

            TMono monoVal = default;
            TCpp* ptr = (TCpp*)&monoVal;
            for (int i = 0; i < list.Count; i++)
            {
                monoVal = list[i];
                res.Add(*ptr);
            }

            return res;
        }

        private static ILCollections.List<T> ListToCppRef<T>(List<T> list)
        {
            ILCollections.List<T> res = new ILCollections.List<T>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                res.Add(list[i]);
            }

            return res;
        }

        public static Expression ListToManaged(Expression list, Type fieldType)
        {
            Type monoElementType = fieldType.GetGenericArguments()[0];
            Type cppElementType = list.Type.GetGenericArguments()[0];
            MethodInfo converter;

            if (monoElementType.IsValueType)
            {
                converter = ((Func<ILCollections.List<int>, List<int>>)ListToManagedStruct<int, int>)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);
            }
            else
            {
                if (monoElementType != cppElementType)
                {
                    throw new ArgumentException($"Mono type != Cpp type for element in ref list!");
                }

                converter = converter = ((Func<ILCollections.List<int>, List<int>>)ListToManagedRef)
                    .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);
            }

            return Expression.Call(converter, list);
        }

        private static List<TMono> ListToManagedStruct<TMono, TCpp>(ILCollections.List<TCpp> list)
            where TMono : unmanaged
            where TCpp : unmanaged
        {
            var res = new List<TMono>(list.Count);

            TCpp cppVal = default;
            TMono* ptr = (TMono*)&cppVal;

            for (int i = 0; i < list.Count; i++)
            {
                cppVal = list[i];
                res.Add(*ptr);
            }

            return res;
        }

        private static List<T> ListToManagedRef<T>(ILCollections.List<T> list)
        {
            int count = list.Count;
            var res = new List<T>(count);

            for (int i = 0; i < count; i++)
            {
                res.Add(list[i]);
            }

            return res;
        }
    }
}