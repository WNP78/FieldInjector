namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class IsUnmanagedAttribute : Attribute
    {
        // This is a positional argument
        public IsUnmanagedAttribute()
        {
        }
    }
}