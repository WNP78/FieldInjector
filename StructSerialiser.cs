using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;
using UnityEngine;
using static FieldInjector.Util;
using static MelonLoader.MelonLogger;
using static UnhollowerBaseLib.IL2CPP;

namespace FieldInjector
{
    internal class StructSerialiser<T> : IStructSerialiser where T : struct
    {
        private struct BlitField
        {
            public FieldInfo field;
            public int length;
            public int offset;
        }

        private struct PointerMarshalField
        {
            public FieldInfo field;
            public SerialisedField marshaller;
            public int offset;
        }

        private struct SubStructField
        {
            public FieldInfo field;
            public int length;
            public IStructSerialiser serialiser;
            public int offset;
        }

        public static StructSerialiser<T> Cache;

        public static StructSerialiser<T> Instance
        {
            get
            {
                if (Cache == null) Cache = new StructSerialiser<T>();
                return Cache;
            }
        }

        private delegate void DeserialiseDelegate(ref T obj, IntPtr src);

        public Delegate MarshalFunction => this.GetMarshalFunction();

        public Type Type => this.t;

        private Type t;
        private BlitField[] blitFields;
        private PointerMarshalField[] pointerMarshalFields;
        private SubStructField[] subStructFields;
        private bool offsetsSet = false;
        private DeserialiseDelegate marshalFunction;

        public bool IsBlittable { get; private set; }

        private StructSerialiser()
        {
            Cache = this;
            Type t = typeof(T);
            this.t = t;

            List<BlitField> blitFields = new List<BlitField>();
            List<SubStructField> subStructFields = new List<SubStructField>();
            List<PointerMarshalField> pointerMarshalFields = new List<PointerMarshalField>();
            
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var ft = field.FieldType;
                if (field.IsNotSerialized) { continue; }

                if (ft.IsPrimitive || ft.IsEnum)
                {
                    if (ft.IsEnum) ft = ft.GetEnumUnderlyingType();
                    blitFields.Add(new BlitField()
                    {
                        field = field,
                        length = Marshal.SizeOf(ft),
                    });
                }
                else if (ft.IsValueType)
                {
                    var ser = GetSerialiser(ft);
                    if (ser.IsBlittable)
                    {
                        blitFields.Add(new BlitField()
                        {
                            field = field,
                            length = Marshal.SizeOf(ft),
                        });
                    }
                    else
                    {
                        subStructFields.Add(new SubStructField()
                        {
                            field = field,
                            length = Marshal.SizeOf(ft),
                            serialiser = ser,
                        });
                    }
                }
                else
                {
                    try
                    {
                        pointerMarshalFields.Add(new PointerMarshalField()
                        {
                            field = field,
                            marshaller = SerialisedField.InferFromField(field),
                        });
                    }
                    catch (Exception ex)
                    {
                        Warning($"Not serialising struct field {field.FieldType.Name} {field.Name} on struct {t.Name} due to error:\n{ex}");
                    }
                }
            }

            this.IsBlittable = blitFields.Count == fields.Length;
            this.blitFields = blitFields.ToArray();
            if (!this.IsBlittable)
            {
                this.pointerMarshalFields = pointerMarshalFields.ToArray();
                this.subStructFields = subStructFields.ToArray();
            }
            else
            {
                this.pointerMarshalFields = new PointerMarshalField[0];
                this.subStructFields = new SubStructField[0];
            }
        }

        private unsafe static IntPtr GetPtrValue(IntPtr pp)
        {
            return *(IntPtr*)pp;
        }

        private DeserialiseDelegate GetMarshalFunction()
        {
            if (!this.offsetsSet) { throw new NotSupportedException($"fields haven't been written yet for {typeof(T)}"); }
            if (this.marshalFunction != null) return this.marshalFunction;

            // generate marshalFunction
            if (this.IsBlittable)
            {
                this.marshalFunction = BlitHere;
            }
            else
            {
                List<Expression> expressions = new List<Expression>();
                var tgt = Expression.Parameter(typeof(T).MakeByRefType(), "tgt");
                var inPtr = Expression.Parameter(typeof(IntPtr), "inPtr");
                //expressions.Add(Expression.Assign(tgt, Expression.Default(typeof(T)))); // initialise variable
                void LogExpr(string txt)
                {
                    expressions.Add(
                        Expression.Call(
                            ((Action<string>)Msg).Method, Expression.Constant(txt)));
                }

                void LogExprAdd(string txt, Expression expr)
                {
                    expressions.Add(
                        Expression.Call(
                            ((Action<string>)Msg).Method, Expression.Call(((Func<string, string, string>)string.Concat).Method, Expression.Constant(txt), Expression.Call(expr, ((Func<string>)new object().ToString).Method, null))));
                }

                foreach (var bf in this.blitFields)
                {
                    //LogExpr($"Deserialise {bf.field.FieldType.Name} {bf.field.Name}");
                    // tgt.field = Marshal.PtrToStructure<T>(inPtr + offset);
                    var mm = ((Func<IntPtr, float>)BlitHere<float>).Method.GetGenericMethodDefinition().MakeGenericMethod(bf.field.FieldType);

                    expressions.Add(
                        Expression.Assign(
                            Expression.Field(tgt, bf.field),
                            Expression.Call(
                                mm,
                                Expression.Add(inPtr, Expression.Constant(bf.offset)))));

                    //LogExprAdd("Result: ", Expression.Field(tgt, bf.field));
                }

                foreach (var pf in this.pointerMarshalFields)
                {
                    //LogExpr($"Deserialise {pf.field.FieldType.Name} {pf.field.Name}");
                    //LogExprAdd($"field offset is {pf.offset}, field location is ", Expression.Add(inPtr, Expression.Constant(pf.offset)));
                    // tgt.field = NativeToManaged(inPtr + offset);
                    Expression fv = Expression.Call(((Func<IntPtr, IntPtr>)GetPtrValue).Method, Expression.Add(inPtr, Expression.Constant(pf.offset)));
                    //LogExprAdd("field value is ", fv);

                    expressions.Add(
                        Expression.Assign(
                            Expression.Field(tgt, pf.field),
                            Expression.Condition(
                                Expression.Equal(fv, Expression.Constant(IntPtr.Zero)),
                                Expression.Default(pf.field.FieldType),
                                pf.marshaller.GetNativeToManagedExpression(fv))));

                    if (pf.field.Name == "objectList") { Msg(DumpExpressionTree(expressions[expressions.Count - 1])); }
                    //LogExprAdd("Result: ", Expression.Call(Expression.Field(tgt, pf.field), ((
                }

                foreach (var sf in this.subStructFields)
                {
                    //LogExpr($"Deserialise {sf.field.FieldType.Name} {sf.field.Name}");
                    // fieldType.MarshalFunction(ref tgt.field)

                    expressions.Add(
                        Expression.Invoke(
                            Expression.Constant(GetSerialiser(sf.field.FieldType).MarshalFunction),
                            Expression.Field(tgt, sf.field),
                            Expression.Add(inPtr, Expression.Constant(sf.offset))));
                    //LogExprAdd("Result: ", Expression.Field(tgt, sf.field));
                }

                this.marshalFunction = Expression.Lambda<DeserialiseDelegate>(Expression.Block(expressions), tgt, inPtr).Compile();
            }

            return this.marshalFunction;
        }

        public unsafe void WriteFields(MyIl2CppClass* klass)
        {
            int fieldCount = this.blitFields.Length + this.pointerMarshalFields.Length + this.subStructFields.Length;
            int offset = (int)klass->actualSize;
            int initialOffset = offset;
            int fieldsAdded = 0;

            if (fieldCount > 0)
            {
                MyIl2CppFieldInfo* fields = (MyIl2CppFieldInfo*)Marshal.AllocHGlobal(sizeof(MyIl2CppFieldInfo) * fieldCount);

                IntPtr AddField(string name, IntPtr type, int length, out int structOffset, int align = 0)
                {
                    var newType = (MyIl2CppType*)Marshal.AllocHGlobal(Marshal.SizeOf<MyIl2CppType>());
                    *newType = *(MyIl2CppType*)type;
                    newType->attrs = (ushort)Il2CppSystem.Reflection.FieldAttributes.Public;

                    offset = AlignTo(offset, align);

                    var destPtr = fields + fieldsAdded++;
                    *destPtr = new MyIl2CppFieldInfo()
                    {
                        name = Marshal.StringToHGlobalAnsi(name),
                        offset = offset,
                        parent = (Il2CppClass*)klass,
                        type = (Il2CppTypeStruct*)newType,
                    };

                    structOffset = offset - initialOffset;

                    offset += length;

                    return (IntPtr)destPtr;
                }

                for (int i = 0; i < this.blitFields.Length; i++)
                {
                    ref var bf = ref this.blitFields[i];
                    var type = bf.field.FieldType;
                    if (type.IsEnum) type = type.GetEnumUnderlyingType();
                    var fieldClass = GetClassPointerForType(type);
                    var fieldType = il2cpp_class_get_type(fieldClass);

                    AddField(bf.field.Name, fieldType, bf.length, out bf.offset);
                }

                for (int i = 0; i < this.subStructFields.Length; i++)
                {
                    ref var ss = ref this.subStructFields[i];
                    var type = ss.field.FieldType;
                    var fieldClass = GetClassPointerForType(type);
                    var fieldType = il2cpp_class_get_type(fieldClass);

                    AddField(ss.field.Name, fieldType, ss.length, out ss.offset);
                }

                for (int i = 0; i < this.pointerMarshalFields.Length; i++)
                {
                    ref var pf = ref this.pointerMarshalFields[i];
                    var size = pf.marshaller.GetFieldSize(out var align);
                    pf.marshaller.NativeField = AddField(pf.field.Name, pf.marshaller.FieldType, size, out pf.offset, align);
                }

                klass->fields = fields;
            }
            else
            {
                klass->fields = (MyIl2CppFieldInfo*)IntPtr.Zero;
            }

            this.offsetsSet = true;

            int addedSize = offset - initialOffset;
            klass->field_count = (ushort)fieldsAdded;
            klass->actualSize += (uint)addedSize;
            klass->instance_size += (uint)addedSize;
            klass->native_size += addedSize;
            klass->bitfield |= MyIl2CppClass.ClassFlags.size_inited;
        }

        public static IStructSerialiser GetSerialiser(Type t1)
        {
            return (IStructSerialiser)typeof(StructSerialiser<>).MakeGenericType(t1).GetProperty("Instance").GetValue(null);
        }

        public Expression GenerateDeserialiser(Expression nativeStructPtr)
        {
            var res = Expression.Variable(typeof(T), "res");
            var block = Expression.Block(new ParameterExpression[] { res },
                Expression.Invoke(Expression.Constant(this.GetMarshalFunction()),
                res, nativeStructPtr),
                res);

            return block;
        }

        public Expression GenerateSerialiser(Expression managedStruct, Expression targetPtr)
        {
            if (!this.offsetsSet) { throw new NotSupportedException("fields haven't been written yet"); }

            if (this.IsBlittable)
            {
                MethodInfo blit = typeof(StructSerialiser<T>).GetMethod("BlitThere", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(this.t);
                return Expression.Call(blit, managedStruct, targetPtr);
            }

            var instrs = new List<Expression>();

            foreach (var field in this.blitFields)
            {
                MethodInfo blit = typeof(StructSerialiser<T>).GetMethod("BlitThere", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(field.field.FieldType);
                instrs.Add(Expression.Call(blit, Expression.Field(managedStruct, field.field), Expression.Add(targetPtr, Expression.Constant(field.offset))));
            }

            foreach (var field in this.subStructFields)
            {
                var serialiser = field.serialiser.GenerateSerialiser(
                    Expression.Field(managedStruct, field.field),
                    Expression.Add(targetPtr, Expression.Constant(field.offset)));

                instrs.Add(serialiser);
            }

            if (this.pointerMarshalFields.Length > 0)
            {
                MethodInfo assignPtr = typeof(StructSerialiser<T>).GetMethod("AssignPtr", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var field in this.pointerMarshalFields)
                {
                    var target = Expression.Add(targetPtr, Expression.Constant(field.offset));
                    var resultPtrExpression = field.marshaller.GetManagedToNativeExpression(Expression.Field(managedStruct, field.field));
                    instrs.Add(Expression.Call(assignPtr, target, resultPtrExpression));
                }
            }

            return Expression.Block(instrs);
        }

        private static unsafe void AssignPtr(IntPtr target, IntPtr value)
        {
            *(IntPtr*)target = value;
        }

        private static unsafe void BlitThere<T2>(T2 value, IntPtr destObj) where T2 : unmanaged
        {
            *(T2*)destObj = value;
        }

        private static unsafe T2 BlitHere<T2>(IntPtr ptr) where T2 : unmanaged
        {
            return *(T2*)ptr;
        }

        private static unsafe void BlitHere(ref T me, IntPtr src)
        {
            me = *(T*)src;
        }
    }
}