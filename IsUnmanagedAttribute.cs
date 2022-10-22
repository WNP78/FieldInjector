using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    sealed class IsUnmanagedAttribute : Attribute
    {
        // This is a positional argument
        public IsUnmanagedAttribute()
        {
        }
    }
}
