using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;
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
            public int offset;
            public int length;
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
            public int offset;
            public int length;
            public IStructSerialiser serialiser;
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
                var offset = (int)Marshal.OffsetOf(ft, field.Name);

                if (ft.IsPrimitive)
                {
                    blitFields.Add(new BlitField()
                    {
                        field = field,
                        offset = offset,
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
                            offset = offset,
                            length = Marshal.SizeOf(ft),
                        });
                    }
                    else
                    {
                        subStructFields.Add(new SubStructField()
                        {
                            field = field,
                            offset = offset,
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
                            offset = offset,
                            marshaller = SerialisedField.InferFromField(field),
                        });
                    }
                    catch (Exception ex)
                    {
                        Warning($"Not serialising struct field {field.FieldType.Name} {field.Name} on struct {t.Name} due to error:\n{ex}");
                    }
                }
            }

            this.IsBlittable = subStructFields.Count == 0 && pointerMarshalFields.Count == 0;
            this.blitFields = blitFields.ToArray();
            if (!this.IsBlittable)
            {
                this.pointerMarshalFields = pointerMarshalFields.ToArray();
                this.subStructFields = subStructFields.ToArray();
            }
        }

        private DeserialiseDelegate GetMarshalFunction()
        {
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

                foreach (var bf in this.blitFields)
                {
                    // tgt.field = Marshal.PtrToStructure<T>(inPtr + offset);

                    expressions.Add(
                        Expression.Assign(
                            Expression.Field(tgt, bf.field),
                            Expression.Call(
                                ((Func<IntPtr, T>)Marshal.PtrToStructure<T>).Method,
                                Expression.Add(inPtr, Expression.Constant(bf.offset)))));
                }

                foreach (var pf in this.pointerMarshalFields)
                {
                    // tgt.field = NativeToManaged(inPtr + offset);

                    expressions.Add(
                        Expression.Assign(
                            Expression.Field(tgt, pf.field),
                            pf.marshaller.GetNativeToManagedExpression(
                                Expression.Add(inPtr, Expression.Constant(pf.offset)))));
                }

                foreach (var sf in this.subStructFields)
                {
                    // fieldType.MarshalFunction(ref tgt.field)

                    expressions.Add(
                        Expression.Invoke(
                            Expression.Constant(GetSerialiser(sf.field.FieldType).MarshalFunction),
                            Expression.Field(tgt, sf.field),
                            Expression.Add(inPtr, Expression.Constant(sf.offset))));
                }

                this.marshalFunction = Expression.Lambda<DeserialiseDelegate>(Expression.Block(expressions), tgt, inPtr).Compile();
            }

            return this.marshalFunction;
        }

        public unsafe void WriteFields(MyIl2CppClass* klass)
        {
            int fieldCount = this.blitFields.Length + this.pointerMarshalFields.Length + this.subStructFields.Length;
            MyIl2CppFieldInfo* fields = (MyIl2CppFieldInfo*)Marshal.AllocHGlobal(sizeof(MyIl2CppFieldInfo) * fieldCount);

            int offset = (int)klass->actualSize;
            int fieldsAdded = 0;

            IntPtr AddField(string name, IntPtr type, int length, int align = 0)
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

                offset += length;

                return (IntPtr)destPtr;
            }

            foreach (var bf in this.blitFields)
            {
                var type = bf.field.FieldType;
                var fieldClass = GetClassPointerForType(type);
                var fieldType = il2cpp_class_get_type(fieldClass);

                AddField(bf.field.Name, fieldType, bf.length);
            }

            foreach (var ss in this.subStructFields)
            {
                var type = ss.field.FieldType;
                var fieldClass = GetClassPointerForType(type);
                var fieldType = il2cpp_class_get_type(fieldClass);

                AddField(ss.field.Name, fieldType, ss.length);
            }

            foreach (var pf in this.pointerMarshalFields)
            {
                var size = pf.marshaller.GetFieldSize(out var align);
                pf.marshaller.NativeField = AddField(pf.field.Name, pf.marshaller.FieldType, size, align);
            }

            klass->field_count = (ushort)fieldsAdded;
            klass->fields = fields;
            klass->actualSize += (uint)offset;
            klass->instance_size += (uint)offset;
            klass->native_size += offset;
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
            if (this.IsBlittable)
            {
                MethodInfo blit = typeof(StructSerialiser<T>).GetMethod("BlitThere").MakeGenericMethod(this.t);
                return Expression.Call(blit, managedStruct, targetPtr);
            }

            var instrs = new List<Expression>();

            foreach (var field in this.blitFields)
            {
                MethodInfo blit = typeof(StructSerialiser<T>).GetMethod("BlitThere", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(field.field.FieldType);
                instrs.Add(Expression.Call(blit, managedStruct, Expression.Add(targetPtr, Expression.Constant(field.offset))));
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

        private static unsafe void BlitHere(ref T me, IntPtr src)
        {
            me = Marshal.PtrToStructure<T>(src);
        }
    }
}