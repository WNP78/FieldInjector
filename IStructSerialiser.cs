using System;
using System.Linq.Expressions;

namespace FieldInjector
{
    internal interface IStructSerialiser
    {
        Type Type { get; }
        bool IsBlittable { get; }
        Expression GenerateSerialiser(Expression managedStruct, Expression targetPtr);
        Expression GenerateDeserialiser(Expression nativeStructPtr);
        Delegate MarshalFunction { get; }
        unsafe void WriteFields(MyIl2CppClass* klass);
    }
}