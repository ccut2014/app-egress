using AppEgress.Native;

namespace AppEgress.Core;

internal interface IRouteResolver
{
    uint? FindInterface(UInt128 source, UInt128 destination);
}

internal sealed class SystemRouteResolver : IRouteResolver
{
    public uint? FindInterface(UInt128 source, UInt128 destination) => RouteNative.FindInterface(source, destination);
}
