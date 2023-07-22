using System;
using System.Runtime.InteropServices;
using System.Text;
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
        public IntPtr generic_class;

        public IntPtr typeDefinition; // const; non-NULL for Il2CppClass's constructed from type defintions

        public IntPtr interopData; // const

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

        public IntPtr rgctx_data; // const; Initialized in Init

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
        public IntPtr genericContainerIndex;

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

        public ClassFlags bitfield;

        //VirtualInvokeData vtable[IL2CPP_ZERO_LEN_ARRAY];

        public static string ToStringClass(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) { return "Il2CppClass:nullptr"; }

            try
            {
                return $"Il2CppClass:{Marshal.PtrToStringAnsi(((MyIl2CppClass*)ptr)->name)} ({ptr})";
            }
            catch
            {
                return $"Il2CppClass:invalid ({ptr})";
            }
        }

        private static void PrintFields(StringBuilder s, MyIl2CppFieldInfo* ptr, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var f = ptr + i;
                s.Append($"  field:\n");
                s.Append($"    name = {Marshal.PtrToStringAnsi(f->name)}\n");
                s.Append($"    type = {((MyIl2CppType*)f->type)->ToString("    ")}\n");
                s.Append($"    parent = {ToStringClass((IntPtr)f->parent)}\n");
                s.Append($"    offset = {f->offset}\n");
                s.Append($"    token = {f->token}\n");
            }
        }

        public string Debug()
        {
            StringBuilder s = new StringBuilder($"Il2CppClass:\n");

            string str(IntPtr p)
            {
                if (p == IntPtr.Zero) { return "nullptr"; }
                return Marshal.PtrToStringAnsi(p);
            }

            string ptr(IntPtr ptr2) => $"0x{(ulong)ptr2:X}";

            s.Append($"image = {(IntPtr)this.image}\n");
            s.Append($"gc_desc = {this.gc_desc}\n");
            s.Append($"name = {str(this.name)}\n");
            s.Append($"namespaze = {str(this.namespaze)}\n");
            s.Append($"byval_arg = {this.byval_arg.ToString()}\n");
            s.Append($"this_arg = {this.this_arg.ToString()}\n");
            s.Append($"element_class = {ToStringClass((IntPtr)this.element_class)})\n");
            s.Append($"castClass = {ToStringClass((IntPtr)this.castClass)}\n");
            s.Append($"declaringType = {ToStringClass((IntPtr)this.declaringType)}\n");
            s.Append($"parent = {ToStringClass((IntPtr)this.parent)}\n");
            s.Append($"generic_class = {this.generic_class}\n");
            s.Append($"typeDefinition = {this.typeDefinition}\n");
            s.Append($"interopData = {this.interopData}\n");
            s.Append($"klass = {(IntPtr)this.klass}\n");
            s.Append($"fields = \n");
            PrintFields(s, this.fields, this.field_count);
            s.Append($"events = {(IntPtr)this.events}\n");
            s.Append($"properties = {(IntPtr)this.properties}\n");
            s.Append($"methods = {(IntPtr)this.methods}\n");
            s.Append($"nestedTypes = {(IntPtr)this.nestedTypes}\n");
            s.Append($"implementedInterfaces = {(IntPtr)this.implementedInterfaces}\n");
            s.Append($"interfaceOffsets = {(IntPtr)this.interfaceOffsets}\n");
            s.Append($"static_fields = {this.static_fields}\n");
            s.Append($"rgctx_data = {this.rgctx_data}\n");
            s.Append($"typeHierarchy = {(IntPtr)this.typeHierarchy}\n");
            s.Append($"unity_user_data = {this.unity_user_data}\n");
            s.Append($"initializationExceptionGCHandle = {this.initializationExceptionGCHandle}\n");
            s.Append($"cctor_started = {this.cctor_started}\n");
            s.Append($"cctor_finished = {this.cctor_finished}\n");
            s.Append($"genericContainerIndex = {this.genericContainerIndex}\n");
            s.Append($"instance_size = {this.instance_size}\n");
            s.Append($"actualSize = {this.actualSize}\n");
            s.Append($"element_size = {this.element_size}\n");
            s.Append($"native_size = {this.native_size}\n");
            s.Append($"static_fields_size = {this.static_fields_size}\n");
            s.Append($"thread_static_fields_size = {this.thread_static_fields_size}\n");
            s.Append($"thread_static_fields_offset = {this.thread_static_fields_offset}\n");
            s.Append($"flags = {this.flags}\n");
            s.Append($"token = {this.token}\n");
            s.Append($"method_count = {this.method_count}\n");
            s.Append($"property_count = {this.property_count}\n");
            s.Append($"field_count = {this.field_count}\n");
            s.Append($"event_count = {this.event_count}\n");
            s.Append($"nested_type_count = {this.nested_type_count}\n");
            s.Append($"vtable_count = {this.vtable_count}\n");
            s.Append($"interfaces_count = {this.interfaces_count}\n");
            s.Append($"interface_offsets_count = {this.interface_offsets_count}\n");
            s.Append($"typeHierarchyDepth = {this.typeHierarchyDepth}\n");
            s.Append($"genericRecursionDepth = {this.genericRecursionDepth}\n");
            s.Append($"rank = {this.rank}\n");
            s.Append($"minimumAlignment = {this.minimumAlignment}\n");
            s.Append($"naturalAligment = {this.naturalAligment}\n");
            s.Append($"packingSize = {this.packingSize}\n");
            s.Append($"bitfield = {this.bitfield}\n");

            s.Append($"implementedInterfaces[{this.interfaces_count}]:\n");
            for (int i = 0; i < this.interfaces_count; i++)
            {
                var iface = (MyIl2CppClass*)this.implementedInterfaces[i];
                s.Append($"  {Marshal.PtrToStringAnsi(iface->namespaze)}::{Marshal.PtrToStringAnsi(iface->name)}\n");
            }

            return s.ToString();
        }

        [Flags]
        public enum ClassFlags : ushort
        {
            None = 0,
            initialized_and_no_error = 1 << 0,
            valuetype = 1 << 1,
            initialized = 1 << 2,
            enumtype = 1 << 3,
            is_generic = 1 << 4,
            has_references = 1 << 5,
            init_pending = 1 << 6,
            size_inited = 1 << 7,
            has_finalize = 1 << 8,
            has_cctor = 1 << 9,
            is_blittable = 1 << 10,
            is_import_or_windows_runtime = 1 << 11,
            is_vtable_initialized = 1 << 12,
            has_initialization_error = 1 << 13,
        }
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
        unsigned int pinned   : 1;  /* valid when included in a local var signature #1#
        unsigned int valuetype : 1;*/
        //MonoCustomMod modifiers [MONO_ZERO_LEN_ARRAY]; /* this may grow */

        public byte num_mods
        {
            get => (byte)(this.mods_byref_pin & 0b0001_1111);
            set => this.mods_byref_pin = (byte)((value & 0b0001_1111) | (this.mods_byref_pin & ~0b0001_1111));
        }

        public bool byref
        {
            get => (this.mods_byref_pin & 0b0010_0000) != 0;
            set => this.mods_byref_pin = (byte)(value ? (this.mods_byref_pin | 0b0010_0000) : (this.mods_byref_pin & ~0b0010_0000));
        }

        public bool pinned
        {
            get => (this.mods_byref_pin & 0b0100_0000) != 0;
            set => this.mods_byref_pin = (byte)(value ? (this.mods_byref_pin | 0b0100_0000) : (this.mods_byref_pin & ~0b0100_0000));
        }

        public bool valuetype
        {
            get => (this.mods_byref_pin & 0b1000_0000) != 0;
            set => this.mods_byref_pin = (byte)(value ? (this.mods_byref_pin | 0b1000_0000) : (this.mods_byref_pin & ~0b1000_0000));
        }

        public string ToString(string indent = "")
        {
            return $"Il2CppType:\n" +
                $"{indent}  data = {this.data}\n" +
                $"{indent}  attrs = {this.attrs}\n" +
                $"{indent}  attrs (field) = {(FieldAttributes)this.attrs}\n" +
                $"{indent}  attrs (param) = {(ParamAttributes)this.attrs}\n" +
                $"{indent}  type = {this.type}\n" +
                $"{indent}  mods_byref_pin = {Convert.ToString(this.mods_byref_pin, 2).PadLeft(8, '0')}\n" +
                $"{indent}  num_mods = {this.num_mods}\n" +
                $"{indent}  byref = {this.byref}\n" +
                $"{indent}  pinned = {this.pinned}\n" +
                $"{indent}  valuetype = {this.valuetype}";
        }
    }

    [Flags]
    internal enum FieldAttributes : ushort
    {
        None = 0,
        FIELD_ACCESS_MASK = 0x0007,
        COMPILER_CONTROLLED = 0x0000,
        PRIVATE = 0x0001,
        FAM_AND_ASSEM = 0x0002,
        ASSEMBLY = 0x0003,
        FAMILY = 0x0004,
        FAM_OR_ASSEM = 0x0005,
        PUBLIC = 0x0006,
        STATIC = 0x0010,
        INIT_ONLY = 0x0020,
        LITERAL = 0x0040,
        NOT_SERIALIZED = 0x0080,
        SPECIAL_NAME = 0x0200,
        PINVOKE_IMPL = 0x2000,
        RESERVED_MASK = 0x9500,
        RT_SPECIAL_NAME = 0x0400,
        HAS_FIELD_MARSHAL = 0x1000,
        HAS_DEFAULT = 0x8000,
        HAS_FIELD_RVA = 0x0100,
    }

    [Flags]
    internal enum ParamAttributes : ushort
    {
        IN = 0x0001,
        OUT = 0x0002,
        OPTIONAL = 0x0010,
        RESERVED_MASK = 0xf000,
        HAS_DEFAULT = 0x1000,
        HAS_FIELD_MARSHAL = 0x2000,
        UNUSED = 0xcfe0,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MyIl2CppParameterInfo
    {
        public IntPtr name; // const char*
        public int position;
        public uint token;
        public Il2CppTypeStruct* parameter_type; // const
    }
}