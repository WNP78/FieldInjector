using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;
using UnhollowerBaseLib.Runtime.VersionSpecific.FieldInfo;
using static UnhollowerBaseLib.IL2CPP;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;
using ILCollections = Il2CppSystem.Collections.Generic;

namespace FieldInjector
{
    /// <summary>
    /// Abstract base for a serialised field of any type.
    /// </summary>
    internal abstract unsafe class SerialisedField
    {
        private FieldInfo field;

        protected abstract IntPtr fieldType { get; }

        public IntPtr NativeField { get; set; }

        public FieldInfo ManagedField => this.field;

        protected virtual Type targetType => this.field.FieldType;

        protected SerialisedField(FieldInfo field)
        {
            this.field = field;
        }

        protected abstract Expression GetNativeToMonoExpression(Expression nativePtr);

        protected abstract Expression GetMonoToNativeExpression(Expression monoObj);

        /// <summary>
        /// Gets the deserialise expression.
        /// </summary>
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
                throw new InvalidOperationException("NativeField was a null pointer!");
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
            *typePtr = *(MyIl2CppType*)this.fieldType;

            typePtr->attrs = (ushort)Il2CppSystem.Reflection.FieldAttributes.Public;

            infoOut.Type = (Il2CppTypeStruct*)typePtr;

            var size = this.GetFieldSize(out int align);
            offset = Util.AlignTo(offset, align);

            infoOut.Offset = offset;

            offset += size;
        }

        public override string ToString()
        {
            return this.GetType().Name + ":" + this.targetType;
        }

        private class ObjectField : SerialisedField
        {
            protected override IntPtr fieldType => il2cpp_class_get_type(Util.GetClassPointerForType(this.targetType));

            public override int GetFieldSize(out int align)
            {
                align = sizeof(IntPtr);
                return sizeof(IntPtr);
            }

            public ObjectField(FieldInfo field) : base(field) { }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                // Expression:
                // return new T(nativePtr);
                var ctor = this.targetType.GetConstructor(new Type[] { typeof(IntPtr) });
                return Expression.New(ctor, nativePtr);
            }

            protected override Expression GetMonoToNativeExpression(Expression monoObj)
            {
                // Expression:
                // return monoObj.Pointer;
                var prop = typeof(Il2CppSystem.Object).GetProperty("Pointer");
                return Expression.Property(monoObj, prop);
            }
        }

        private class StringField : SerialisedField
        {
            protected override IntPtr fieldType => il2cpp_class_get_type(Il2CppClassPointerStore<string>.NativeClassPtr);

            public StringField(FieldInfo field) : base(field) { }

            public override int GetFieldSize(out int align)
            {
                align = sizeof(IntPtr);
                return sizeof(IntPtr);
            }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                // Expression:
                // return Il2CppStringToManaged(nativePtr);
                var method = ((Func<IntPtr, string>)Il2CppStringToManaged).Method;
                return Expression.Call(method, nativePtr);
            }

            protected override Expression GetMonoToNativeExpression(Expression monoObj)
            {
                // Expression:
                // return ManagedStringToIl2Cpp(monoObj);
                var method = ((Func<string, IntPtr>)ManagedStringToIl2Cpp).Method;
                return Expression.Call(method, monoObj);
            }
        }

        private class StructField : SerialisedField
        {
            public StructField(FieldInfo field) : base(field)
            {
                if (base.targetType.IsEnum)
                {
                    this._serialisedType = base.targetType.GetEnumUnderlyingType();
                }
                else
                {
                    this._serialisedType = base.targetType;
                }

                this.fieldClass = Util.GetClassPointerForType(this.targetType);
                this._fieldType = il2cpp_class_get_type(this.fieldClass);
                this.tempPtr = Expression.Parameter(typeof(IntPtr), "tempStorage");
            }

            private IntPtr fieldClass;

            private IntPtr _fieldType;

            private ParameterExpression tempPtr;

            private Type _serialisedType;

            protected override IntPtr fieldType => this._fieldType;

            protected override Type targetType => this._serialisedType;

            public unsafe override int GetFieldSize(out int align)
            {
                align = 0;
                return (int)(Wrap((Il2CppClass*)this.fieldClass).ActualSize - Marshal.SizeOf<Il2CppObject>());
            }

            public unsafe static T GetValue<T>(IntPtr obj, IntPtr field) where T : unmanaged
            {
                MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;
                void* dest = (char*)obj + fieldInfo->offset;

                return *(T*)dest;
            }

            protected override Expression GetMonoToNativeExpression(Expression monoValue)
            {
                // This isn't used since GetSerializeExpression is manually overriden here
                throw new InvalidOperationException();
            }

            /// <summary>
            /// Copies the data in value to the field on the Il2Cpp object.
            /// </summary>
            private unsafe static void SetValue<T>(T value, IntPtr obj, IntPtr field) where T : unmanaged
            {
                MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;

                void* dest = (byte*)obj + fieldInfo->offset;
                *(T*)dest = value;
            }

            public override IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
            {
                // Expression: SetValue(monoValue, nativePtr, NativeField);
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
                //yield break;
            }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                // for normal struct types
                // new Il2CppSystem.Object(ptr).Unbox<T>()

                // Il2Cpp struct
                var ctor = typeof(Il2CppSystem.Object).GetConstructor(new Type[] { typeof(IntPtr) });
                var unbox = typeof(Il2CppSystem.Object).GetMethod("Unbox")
                                .MakeGenericMethod(this.targetType);

                Expression res = Expression.Call(Expression.New(ctor, nativePtr), unbox);
                
                if (res.Type != this.field.FieldType)
                {
                    res = Expression.Convert(res, this.field.FieldType);
                }
                return res;
            }
        }

        private class ArrayField : SerialisedField
        {
            protected IntPtr _fieldType;

            protected Type _proxyType;

            public override int GetFieldSize(out int align)
            {
                align = sizeof(IntPtr);
                return sizeof(IntPtr);
            }

            protected ArrayField (Type elementType, FieldInfo field) : base(field)
            {
                if (elementType.IsEnum)
                {
                    elementType = elementType.GetEnumUnderlyingType();
                }

                this._proxyType = typeof(ILCollections.List<>).MakeGenericType(elementType);
                var classPtr = Util.GetClassPointerForType(this._proxyType);
                this._fieldType = il2cpp_class_get_type(classPtr);
            }

            public ArrayField(FieldInfo field) : this(field.FieldType.GetElementType(), field) { }

            protected override IntPtr fieldType => this._fieldType;

            protected override Expression GetMonoToNativeExpression(Expression monoObj)
            {
                return ConvertArrayToIl2CppList(monoObj, this._proxyType);
            }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
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
                    MethodInfo convertGeneralList = ((Func<ILCollections.List<int>, int[]>)ConvertGeneralList<int>)
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
                    MethodInfo convertGeneralArray = ((Func<int[], ILCollections.List<int>>)ConvertGeneralArray<int>)
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
                for (int i = 0;i < res.Length; i++)
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

        private class ListField : ArrayField
        {
            public ListField(FieldInfo field) : base(field.FieldType.GetGenericArguments()[0], field)
            {
            }

            protected override Expression GetMonoToNativeExpression(Expression monoObj)
            {
                return Expression.Property(ListToCpp(monoObj, this._proxyType), "Pointer");
            }

            protected override Expression GetNativeToMonoExpression(Expression nativePtr)
            {
                var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
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
                    converter = ((Func<List<int>, ILCollections.List<int>>)ListToCppRef<int>)
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

                    converter = converter = ((Func<ILCollections.List<int>, List<int>>)ListToManagedRef<int>)
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
}