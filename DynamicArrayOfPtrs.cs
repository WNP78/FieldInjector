using System;
using Logging = MelonLoader.MelonLogger;

namespace FieldInjector
{
    internal unsafe struct DynamicArrayOfPtrs
    {
        public void** m_ptr;
        public int memLabelId;
        public ulong size;
        public ulong capacity;

        public ulong Capacity
        {
            get => this.capacity >> 1;
            set => this.capacity = value << 1 | (this.capacity & 1);
        }

        public void InsertReplaceNull(IntPtr value)
        {
            for (ulong i = 0; i < this.size; i++)
            {
                if (this.m_ptr[i] == null)
                {
                    this.m_ptr[i] = (void*)value;
                    return;
                }
            }

            Logging.Error($"ERROR: scanned {this.size} images and no null pointer found, cannot insert ours! Struct injection will probably not work. Go tell WNP78 on discord and let someone know that he was wrong");
        }
    }
}