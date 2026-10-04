// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Diagnostics
{
    public static partial class Debugger
    {
        /// <summary>
        /// Represents the default category of message with a constant.
        /// </summary>
        /// <remarks>
        /// The value of this default constant is `null`. <see cref="Debugger.DefaultCategory"/>
        /// is used by <see cref="Debugger.Log"/>.
        /// </remarks>
        public static readonly string? DefaultCategory;

        /// <summary>
        /// Signals a breakpoint to an attached debugger with the <paramref name="exception"/> details
        /// if a .NET debugger is attached with break on user-unhandled exception enabled and a method
        /// attributed with DebuggerDisableUserUnhandledExceptionsAttribute calls this method.
        /// </summary>
        /// <param name="exception">The user-unhandled exception.</param>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static void BreakForUserUnhandledException(Exception exception)
        {
        }

        /// <summary>
        /// Records, for the calling thread, an annotation that describes <paramref name="exception"/>, so that an
        /// attached debugger can show it when it stops for that exception.
        /// </summary>
        /// <param name="annotation">The text to show at a debugger stop for <paramref name="exception"/>.</param>
        /// <param name="exception">The exception that <paramref name="annotation"/> describes.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="annotation"/> or <paramref name="exception"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// This method never stops the process and never sends anything to the debugger. It stores the annotation in
        /// a small per-thread buffer that a debugger reads when it stops for an exception, without running code in
        /// the process; the debugger shows the annotation only when the stop's exception is <paramref name="exception"/>.
        /// The format of the buffer is a contract with debuggers, described in docs/design/features/debugger-annotations.md.
        /// </para>
        /// <para>
        /// Nothing is recorded unless a managed debugger is attached at the moment of the call. Annotations made while
        /// no debugger is attached are dropped, not buffered. The buffer keeps the most recent annotations of each thread;
        /// annotations do not flow with <see cref="System.Threading.ExecutionContext"/>.
        /// </para>
        /// <para>
        /// The arguments are checked whether or not a debugger is attached. A caller that builds an expensive annotation
        /// can test <see cref="IsAttached"/> first.
        /// </para>
        /// </remarks>
        public static void Annotate(string annotation, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(annotation);
            ArgumentNullException.ThrowIfNull(exception);

            // Tested on its own and first, so that when the feature switch is off the trimmer removes
            // everything below, including the store type.
            if (!IsSupported)
            {
                return;
            }

            if (!IsAttached)
            {
                // Releases the entries a thread kept from an earlier debugging session.
                DebuggerAnnotationStore.ClearIfPresent();
                return;
            }

            DebuggerAnnotationStore.Record(annotation, exception);
        }

        [FeatureSwitchDefinition("System.Diagnostics.Debugger.IsSupported")]
        internal static bool IsSupported { get; } = AppContext.TryGetSwitch("System.Diagnostics.Debugger.IsSupported", out bool isSupported) ? isSupported : true;
    }
}
