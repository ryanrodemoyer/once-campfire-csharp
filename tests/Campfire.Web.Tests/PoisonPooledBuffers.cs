using System.Runtime.CompilerServices;
using Campfire.Templates;

namespace Campfire.Web.Tests;

/// <summary>
/// Every test in this project runs with <see cref="PooledBufferWriter.PoisonOnDispose"/> on, so a
/// controller that hands back a rendered body after its pooled buffer was returned fails its own
/// tests with invalid UTF-8, instead of corrupting a response under load.
/// </summary>
static class PoisonPooledBuffers
{
    [ModuleInitializer]
    internal static void TurnOn() => PooledBufferWriter.PoisonOnDispose = true;
}
