using Microsoft.Azure.Functions.Worker;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Functions.Extensions;

public static class FunctionContextExtensions
{
    internal static MethodInfo GetTargetFunctionMethod(this FunctionContext context)
    {
        var entryPoint = context.FunctionDefinition.EntryPoint;

        var assemblyPath = context.FunctionDefinition.PathToAssembly;
        var assembly = Assembly.LoadFrom(assemblyPath);
        var typeName = entryPoint.Substring(0, entryPoint.LastIndexOf('.'));
        var type = assembly.GetType(typeName);
        var methodName = entryPoint.Substring(entryPoint.LastIndexOf('.') + 1);
        var method = type.GetMethod(methodName);
        return method;
    }
}