using System;
using System.Runtime.InteropServices;
using UnhollowerBaseLib.Runtime;

namespace FieldInjector
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MyIl2CppFieldInfo
    {
        public IntPtr name; // const char*
        public Il2CppTypeStruct* type; // const
        public Il2CppClass* parent; // non-const?
        public int offset; // If offset is -1, then it's thread static
        public uint token;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MyIl2CppClass
    {
        // The following fields are always valid for a Il2CppClass structure
        public Il2CppImage* image; // const
        public IntPtr gc_desc;
        public IntPtr name; // const char*
        public IntPtr namespaze; // const char*
        public MyIl2CppType byval_arg; // not const, no ptr
        public MyIl2CppType this_arg; // not const, no ptr
        public Il2CppClass* element_class; // not const
        public Il2CppClass* castClass; // not const
        public Il2CppClass* declaringType; // not const
        public Il2CppClass* parent; // not const
        public /*Il2CppGenericClass**/ IntPtr generic_class;

        public /*Il2CppTypeDefinition**/
            IntPtr typeDefinition; // const; non-NULL for Il2CppClass's constructed from type defintions

        public /*Il2CppInteropData**/ IntPtr interopData; // const

        public Il2CppClass* klass; // not const; hack to pretend we are a MonoVTable. Points to ourself
        // End always valid fields

        // The following fields need initialized before access. This can be done per field or as an aggregate via a call to Class::Init
        public MyIl2CppFieldInfo* fields; // Initialized in SetupFields
        public Il2CppEventInfo* events; // const; Initialized in SetupEvents
        public Il2CppPropertyInfo* properties; // const; Initialized in SetupProperties
        public Il2CppMethodInfo** methods; // const; Initialized in SetupMethods
        public Il2CppClass** nestedTypes; // not const; Initialized in SetupNestedTypes
        public Il2CppClass** implementedInterfaces; // not const; Initialized in SetupInterfaces
        public Il2CppRuntimeInterfaceOffsetPair* interfaceOffsets; // not const; Initialized in Init
        public IntPtr static_fields; // not const; Initialized in Init

        public /*Il2CppRGCTXData**/ IntPtr rgctx_data; // const; Initialized in Init

        // used for fast parent checks
        public Il2CppClass** typeHierarchy; // not const; Initialized in SetupTypeHierachy
        // End initialization required fields

        public IntPtr unity_user_data;

        public uint initializationExceptionGCHandle;

        public uint cctor_started;

        public uint cctor_finished;

        /*ALIGN_TYPE(8)*/
        private ulong cctor_thread;

        // Remaining fields are always valid except where noted
        public /*GenericContainerIndex*/ IntPtr genericContainerIndex;
        public uint instance_size;
        public uint actualSize;
        public uint element_size;
        public int native_size;
        public uint static_fields_size;
        public uint thread_static_fields_size;
        public int thread_static_fields_offset;
        public Il2CppClassAttributes flags;
        public uint token;

        public ushort method_count; // lazily calculated for arrays, i.e. when rank > 0
        public ushort property_count;
        public ushort field_count;
        public ushort event_count;
        public ushort nested_type_count;
        public ushort vtable_count; // lazily calculated for arrays, i.e. when rank > 0
        public ushort interfaces_count;
        public ushort interface_offsets_count; // lazily calculated for arrays, i.e. when rank > 0

        public byte typeHierarchyDepth; // Initialized in SetupTypeHierachy
        public byte genericRecursionDepth;
        public byte rank;
        public byte minimumAlignment; // Alignment of this type
        public byte naturalAligment; // Alignment of this type without accounting for packing
        public byte packingSize;

        // this is critical for performance of Class::InitFromCodegen. Equals to initialized && !has_initialization_error at all times.
        // Use Class::UpdateInitializedAndNoError to update
        public byte bitfield_1;
        /*uint8_t initialized_and_no_error : 1;

        uint8_t valuetype : 1;
        uint8_t initialized : 1;
        uint8_t enumtype : 1;
        uint8_t is_generic : 1;
        uint8_t has_references : 1;
        uint8_t init_pending : 1;
        uint8_t size_inited : 1;*/

        public byte bitfield_2;
        /*uint8_t has_finalize : 1;
        uint8_t has_cctor : 1;
        uint8_t is_blittable : 1;
        uint8_t is_import_or_windows_runtime : 1;
        uint8_t is_vtable_initialized : 1;
        uint8_t has_initialization_error : 1;*/

        //VirtualInvokeData vtable[IL2CPP_ZERO_LEN_ARRAY];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MyIl2CppType
    {
        /*union
        {
            // We have this dummy field first because pre C99 compilers (MSVC) can only initializer the first value in a union.
            void* dummy;
            TypeDefinitionIndex klassIndex; /* for VALUETYPE and CLASS #1#
            const Il2CppType *type;   /* for PTR and SZARRAY #1#
            Il2CppArrayType *array; /* for ARRAY #1#
            //MonoMethodSignature *method;
            GenericParameterIndex genericParameterIndex; /* for VAR and MVAR #1#
            Il2CppGenericClass *generic_class; /* for GENERICINST #1#
        } data;*/
        public IntPtr data;

        public ushort attrs;
        public Il2CppTypeEnum type;
        public byte mods_byref_pin;
        /*unsigned int attrs    : 16; /* param attributes or field flags #1#
        Il2CppTypeEnum type     : 8;
        unsigned int num_mods : 5;  /* max 32 modifiers follow at the end #1#
        unsigned int byref    : 1;
        unsigned int pinned   : 1;  /* valid when included in a local var signature #1#*/
        //MonoCustomMod modifiers [MONO_ZERO_LEN_ARRAY]; /* this may grow */
        public bool IsByRef
        {
            get
            {
                return (mods_byref_pin >> 5 & 1) == 1;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MyIl2CppParameterInfo
    {
        public IntPtr name; // const char*
        public int position;
        public uint token;
        public Il2CppTypeStruct* parameter_type; // const
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MyMethodInfo
    {
        public IntPtr methodPointer; // Il2CppMethodPointer
        public IntPtr virtualMethodPointer; // Il2CppMethodPointer
        public IntPtr invoker_method; // InvokerMethod
        public IntPtr name; // const char*
        public MyIl2CppClass* klass;
        public MyIl2CppType* return_type;
        public MyIl2CppType** parameters;

        //union
        //{
        //    const Il2CppRGCTXData* rgctx_data; /* is_inflated is true and is_generic is false, i.e. a generic instance method */
        //    Il2CppMetadataMethodDefinitionHandle methodMetadataHandle;
        //};
        public IntPtr rgctx_data; // just making it an intptr since that's the max size

        /* note, when is_generic == true and is_inflated == true the method represents an uninflated generic method on an inflated type. */
        //union
        //{
        //    const Il2CppGenericMethod* genericMethod; /* is_inflated is true */
        //    Il2CppMetadataGenericContainerHandle genericContainerHandle; /* is_inflated is false and is_generic is true */
        //};
        public IntPtr genericMethod;

        public uint token;
        public ushort flags;
        public ushort iflags;
        public ushort slot;
        public byte parameters_count;
        //uint8_t is_generic : 1; /* true if method is a generic method definition */
        //uint8_t is_inflated : 1; /* true if declaring_type is a generic instance or if method is a generic instance*/
        //uint8_t wrapper_type : 1; /* always zero (MONO_WRAPPER_NONE) needed for the debugger */
        //uint8_t has_full_generic_sharing_signature : 1;
        //uint8_t indirect_call_via_invokers : 1;
        public byte bitfield;
    }
}