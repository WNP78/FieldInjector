using System;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;

namespace FieldInjector
{
    internal delegate void StaticVoidIntPtrDelegate(IntPtr intPtr);
        
    internal unsafe class InstanceMethodPtrInvoker
    {
        public IntPtr InvokerPtr { get; private set; }

        public InstanceMethodPtrInvoker()
        {
            var del = new InvokerDelegate(StaticVoidIntPtrInvoker);
            GCHandle.Alloc(del, GCHandleType.Normal); // prevent GC of our delegate
            InvokerPtr = Marshal.GetFunctionPointerForDelegate(del);
        }
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InvokerDelegate(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args);

        private static IntPtr StaticVoidIntPtrInvoker(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args)
        {
            Marshal.GetDelegateForFunctionPointer<StaticVoidIntPtrDelegate>(methodPointer)(obj);
            return IntPtr.Zero;
        }
    }
}