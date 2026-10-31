using System.Runtime.CompilerServices;

// Tests exercise the compiled internal delivery kernel; no production SDK
// message can obtain these host-only capabilities or access a native object.
[assembly: InternalsVisibleTo("AutumnOS.Tests")]
