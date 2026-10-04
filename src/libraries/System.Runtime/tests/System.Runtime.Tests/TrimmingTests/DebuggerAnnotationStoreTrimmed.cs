// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;

/// <summary>
/// Ensures setting DebuggerSupport = false lets the trimmer remove the store behind Debugger.Annotate,
/// because Annotate returns before it reaches the store when debugging is not supported.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        // Ensure Annotate is kept.
        Debugger.Annotate("annotation", new InvalidOperationException());

        // The argument checks still run.
        try
        {
            Debugger.Annotate(null!, new InvalidOperationException());
            return -1;
        }
        catch (ArgumentNullException)
        {
        }

        Assembly coreLib = typeof(object).Assembly;

        // The lookup finds a type the app keeps, so a null result below means the type is gone.
        if (GetDiagnosticsType(coreLib, "Debugger") is null)
            return -2;

        if (GetDiagnosticsType(coreLib, "DebuggerAnnotationStore") is not null)
            return -3;

        return 100;
    }

    // The name is built at run time, so that the trimmer cannot see which type is looked up and keep it.
    static Type? GetDiagnosticsType(Assembly assembly, string name) => assembly.GetType("System.Diagnostics." + name);
}
